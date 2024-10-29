﻿using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Shared.Engine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Lampac.Engine.Middlewares
{
    public class Accsdb
    {
        private readonly RequestDelegate _next;
        IMemoryCache memoryCache;

        public Accsdb(RequestDelegate next, IMemoryCache mem)
        {
            _next = next;
            memoryCache = mem;
        }

        public Task Invoke(HttpContext httpContext)
        {
            if (httpContext.Request.Headers.TryGetValue("localrequest", out var _localpasswd) && _localpasswd.ToString() == FileCache.ReadAllText("passwd"))
                return _next(httpContext);

            #region manifest / admin
            if (!File.Exists("module/manifest.json"))
            {
                if (httpContext.Request.Path.Value.StartsWith("/admin/manifest/install"))
                    return _next(httpContext);

                httpContext.Response.Redirect("/admin/manifest/install");
                return Task.CompletedTask;
            }

            if (httpContext.Request.Path.Value.StartsWith("/admin/"))
            {
                if (httpContext.Request.Path.Value.StartsWith("/admin/auth"))
                    return _next(httpContext);

                if (httpContext.Request.Cookies.TryGetValue("passwd", out string passwd) && passwd == FileCache.ReadAllText("passwd"))
                    return _next(httpContext);

                httpContext.Response.Redirect("/admin/auth");
                return Task.CompletedTask;
            }
            #endregion

            if (!AppInit.conf.weblog.enable && !AppInit.conf.rch.enable && httpContext.Request.Path.Value.StartsWith("/ws"))
                return httpContext.Response.WriteAsync("disabled", httpContext.RequestAborted);

            string jacpattern = "^/(api/v2.0/indexers|api/v1.0/|toloka|rutracker|rutor|torrentby|nnmclub|kinozal|bitru|selezen|megapeer|animelayer|anilibria|anifilm|toloka|lostfilm|baibako|hdrezka)";

            if (!string.IsNullOrEmpty(AppInit.conf.apikey))
            {
                if (Regex.IsMatch(httpContext.Request.Path.Value, jacpattern))
                {
                    if (AppInit.conf.apikey != httpContext.Request.Query["apikey"])
                        return Task.CompletedTask;
                }
            }

            if (AppInit.conf.accsdb.enable)
            {
                if (!string.IsNullOrEmpty(AppInit.conf.accsdb.whitepattern) && Regex.IsMatch(httpContext.Request.Path.Value, AppInit.conf.accsdb.whitepattern, RegexOptions.IgnoreCase))
                    return _next(httpContext);

                if (Regex.IsMatch(httpContext.Request.Path.Value, jacpattern))
                    return _next(httpContext);

                if (httpContext.Request.Path.Value.EndsWith("/personal.lampa"))
                    return _next(httpContext);

                if (httpContext.Request.Path.Value != "/" && !Regex.IsMatch(httpContext.Request.Path.Value, "^/((proxy-dash|ts|ws|headers|myip|version|weblog|rch/result)(/|$)|extensions|(streampay|b2pay|cryptocloud|freekassa|litecoin)/|lite/(filmixpro|fxapi/lowlevel/|kinopubpro|vokinotk)|lampa-(main|lite)/app\\.min\\.js|[a-zA-Z]+\\.js|msx/start\\.json|samsung\\.wgt)"))
                {
                    bool limitip = false;
                    HashSet<string> ips = null;
                    string account_email = httpContext.Request.Query["account_email"].ToString()?.ToLower()?.Trim() ?? string.Empty;

                    bool userfindtoaccounts = AppInit.conf.accsdb.accounts.TryGetValue(account_email, out DateTime ex);
                    string uri = httpContext.Request.Path.Value+httpContext.Request.QueryString.Value;

                    if (string.IsNullOrWhiteSpace(account_email) || !userfindtoaccounts || DateTime.UtcNow > ex || IsLockHostOrUser(account_email, httpContext.Connection.RemoteIpAddress.ToString(), uri, out limitip, out ips))
                    {
                        if (Regex.IsMatch(httpContext.Request.Path.Value, "^/(proxy/|proxyimg)"))
                        {
                            string href = Regex.Replace(httpContext.Request.Path.Value, "^/(proxy|proxyimg([^/]+)?)/", "") + httpContext.Request.QueryString.Value;

                            if (href.Contains(".themoviedb.org") || href.Contains(".tmdb.org") || href.StartsWith("http"))
                            {
                                httpContext.Response.Redirect(href);
                                return Task.CompletedTask;
                            }
                        }

                        if (Regex.IsMatch(httpContext.Request.Path.Value, "\\.(js|css|ico|png|svg|jpe?g|woff|webmanifest)"))
                        {
                            httpContext.Response.StatusCode = 404;
                            httpContext.Response.ContentType = "application/octet-stream";
                            return Task.CompletedTask;
                        }

                        string msg = limitip ? $"Превышено допустимое количество ip/запросов на аккаунт." : // Разбан через {60 - DateTime.Now.Minute} мин.\n{string.Join(", ", ips)}
                                     string.IsNullOrWhiteSpace(account_email) ? AppInit.conf.accsdb.authMesage :
                                     userfindtoaccounts ? AppInit.conf.accsdb.expiresMesage.Replace("{account_email}", account_email).Replace("{expires}", ex.ToString("dd.MM.yyyy")) :
                                     AppInit.conf.accsdb.denyMesage.Replace("{account_email}", account_email);

                        httpContext.Response.ContentType = "application/javascript; charset=utf-8";
                        return httpContext.Response.WriteAsync("{\"accsdb\":true,\"msg\":\"" + msg + "\"}", httpContext.RequestAborted);
                    }
                }
            }

            return _next(httpContext);
        }


        #region IsLock
        bool IsLockHostOrUser(string account_email, string userip, string uri, out bool islock, out HashSet<string> ips)
        {
            #region countlock_day
            int countlock_day(bool update)
            {
                string key = $"Accsdb:lock_day:{account_email}:{DateTime.Now.Day}";

                if (memoryCache.TryGetValue(key, out int countlock))
                {
                    if (update)
                    {
                        countlock = countlock + 1;
                        memoryCache.Set(key, countlock, DateTime.Now.AddDays(1));
                    }

                    return countlock;
                }
                else if (update)
                {
                    memoryCache.Set(key, 1, DateTime.Now.AddDays(1));
                    return 1;
                }

                return 0;
            }
            #endregion

            if (IsLockIpHour(account_email, userip, out islock, out ips) || IsLockReqHour(account_email, uri, out islock))
            {
                countlock_day(update: true);
                return islock;
            }

            if (countlock_day(update: false) >= AppInit.conf.accsdb.maxlock_day)
            {
                if (AppInit.conf.accsdb.blocked_hour != -1)
                    memoryCache.Set($"Accsdb:blocked_hour:{account_email}", 0, DateTime.Now.AddHours(AppInit.conf.accsdb.blocked_hour));

                islock = true;
                return islock;
            }

            if (memoryCache.TryGetValue($"Accsdb:blocked_hour:{account_email}", out _))
            {
                islock = true;
                return islock;
            }

            islock = false;
            return islock;
        }


        bool IsLockIpHour(string account_email, string userip, out bool islock, out HashSet<string> ips)
        {
            string memKeyLocIP = $"Accsdb:IsLockIpHour:{account_email}:{DateTime.Now.Hour}";

            if (memoryCache.TryGetValue(memKeyLocIP, out ips))
            {
                ips.Add(userip);
                memoryCache.Set(memKeyLocIP, ips, DateTime.Now.AddHours(1));

                if (ips.Count > AppInit.conf.accsdb.maxip_hour)
                {
                    islock = true;
                    return islock;
                }
            }
            else
            {
                ips = new HashSet<string>() { userip };
                memoryCache.Set(memKeyLocIP, ips, DateTime.Now.AddHours(1));
            }

            islock = false;
            return islock;
        }

        bool IsLockReqHour(string account_email, string uri, out bool islock)
        {
            if (Regex.IsMatch(uri, "^/(proxy/|proxyimg|lifeevents|externalids)"))
            {
                islock = false;
                return islock;
            }

            string memKeyLocIP = $"Accsdb:IsLockReqHour:{account_email}";

            if (memoryCache.TryGetValue(memKeyLocIP, out HashSet<string> urls))
            {
                urls.Add(uri);
                memoryCache.Set(memKeyLocIP, urls, DateTime.Now.AddHours(1));

                if (urls.Count > AppInit.conf.accsdb.maxrequest_hour)
                {
                    islock = true;
                    return islock;
                }
            }
            else
            {
                urls = new HashSet<string>() { uri };
                memoryCache.Set(memKeyLocIP, urls, DateTime.Now.AddHours(1));
            }

            islock = false;
            return islock;
        }
        #endregion
    }
}
