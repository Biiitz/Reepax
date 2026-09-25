using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Reepax.Services.AdBlock.Native;
using Reepax.Services.Browser;
using Reepax.Services.Storage;

namespace Reepax.Services.AdBlock;

public enum FilterUpdateResult
{
    NewFiltersApplied,
    AlreadyUpToDate,
    Failed
}

/// <summary>
/// High-level service managing the native Brave adblock-rust engine.
/// Supports uBlock Origin & EasyList rules, FlatBuffers caching, domain-specific toggles,
/// cosmetic injection, and real-time statistics.
/// </summary>
public class AdBlockRustEngine : IDisposable
{
    private static readonly Lazy<AdBlockRustEngine> _instance = new(() => new AdBlockRustEngine());
    public static AdBlockRustEngine Instance => _instance.Value;

    private IntPtr _engine = IntPtr.Zero;
    private readonly object _lock = new();
    private bool _isDisposed;

    private readonly ConcurrentDictionary<string, byte> _userWhitelistedDomains = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _blockedCountsPerHost = new(StringComparer.OrdinalIgnoreCase);

    public bool IsNativeAvailable => AdBlockRustNative.IsAvailable && _engine != IntPtr.Zero;

    public bool IsEnabled { get; set; } = true;
    public bool IsCosmeticFilteringEnabled { get; set; } = true;
    public bool IsAntiPopupGuardEnabled { get; set; } = true;

    public int TotalBlockedCount { get; private set; }

    private static string CacheFilePath => Path.Combine(SettingsService.AppDataDirectory, "adblock_cache.dat");
    private static string WhitelistFilePath => Path.Combine(SettingsService.AppDataDirectory, "adblock_whitelist.txt");
    private static string RulesFilePath => Path.Combine(SettingsService.AppDataDirectory, "adblock_rules.txt");
    private static string RulesHashFilePath => Path.Combine(SettingsService.AppDataDirectory, "adblock_rules.hash");

    private static readonly string[] FilterListUrls =
    [
        "https://easylist.to/easylist/easylist.txt",
        "https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/badware.txt"
    ];

    public AdBlockRustEngine()
    {
        LoadWhitelist();
        InitializeEngine();
    }

    private void InitializeEngine()
    {
        if (!AdBlockRustNative.IsAvailable)
        {
            AppLogger.Warn("[AdBlockRustEngine] Native Rust library (reepax_adblock.dll) not present.");
            return;
        }

        try
        {
            // 1. Try loading cached serialized FlatBuffer from disk
            if (File.Exists(CacheFilePath))
            {
                try
                {
                    var bytes = File.ReadAllBytes(CacheFilePath);
                    if (bytes.Length > 0)
                    {
                        var ptr = AdBlockRustNative.CreateFromBuffer(bytes);
                        if (ptr != IntPtr.Zero)
                        {
                            _engine = ptr;
                            AppLogger.Info($"[AdBlockRustEngine] Loaded serialized adblock engine from cache ({bytes.Length} bytes).");
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"[AdBlockRustEngine] Failed to load cache file: {ex.Message}");
                }
            }

            // 2. Check if updated rules exist on disk, or fall back to core rules
            string allRules = GetCoreFilterRules();
            if (File.Exists(RulesFilePath))
            {
                try
                {
                    var downloaded = File.ReadAllText(RulesFilePath);
                    if (!string.IsNullOrWhiteSpace(downloaded))
                    {
                        allRules = allRules + "\n\n" + downloaded;
                    }
                }
                catch { }
            }

            // 3. Compile rules into native engine
            _engine = AdBlockRustNative.CreateFromRules(allRules);

            if (_engine != IntPtr.Zero)
            {
                AppLogger.Info("[AdBlockRustEngine] Compiled filter rules into native engine.");
                SaveCacheAsync();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("[AdBlockRustEngine] Failed to initialize native engine", ex);
        }
    }

    /// <summary>
    /// Downloads the latest adblock filter lists, verifies content, and compiles them into the native Rust engine.
    /// </summary>
    public async Task<FilterUpdateResult> UpdateFilterListsAsync(System.Threading.CancellationToken cancellationToken = default)
    {
        if (!AdBlockRustNative.IsAvailable)
            return FilterUpdateResult.Failed;

        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"Mozilla/5.0 (Windows NT 10.0; Win64; x64) Reepax/{Update.AppUpdateService.AppCurrentVersion}");

            string? downloadedRules = null;
            foreach (var url in FilterListUrls)
            {
                try
                {
                    var content = await httpClient.GetStringAsync(url, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(content) && content.Length > 500 && (content.Contains("||") || content.Contains("! Title") || content.Contains("##")))
                    {
                        downloadedRules = content;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"[AdBlockRustEngine] Failed to fetch filter list from {url}: {ex.Message}");
                }
            }

            if (string.IsNullOrWhiteSpace(downloadedRules))
            {
                return FilterUpdateResult.Failed;
            }

            // Calculate SHA-256 hash of downloaded rules to check for actual changes
            string newHash;
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                var hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(downloadedRules));
                newHash = Convert.ToHexString(hashBytes);
            }

            if (File.Exists(RulesHashFilePath))
            {
                try
                {
                    var currentHash = (await File.ReadAllTextAsync(RulesHashFilePath, cancellationToken)).Trim();
                    if (string.Equals(currentHash, newHash, StringComparison.OrdinalIgnoreCase))
                    {
                        return FilterUpdateResult.AlreadyUpToDate;
                    }
                }
                catch { }
            }

            var combinedRules = GetCoreFilterRules() + "\n\n" + downloadedRules;
            var newEnginePtr = await Task.Run(() => AdBlockRustNative.CreateFromRules(combinedRules), cancellationToken);

            if (newEnginePtr == IntPtr.Zero)
            {
                AppLogger.Warn("[AdBlockRustEngine] Failed to compile downloaded filter rules into native engine.");
                return FilterUpdateResult.Failed;
            }

            lock (_lock)
            {
                var oldEngine = _engine;
                _engine = newEnginePtr;
                if (oldEngine != IntPtr.Zero)
                {
                    AdBlockRustNative.Free(oldEngine);
                }
            }

            try
            {
                Directory.CreateDirectory(SettingsService.AppDataDirectory);
                await File.WriteAllTextAsync(RulesFilePath, downloadedRules, cancellationToken);
                await File.WriteAllTextAsync(RulesHashFilePath, newHash, cancellationToken);
                SaveCacheAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[AdBlockRustEngine] Failed to write updated rules files: {ex.Message}");
            }

            AppLogger.Info($"[AdBlockRustEngine] Successfully updated filter lists ({downloadedRules.Length} characters).");
            return FilterUpdateResult.NewFiltersApplied;
        }
        catch (Exception ex)
        {
            AppLogger.Error("[AdBlockRustEngine] Filter list update encountered an error", ex);
            return FilterUpdateResult.Failed;
        }
    }

    private void SaveCacheAsync()
    {
        Task.Run(() =>
        {
            lock (_lock)
            {
                if (_engine == IntPtr.Zero) return;
                try
                {
                    var bytes = AdBlockRustNative.Serialize(_engine);
                    if (bytes != null && bytes.Length > 0)
                    {
                        Directory.CreateDirectory(SettingsService.AppDataDirectory);
                        File.WriteAllBytes(CacheFilePath, bytes);
                        AppLogger.Info($"[AdBlockRustEngine] Saved compiled engine cache ({bytes.Length} bytes).");
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"[AdBlockRustEngine] Failed to save cache: {ex.Message}");
                }
            }
        });
    }

    /// <summary>
    /// Checks whether a network request should be blocked.
    /// </summary>
    public bool ShouldBlock(string url, string? sourceUrl = null, string requestType = "other")
    {
        return ShouldBlock(url, sourceUrl, requestType, out _);
    }

    /// <summary>
    /// Checks whether a network request should be blocked or redirected.
    /// </summary>
    public bool ShouldBlock(string url, string? sourceUrl, string requestType, out bool isRedirect)
    {
        isRedirect = false;

        if (!IsEnabled)
            return false;

        if (string.IsNullOrWhiteSpace(url))
            return false;

        // Never block CAPTCHAs or genuine filehoster downloads
        if (BrowserSecurityGuard.IsWhitelisted(url) || BrowserSecurityGuard.IsDirectDownloadUrl(url))
            return false;

        // Check if source domain is on user whitelist
        if (!string.IsNullOrWhiteSpace(sourceUrl))
        {
            var host = GetHost(sourceUrl);
            if (!string.IsNullOrEmpty(host) && IsDomainWhitelisted(host))
                return false;
        }

        // Native Rust Brave adblock engine
        if (IsNativeAvailable)
        {
            var res = AdBlockRustNative.CheckNetwork(_engine, url, sourceUrl ?? string.Empty, requestType);
            if (res == 1) // Blocked
            {
                RegisterBlocked(sourceUrl);
                return true;
            }
            if (res == 2) // Redirected (e.g. 1x1 pixel or dummy)
            {
                isRedirect = true;
                RegisterBlocked(sourceUrl);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets cosmetic CSS element-hiding rules and scriptlets for a given page URL.
    /// </summary>
    public (string Css, string Script) GetCosmeticResources(string url)
    {
        if (!IsEnabled || !IsCosmeticFilteringEnabled)
            return (string.Empty, string.Empty);

        var host = GetHost(url);
        if (!string.IsNullOrEmpty(host) && IsDomainWhitelisted(host))
            return (string.Empty, string.Empty);

        if (IsNativeAvailable)
        {
            return AdBlockRustNative.GetCosmeticResources(_engine, url);
        }

        return (BrowserSecurityGuard.GetCosmeticFilterCss(), string.Empty);
    }

    private void RegisterBlocked(string? sourceUrl)
    {
        TotalBlockedCount++;
        var host = GetHost(sourceUrl);
        if (!string.IsNullOrEmpty(host))
        {
            _blockedCountsPerHost.AddOrUpdate(host, 1, (_, count) => count + 1);
        }
    }

    public int GetBlockedCountForHost(string? hostOrUrl)
    {
        var host = GetHost(hostOrUrl);
        if (string.IsNullOrEmpty(host)) return 0;
        _blockedCountsPerHost.TryGetValue(host, out var count);
        return count;
    }

    public void ResetBlockedCountForHost(string? hostOrUrl)
    {
        var host = GetHost(hostOrUrl);
        if (!string.IsNullOrEmpty(host))
        {
            _blockedCountsPerHost.TryRemove(host, out _);
        }
    }

    public bool IsDomainWhitelisted(string hostOrDomain)
    {
        var host = GetHost(hostOrDomain) ?? hostOrDomain.ToLowerInvariant();
        if (_userWhitelistedDomains.ContainsKey(host))
            return true;

        var regDomain = BrowserSecurityGuard.GetRegistrableDomain(host);
        return !string.IsNullOrEmpty(regDomain) && _userWhitelistedDomains.ContainsKey(regDomain);
    }

    public void SetDomainWhitelisted(string hostOrDomain, bool whitelisted)
    {
        var host = GetHost(hostOrDomain) ?? hostOrDomain.ToLowerInvariant();
        if (whitelisted)
        {
            _userWhitelistedDomains[host] = 1;
        }
        else
        {
            _userWhitelistedDomains.TryRemove(host, out _);
            var regDomain = BrowserSecurityGuard.GetRegistrableDomain(host);
            if (!string.IsNullOrEmpty(regDomain))
            {
                _userWhitelistedDomains.TryRemove(regDomain, out _);
            }
        }
        SaveWhitelist();
    }

    private void LoadWhitelist()
    {
        try
        {
            if (File.Exists(WhitelistFilePath))
            {
                foreach (var line in File.ReadAllLines(WhitelistFilePath))
                {
                    var clean = line.Trim().ToLowerInvariant();
                    if (!string.IsNullOrWhiteSpace(clean) && !clean.StartsWith("#"))
                    {
                        _userWhitelistedDomains[clean] = 1;
                    }
                }
            }
        }
        catch { }
    }

    private void SaveWhitelist()
    {
        try
        {
            Directory.CreateDirectory(SettingsService.AppDataDirectory);
            File.WriteAllLines(WhitelistFilePath, _userWhitelistedDomains.Keys);
        }
        catch { }
    }

    private static string? GetHost(string? urlOrHost)
    {
        if (string.IsNullOrWhiteSpace(urlOrHost)) return null;
        if (!urlOrHost.Contains("://"))
        {
            return urlOrHost.Trim().ToLowerInvariant();
        }
        try
        {
            return new Uri(urlOrHost).Host.ToLowerInvariant();
        }
        catch
        {
            return urlOrHost.Trim().ToLowerInvariant();
        }
    }

    private static string GetCoreFilterRules()
    {
        // Hand-crafted core filter list in standard uBlock Origin / EasyList syntax
        // Includes major ad networks, tracking telemetry, video ads, cosmetic elements, and redirects.
        return @"! Title: Reepax Core Adblock Rules
! Version: 2026.1
! Description: High-performance core rules compatible with uBlock Origin & EasyList

! === Display & Programmatic Ad Exchanges ===
||doubleclick.net^
||googlesyndication.com^
||googleadservices.com^
||pagead2.googlesyndication.com^
||adservice.google.com^
||adnxs.com^
||criteo.com^
||criteo.net^
||rubiconproject.com^
||openx.net^
||pubmatic.com^
||smartadserver.com^
||bidswitch.net^
||casalemedia.com^
||taboola.com^
||outbrain.com^
||infolinks.com^
||media.net^
||amazon-adsystem.com^
||sovrn.com^
||contextweb.com^
||yieldmo.com^
||triplelift.com^
||sharethrough.com^
||mgid.com^
||revcontent.com^
||ezoic.net^
||ezoic.com^

! === Popups, Popunders & Click-Hijack Networks ===
||popads.net^
||popcash.net^
||propellerads.com^
||propellerclick.com^
||adcash.com^
||adsterra.com^
||admaven.com^
||ad-maven.com^
||clickadu.com^
||clickadu.net^
||zeropark.com^
||hilltopads.com^
||hilltopads.net^
||monetag.com^
||monetag.net^
||richpush.co^
||richads.com^
||pushground.com^
||galaksion.com^
||evadav.com^
||trafficjunky.com^
||trafficfactory.biz^
||exoclick.com^
||juicyads.com^
||plugrush.com^
||onclickmega.com^
||onclicksuper.com^
||onclickads.net^
||deloton.com^
||rollerads.com^
||trafficstars.com^
||highperformanceformat.com^
||highcpmgate.com^
||highcpmrevenuenetwork.com^
||tsyndicate.com^
||yllix.com^
||adport.io^
||daftpush.com^
||notix.co^
||adsco.re^
||adoperator.com^

! === Telemetry & Web Trackers ===
||scorecardresearch.com^
||quantserve.com^
||hotjar.com^
||clarity.ms^
||mouseflow.com^
||crazyegg.com^
||segment.io^
||mixpanel.com^
||amplitude.com^
||branch.io^
||appsflyer.com^
||statcounter.com^
||mc.yandex.ru^
||google-analytics.com^
||googletagmanager.com^
||googletagservices.com^
||newrelic.com^
||nr-data.net^
||doubleverify.com^
||moatads.com^

! === Generic Cosmetic Filter Rules (Element Hiding) ===
##.ad-banner
##.ad-container
##.ads-banner
##.adv-block
##.banner-wrap
##.ad_box
##div[id^=""ad_""]
##div[class*=""sponsored""]
##ins.adsbygoogle
##[class*=""adsbygoogle""]
##div[id^=""div-gpt-ad""]
##[id*=""google_ads_iframe""]
##[id*=""taboola""]
##[class*=""taboola""]
##[id*=""outbrain""]
##[class*=""outbrain""]
##[id*=""mgid""]
##[class*=""mgid""]
##[data-ad]
##[data-ads]
##[data-adunit]
##[data-ad-slot]
##[class*=""banner-ad""]
##[class*=""ad-slot""]
##[class*=""ad-wrapper""]
##[class*=""advertisement""]
##aside[class*=""ad""]

! === YouTube Video Ads ===
||googlevideo.com/videoplayback*$ad
||youtube.com/api/stats/ads^
||youtube.com/pagead/*
||youtube.com/ptracking*
youtube.com##.video-ads
youtube.com##.ytp-ad-module
youtube.com##.ytp-ad-overlay-container
youtube.com##ytd-promoted-sparkles-web-renderer
youtube.com##ytd-display-ad-renderer

! === Whitelist for Essential CAPTCHA Platforms (Never Block) ===
@@||challenges.cloudflare.com^
@@||turnstile.cloudflare.com^
@@||recaptcha.net^
@@||google.com/recaptcha^
@@||gstatic.com/recaptcha^
@@||hcaptcha.com^
@@||geetest.com^
@@||arkoselabs.com^
@@||friendlycaptcha.com^
";
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                if (_engine != IntPtr.Zero)
                {
                    AdBlockRustNative.Free(_engine);
                    _engine = IntPtr.Zero;
                }
            }
        }
    }
}
