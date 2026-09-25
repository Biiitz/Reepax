using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Reepax.Services.AdBlock;

namespace Reepax.Services.Browser;

/// <summary>
/// Security guard for the embedded WebView2 browser sandbox.
/// Protects users against malicious cross-domain redirects, popups, and clickjacking traps,
/// while strictly whitelisting security challenges (Cloudflare Turnstile, reCAPTCHA, hCaptcha).
/// </summary>
public static class BrowserSecurityGuard
{
    private static readonly ConcurrentDictionary<string, byte> _whitelistedDomains = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<Regex> _whitelistedUrlPatterns = new();
    private static readonly object _lock = new();

    public static ConcurrentDictionary<string, byte> DynamicallyBlockedRedirectDomains { get; } = new(StringComparer.OrdinalIgnoreCase);

    static BrowserSecurityGuard()
    {
        InitializeWhitelists();
    }

    private static void InitializeWhitelists()
    {
        // 100% CAPTCHA & Security Challenge Protection - Never Block or Tamper
        var captchaDomains = new[]
        {
            // Cloudflare Turnstile & Challenge Platform
            "challenges.cloudflare.com",
            "turnstile.cloudflare.com",
            "static.cloudflareinsights.com",
            "cloudflareinsights.com",
            "cloudflare.com/cdn-cgi/challenge-platform",

            // Google reCAPTCHA (v2, v3, Enterprise)
            "recaptcha.net",
            "google.com/recaptcha",
            "gstatic.com/recaptcha",
            "google.com/js/bg",
            "apis.google.com/js/api.js",
            "www.google.com/recaptcha",
            "www.gstatic.com/recaptcha",

            // hCaptcha
            "hcaptcha.com",
            "js.hcaptcha.com",
            "assets.hcaptcha.com",
            "newassets.hcaptcha.com",
            "api.hcaptcha.com",
            "api2.hcaptcha.com",
            "imgs.hcaptcha.com",

            // GeeTest
            "geetest.com",
            "static.geetest.com",
            "api.geetest.com",
            "api-na.geetest.com",
            "gcaptcha4.geetest.com",

            // Arkose Labs / FunCaptcha
            "arkoselabs.com",
            "client-api.arkoselabs.com",
            "funcaptcha.com",

            // Friendly Captcha
            "friendlycaptcha.com",
            "friendlycaptcha.eu"
        };

        foreach (var cd in captchaDomains)
        {
            _whitelistedDomains[cd] = 1;
        }
    }

    /// <summary>
    /// Checks whether a URL is on the CAPTCHA / challenge whitelist or internal browser schemes.
    /// </summary>
    public static bool IsWhitelisted(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        // Exempt internal browser schemes (extensions, edge, data, blob, about)
        if (url.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("edge://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Mandatory Whitelist Fast-Path (<0.005ms)
        foreach (var white in _whitelistedDomains.Keys)
        {
            if (url.Contains(white, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        lock (_lock)
        {
            foreach (var whitePat in _whitelistedUrlPatterns)
            {
                try
                {
                    if (whitePat.IsMatch(url))
                        return true;
                }
                catch (RegexMatchTimeoutException) { }
            }
        }

        return false;
    }

    private static readonly HashSet<string> _secondLevelTlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "co.uk", "org.uk", "me.uk", "ltd.uk", "plc.uk", "net.uk",
        "com.au", "net.au", "org.au", "edu.au", "gov.au",
        "co.nz", "net.nz", "org.nz",
        "co.za", "org.za", "net.za",
        "co.jp", "ne.jp", "ac.jp", "go.jp",
        "com.br", "net.br", "org.br",
        "com.mx", "org.mx", "net.mx",
        "co.in", "net.in", "org.in", "gen.in",
        "com.tr", "org.tr", "net.tr",
        "com.ar", "org.ar", "net.ar",
        "com.pl", "org.pl", "net.pl",
        "co.kr", "ne.kr", "re.kr",
        "com.tw", "org.tw", "net.tw",
        "com.hk", "org.hk", "net.hk",
        "com.sg", "org.sg", "net.sg",
        "com.my", "org.my", "net.my",
        "com.ph", "org.ph", "net.ph",
        "co.id", "or.id", "net.id",
        "co.il", "org.il", "net.il",
        "gc.ca",
        "asso.fr",
        "asso.mc",
        "com.de",
        "com.es", "nom.es", "org.es",
        "com.pt", "org.pt",
        "com.ru", "net.ru", "org.ru", "pp.ru",
        "com.ua", "net.ua", "org.ua"
    };

    /// <summary>
    /// Determines the registrable domain (eTLD+1) of a host.
    /// </summary>
    public static string GetRegistrableDomain(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return string.Empty;

        host = host.Trim().TrimEnd('.').ToLowerInvariant();

        if (host == "localhost" || System.Net.IPAddress.TryParse(host, out _))
            return host;

        var parts = host.Split('.');
        if (parts.Length <= 2)
            return host;

        var lastTwo = parts[^2] + "." + parts[^1];
        if (parts.Length >= 3 && _secondLevelTlds.Contains(lastTwo))
            return parts[^3] + "." + lastTwo;

        return lastTwo;
    }

    public static bool AddDynamicBlockedDomain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return false;
        if (DynamicallyBlockedRedirectDomains.Count > 1000)
        {
            DynamicallyBlockedRedirectDomains.Clear();
        }
        return DynamicallyBlockedRedirectDomains.TryAdd(domain, 1);
    }

    private static readonly Regex _directDownloadRegex = new(
        @"\.(zip|rar|7z|tar|gz|bz2|xz|iso|bin|img|exe|msi|dmg|pkg|mp4|mkv|avi|mov|wmv|flv|webm|mp3|flac|wav|pdf|epub|apk|appx)(\?.*)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Checks whether a URL resembles a direct download link.
    /// </summary>
    public static bool IsDirectDownloadUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        try
        {
            var uri = new Uri(url);
            var path = uri.AbsolutePath;

            if (_directDownloadRegex.IsMatch(path))
                return true;

            var fullUrl = uri.ToString();
            if (_directDownloadRegex.IsMatch(fullUrl))
                return true;

            if (path.Contains("/dl/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/download/", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/download", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/getfile/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/d/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch { }

        return false;
    }

    private static readonly HashSet<string> _knownSafeAndHosterDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "rapidgator.net", "rg.to",
        "ddownload.com", "ddl.to",
        "mega.nz", "mega.co.nz", "mega.io",
        "1fichier.com", "alterupload.com",
        "gofile.io",
        "mediafire.com",
        "buzzheavier.com",
        "datanodes.to",
        "katfile.com",
        Reepax.Services.Extractor.FastHostResolver.CanonicalDomain,
        "pixeldrain.com",
        "drive.google.com", "docs.google.com", "google.com",
        "dropbox.com",
        "onedrive.live.com", "1drv.ms",
        "turbobit.net",
        "nitroflare.com",
        "uploadgig.com",
        "filefactory.com",
        "k2s.cc", "keep2share.cc",
        "hexupload.net",
        "alfafile.net",
        "wdupload.com",
        "mexashare.com",
        "krakenfiles.com",
        "bowfile.com",
        "userscloud.com",
        "send.cm",
        "anonfiles.com",
        "bayfiles.com",
        "terabox.com", "teraboxapp.com",
        "qiwi.gg",
        "multiup.io", "multiup.org",
        "mirrored.to",
        "filelion.to", "filelion.com",
        "streamtape.com",
        "doodstream.com", "dood.to", "dood.so", "dood.wf",
        "mixdrop.co", "mixdrop.to",
        "upstream.to",
        "voe.sx",
        "vidoza.net",
        "cloud.mail.ru",
        "disk.yandex.ru", "yadi.sk",
        "filecrypt.cc", "filecrypt.co",
        "clicknupload.click", "clicknupload.me", "clicknupload.net",
        "keeplinks.org", "keeplinks.co",
        "safelinking.net",
        "relink.to",
        "github.com"
    };

    public static bool IsKnownSafeOrHosterDomain(string? hostOrDomain)
    {
        if (string.IsNullOrWhiteSpace(hostOrDomain)) return false;
        var reg = GetRegistrableDomain(hostOrDomain);
        if (_knownSafeAndHosterDomains.Contains(hostOrDomain) || _knownSafeAndHosterDomains.Contains(reg))
            return true;

        if (hostOrDomain.EndsWith("." + Reepax.Services.Extractor.FastHostResolver.CanonicalDomain, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static readonly HashSet<string> _searchEngineAndPortalDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "google.com", "google.de", "google.fr", "google.co.uk", "google.es", "google.it",
        "bing.com", "duckduckgo.com", "ecosia.org", "qwant.com", "startpage.com", "yahoo.com",
        "baidu.com", "yandex.com", "yandex.ru",
        "wikipedia.org", "wikimedia.org"
    };

    public static bool IsSearchEngineOrPortal(string? hostOrDomain)
    {
        if (string.IsNullOrWhiteSpace(hostOrDomain)) return false;
        var reg = GetRegistrableDomain(hostOrDomain);
        return _searchEngineAndPortalDomains.Contains(hostOrDomain) ||
               _searchEngineAndPortalDomains.Contains(reg);
    }

    public static bool HasDeceptiveRedirectTarget(string targetUrl)
    {
        if (string.IsNullOrWhiteSpace(targetUrl))
            return false;

        try
        {
            var uri = new Uri(targetUrl);
            var query = uri.Query;
            if (!string.IsNullOrWhiteSpace(query))
            {
                var matches = Regex.Matches(query, @"(?:[\?&])(?:url|redirect|dest|target|to|r|goto|out|link|next|forward|u)=([^&]+)", RegexOptions.IgnoreCase);
                foreach (Match match in matches)
                {
                    if (match.Groups.Count > 1)
                    {
                        var encodedVal = match.Groups[1].Value;
                        var decodedVal = Uri.UnescapeDataString(encodedVal);
                        if (decodedVal.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                            decodedVal.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        {
                            if (AdBlockRustEngine.Instance.ShouldBlock(decodedVal) ||
                                DynamicallyBlockedRedirectDomains.ContainsKey(GetRegistrableDomain(new Uri(decodedVal).Host)))
                            {
                                return true;
                            }

                            // Recursively check for nested deceptive redirects
                            if (HasDeceptiveRedirectTarget(decodedVal))
                            {
                                return true;
                            }
                        }
                    }
                }
            }
        }
        catch { }

        var lower = targetUrl.ToLowerInvariant();
        if (lower.Contains("utm_") ||
            lower.Contains("aff_") ||
            lower.Contains("affid") ||
            lower.Contains("clickid") ||
            lower.Contains("campaignid") ||
            lower.Contains("subid") ||
            lower.Contains("/pop.") ||
            lower.Contains("/redirect?") ||
            lower.Contains("out.php?") ||
            lower.Contains("goto.php?") ||
            lower.Contains("r.php?") ||
            lower.Contains("tracker") ||
            lower.Contains("/click?") ||
            lower.Contains("traffic") ||
            lower.Contains("/delivery/") ||
            lower.Contains("landing") ||
            lower.Contains("ad_url="))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks whether a navigation redirect should be blocked.
    /// </summary>
    public static bool ShouldBlockRedirect(string? currentUrl, string? targetUrl, bool isUserInitiated)
    {
        if (string.IsNullOrWhiteSpace(targetUrl))
            return false;

        // 1. Never block CAPTCHAs or direct file downloads
        if (IsWhitelisted(targetUrl) || IsDirectDownloadUrl(targetUrl))
            return false;

        // 2. ALWAYS block known ad, tracker, and redirector networks via native Rust engine
        if (AdBlockRustEngine.Instance.ShouldBlock(targetUrl))
            return true;

        try
        {
            var targetUri = new Uri(targetUrl);
            var targetHost = targetUri.Host.ToLowerInvariant();
            var targetDomain = GetRegistrableDomain(targetHost);

            // 3. Block dynamically detected ad/redirect domains
            if (DynamicallyBlockedRedirectDomains.ContainsKey(targetDomain) ||
                DynamicallyBlockedRedirectDomains.ContainsKey(targetHost))
            {
                return true;
            }

            // 4. Check deceptive/cloaked redirect query targets
            if (HasDeceptiveRedirectTarget(targetUrl))
                return true;

            // 5. Allow known safe hosters and platforms
            bool isKnownHosterOrSafe = IsKnownSafeOrHosterDomain(targetDomain) || IsKnownSafeOrHosterDomain(targetHost);
            if (isKnownHosterOrSafe)
                return false;

            // 6. User clicks to legitimate sites are allowed
            if (isUserInitiated)
                return false;

            // 7. Block non-user-initiated (automatic) cross-domain redirects to unknown external domains
            if (!string.IsNullOrWhiteSpace(currentUrl))
            {
                var currentUri = new Uri(currentUrl);
                var currentHost = currentUri.Host.ToLowerInvariant();
                var currentDomain = GetRegistrableDomain(currentHost);

                if (!string.IsNullOrEmpty(currentDomain) &&
                    !string.IsNullOrEmpty(targetDomain) &&
                    !currentDomain.Equals(targetDomain, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch { }

        return false;
    }

    public static string GetAntiPopupScript() => @"
(function() {
    'use strict';
    try {
        const noop = function() { return null; };
        const originalOpen = window.open;
        window.open = function(url, target, features) {
            if (event && event.isTrusted) {
                return originalOpen.call(window, url, target, features);
            }
            return null;
        };
        window.alert = noop;
        window.confirm = function() { return true; };
        window.prompt = noop;
    } catch(e) {}
})();";

    public static string GetCosmeticFilterCss() => @"
[class*=""ad-""], [id*=""ad-""], [class*=""-ad""], [id*=""-ad""],
[class*=""banner""], [id*=""banner""], [class*=""popup""], [id*=""popup""],
[class*=""sponsor""], [id*=""sponsor""], .ad, .ads, .advertisement {
    display: none !important;
    visibility: hidden !important;
    height: 0 !important;
    opacity: 0 !important;
    pointer-events: none !important;
}";
}
