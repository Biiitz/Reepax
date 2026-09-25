using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Reepax.Models;
using Reepax.Services.AdBlock;
using Reepax.Services.Download;
using Reepax.Services.Storage;

namespace Reepax.Services.Browser;

/// <summary>
/// Controls the WebView2 instance of an external browser window (Scenario C):
/// AdBlock, popup redirection, download interception, and direct link extraction.
/// </summary>
public class BrowserWindowController : IDisposable
{
    private readonly int _windowId;
    private readonly WebView2 _webView;
    private bool _isInitialized;
    private string? _currentNavigatingUrl;

    // Redirect blocker: last allowed top-level URL (reference for cross-domain checks)
    private string? _currentTopLevelUrl;

    // Blocked redirects/ads counter & notification for UI
    public int BlockedCount { get; private set; }
    public event Action<int>? BlockedCountChanged;

    // AdBlock / redirect blocking can be disabled via app settings
    private static bool IsAdBlockerEnabled => SettingsService.Instance.Settings.EnableAdBlocker;

    // Shared environment/profile for ALL browser windows: extensions, cookies, and
    // their settings persist across windows and sessions.
    private static CoreWebView2Environment? _sharedEnvironment;
    private static readonly SemaphoreSlim _sharedEnvLock = new(1, 1);
    private static readonly SemaphoreSlim _extensionsLock = new(1, 1);

    public int WindowId => _windowId;
    public bool IsInitialized => _isInitialized;
    public string? CurrentNavigatingUrl => _currentNavigatingUrl;

    /// <summary>
    /// Returns the shared WebView2 environment for all browser windows (and options popups).
    /// </summary>
    internal static async Task<CoreWebView2Environment> GetSharedEnvironmentAsync()
    {
        if (_sharedEnvironment != null)
            return _sharedEnvironment;

        await _sharedEnvLock.WaitAsync();
        try
        {
            if (_sharedEnvironment == null)
            {
                var folder = Path.Combine(Storage.SettingsService.WebView2Directory, "Browser");
                Directory.CreateDirectory(folder);

                // AreBrowserExtensionsEnabled=true is required, otherwise AddBrowserExtensionAsync
                // fails with ERROR_NOT_SUPPORTED (0x80070032).
                // Flags prevent Chromium from throttling timers or suspending rendering for minimized windows.
                var options = new CoreWebView2EnvironmentOptions
                {
                    AreBrowserExtensionsEnabled = true,
                    AdditionalBrowserArguments = "--disable-background-timer-throttling --disable-backgrounding-occluded-windows --disable-renderer-backgrounding"
                };
                _sharedEnvironment = await CoreWebView2Environment.CreateAsync(null, folder, options);
            }
            return _sharedEnvironment;
        }
        finally
        {
            _sharedEnvLock.Release();
        }
    }

    private bool _isDisposed;

    public BrowserWindowController(int windowId, WebView2 webView)
    {
        _windowId = windowId;
        _webView = webView;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        ThemeService.Instance.ThemeChanged -= UpdateTheme;
        if (_webView.CoreWebView2 != null)
        {
            try
            {
                _webView.CoreWebView2.WebResourceRequested -= OnWebResourceRequested;
                _webView.CoreWebView2.WebResourceResponseReceived -= OnWebResourceResponseReceived;
                _webView.CoreWebView2.NewWindowRequested -= OnNewWindowRequested;
                _webView.CoreWebView2.NavigationStarting -= OnNavigationStarting;
                _webView.CoreWebView2.FrameNavigationStarting -= OnFrameNavigationStarting;
                _webView.CoreWebView2.PermissionRequested -= OnPermissionRequested;
                _webView.CoreWebView2.DownloadStarting -= OnDownloadStarting;
                _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                _webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                _webView.CoreWebView2.Stop();
            }
            catch { }
        }
        try { _webView.Dispose(); } catch { }
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized || _isDisposed)
            return;

        try
        {
            // Shared environment: extensions & settings apply across all windows
            var env = await GetSharedEnvironmentAsync();
            if (_isDisposed) return;
            await _webView.EnsureCoreWebView2Async(env);
            if (_isDisposed) return;

            // Install / synchronize extensions into shared profile
            await _extensionsLock.WaitAsync();
            try
            {
                await BrowserExtensionService.Instance.InstallIntoProfileAsync(_webView.CoreWebView2.Profile);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[Extensions] Installation failed: {ex.Message}");
            }
            finally
            {
                _extensionsLock.Release();
            }

            if (_isDisposed) return;

            // Extract and register real runtime User-Agent for all background HttpClient instances
            try
            {
                var liveUa = await _webView.CoreWebView2.ExecuteScriptAsync("navigator.userAgent");
                if (!string.IsNullOrWhiteSpace(liveUa))
                {
                    SystemIntegration.HttpUserAgentService.UpdateUserAgent(liveUa);
                }
            }
            catch { }

            if (_isDisposed) return;

            // Configure Theme and Background
            UpdateTheme(ThemeService.Instance.IsDarkMode);
            ThemeService.Instance.ThemeChanged += UpdateTheme;

            // Configure Settings
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _webView.CoreWebView2.Settings.IsWebMessageEnabled = true;

            // 1. Network-Level Ad- & Pop-up Blocker Filter
            _webView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            _webView.CoreWebView2.WebResourceRequested += OnWebResourceRequested;

            // 1b. Observe 30x redirect chains (cross-domain targets are dynamically blocked)
            _webView.CoreWebView2.WebResourceResponseReceived += OnWebResourceResponseReceived;

            // 2. Redirect Pop-up Windows & Tabs to the current window (allows download buttons with target="_blank" to work!)
            _webView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;

            // 3. Block Deceptive Auto-Redirects to Ad Landers (including unknown cross-domain redirects)
            _webView.CoreWebView2.NavigationStarting += OnNavigationStarting;

            // 4. Block Third-Party Ad Subframes (while whitelisting CAPTCHAs)
            _webView.CoreWebView2.FrameNavigationStarting += OnFrameNavigationStarting;

            // 5. Automatically Deny All Push Notification / Permission Traps
            _webView.CoreWebView2.PermissionRequested += OnPermissionRequested;

            // 6. Inject Stealth Anti-Popup & Anti-Clickjack Shield Script
            await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(BrowserSecurityGuard.GetAntiPopupScript());

            // 7. Inject Cosmetic Filter CSS
            var cosmeticScript = $@"
            (function() {{
                try {{
                    const style = document.createElement('style');
                    style.textContent = `{BrowserSecurityGuard.GetCosmeticFilterCss().Replace("`", "\\`")}`;
                    document.head ? document.head.appendChild(style) : document.documentElement.appendChild(style);
                }} catch(e) {{}}
            }})();";
            await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(cosmeticScript);

            // 8. Download Interception Engine (Suppresses browser popup & hands off to C# DownloadEngine)
            _webView.CoreWebView2.DownloadStarting += OnDownloadStarting;

            // 9. Script & DOM extraction communication
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            // Navigation error handling & automated DOM resolution
            _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

            _isInitialized = true;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[Browser Window {_windowId}] Init Error", ex);
        }
    }

    private void UpdateTheme(bool isDark)
    {
        try
        {
            // Keep webview background neutral white so hoster web pages display in their natural design
            _webView.DefaultBackgroundColor = System.Drawing.Color.White;

            if (_webView.CoreWebView2 != null)
            {
                // CoreWebView2PreferredColorScheme.Light ensures websites and CAPTCHAs render in their intended colors
                // rather than forcing artificial dark-mode inversion onto pages.
                _webView.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Light;
            }
        }
        catch { }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        var uri = e.Uri;
        if (string.IsNullOrWhiteSpace(uri))
            return;

        // Reset blocked counter on new top-level navigation
        BlockedCount = 0;
        try
        {
            App.Current?.Dispatcher?.InvokeAsync(() => BlockedCountChanged?.Invoke(0));
        }
        catch { }

        if (IsAdBlockerEnabled)
        {
            var isBlockedRedirect = BrowserSecurityGuard.ShouldBlockRedirect(
                _webView.Source?.ToString() ?? _currentTopLevelUrl,
                uri,
                e.IsUserInitiated);

            if (isBlockedRedirect)
            {
                AppLogger.Warn($"[Browser Window {_windowId}] Blocked redirect to ad network: {uri} (UserInitiated={e.IsUserInitiated})");
                e.Cancel = true;
                IncrementBlockedCount(uri, "NavigationStarting");
                return;
            }

            // Inject dynamic cosmetic rules from AdBlockRustEngine for this URL
            try
            {
                var (css, script) = AdBlockRustEngine.Instance.GetCosmeticResources(uri);
                if (!string.IsNullOrEmpty(css))
                {
                    var escapedCss = css.Replace("`", "\\`");
                    var dynScript = $@"
                    (function() {{
                        try {{
                            var s = document.createElement('style');
                            s.textContent = `{escapedCss}`;
                            (document.head || document.documentElement).appendChild(s);
                        }} catch(e) {{}}
                    }})();";
                    _webView.CoreWebView2?.ExecuteScriptAsync(dynScript);
                }
                if (!string.IsNullOrEmpty(script))
                {
                    _webView.CoreWebView2?.ExecuteScriptAsync(script);
                }
            }
            catch { }
        }

        // Allowed top-level navigation → remember as current page
        _currentTopLevelUrl = uri;
    }

    private void OnFrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!IsAdBlockerEnabled)
            return;

        var uri = e.Uri;
        if (!string.IsNullOrWhiteSpace(uri) && !BrowserSecurityGuard.IsWhitelisted(uri) && AdBlockRustEngine.Instance.ShouldBlock(uri))
        {
            AppLogger.Warn($"[Browser Window {_windowId}] Blocked frame navigation to ad network: {uri}");
            e.Cancel = true;
            IncrementBlockedCount(uri, "FrameNavigationStarting");
        }
    }

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        e.State = CoreWebView2PermissionState.Deny;
        e.Handled = true;
    }

    /// <summary>
    /// Observes top-level document responses: cross-domain 30x redirects (301/302/307/308)
    /// are detected and third-party target domains are added to the blocklist if identified as ads/trackers.
    /// </summary>
    private void OnWebResourceResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        if (!IsAdBlockerEnabled)
            return;

        try
        {
            // Only inspect top-level document responses: request URI must match
            // currently navigating top-level URI (or current page).
            var requestUri = e.Request.Uri;
            if (string.IsNullOrWhiteSpace(requestUri))
                return;

            var topLevelUrl = _currentTopLevelUrl ?? _webView.Source?.ToString();
            if (string.IsNullOrWhiteSpace(topLevelUrl) ||
                !string.Equals(requestUri, topLevelUrl, StringComparison.OrdinalIgnoreCase))
                return;

            var status = e.Response.StatusCode;
            if (status != 301 && status != 302 && status != 307 && status != 308)
                return;

            if (!e.Response.Headers.Contains("Location"))
                return;

            var location = e.Response.Headers.GetHeader("Location");
            if (string.IsNullOrWhiteSpace(location))
                return;

            // Location can be relative -> resolve against request URI
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var sourceUri) ||
                !Uri.TryCreate(sourceUri, location, out var targetUri))
                return;

            var sourceDomain = BrowserSecurityGuard.GetRegistrableDomain(sourceUri.Host);
            var targetDomain = BrowserSecurityGuard.GetRegistrableDomain(targetUri.Host);

            // Ignore same-eTLD+1, direct downloads, CAPTCHA whitelists, and known safe hosters
            if (string.IsNullOrEmpty(targetDomain) ||
                string.Equals(sourceDomain, targetDomain, StringComparison.OrdinalIgnoreCase) ||
                BrowserSecurityGuard.IsWhitelisted(targetUri.ToString()) ||
                BrowserSecurityGuard.IsDirectDownloadUrl(targetUri.ToString()) ||
                BrowserSecurityGuard.IsKnownSafeOrHosterDomain(targetDomain))
                return;

            // Block confirmed ad/tracker networks or deceptive redirect domains
            if (AdBlockRustEngine.Instance.ShouldBlock(targetUri.ToString()) ||
                BrowserSecurityGuard.HasDeceptiveRedirectTarget(targetUri.ToString()))
            {
                if (BrowserSecurityGuard.AddDynamicBlockedDomain(targetDomain))
                {
                    AppLogger.Warn($"[Browser Window {_windowId}] Cross-domain 30x redirect chain to ad domain: {e.Request.Uri} -> {targetUri} (domain now blocked: {targetDomain})");
                    IncrementBlockedCount(targetUri.ToString(), "30x-Chain");
                }
            }
        }
        catch { }
    }

    private static string? GetDomainFromUrl(string url)
    {
        try
        {
            return BrowserSecurityGuard.GetRegistrableDomain(new Uri(url).Host);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetHostFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            return new Uri(url).Host;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> GetCookiesForUrlAsync(string url)
    {
        try
        {
            if (_webView.CoreWebView2?.CookieManager != null)
            {
                var cookies = await _webView.CoreWebView2.CookieManager.GetCookiesAsync(url);
                if (cookies != null && cookies.Count > 0)
                {
                    return string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
                }
            }
        }
        catch { }
        return null;
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!IsAdBlockerEnabled)
            return;

        var url = e.Request.Uri;
        var currentUrl = _currentTopLevelUrl ?? _webView.Source?.ToString() ?? string.Empty;

        var requestType = e.ResourceContext switch
        {
            CoreWebView2WebResourceContext.Script => "script",
            CoreWebView2WebResourceContext.Image => "image",
            CoreWebView2WebResourceContext.Stylesheet => "stylesheet",
            CoreWebView2WebResourceContext.Fetch or CoreWebView2WebResourceContext.XmlHttpRequest => "xmlhttprequest",
            CoreWebView2WebResourceContext.Document => "subdocument",
            CoreWebView2WebResourceContext.Media => "media",
            CoreWebView2WebResourceContext.Font => "font",
            _ => "other"
        };

        // Check if AdBlockRustEngine flags this resource as an ad / tracker
        if (AdBlockRustEngine.Instance.ShouldBlock(url, currentUrl, requestType, out var isRedirect))
        {
            IncrementBlockedCount(url, "AdBlock");

            if (e.ResourceContext == CoreWebView2WebResourceContext.Script)
            {
                // For scripts, return 200 OK JS stub to defuse anti-adblockers
                var jsStub = "/* Reepax AdBlock Stub */ window.adsbygoogle = window.adsbygoogle || []; window.canRunAds = true;";
                var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(jsStub));
                e.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                    stream, 200, "OK", "Content-Type: application/javascript; charset=utf-8");
            }
            else
            {
                e.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                    Stream.Null, 
                    204, 
                    "No Content", 
                    "Content-Type: text/plain");
            }
        }
    }

    private async void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Suppress opening separate desktop window
        e.Handled = true;

        var url = e.Uri;
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase))
            return;

        // If blocked as an ad or popup network by Rust engine, drop immediately
        if (IsAdBlockerEnabled && AdBlockRustEngine.Instance.ShouldBlock(url))
        {
            AppLogger.Info($"[Browser Window {_windowId}] Blocked popup window to ad URL: {url}");
            IncrementBlockedCount(url, "NewWindowRequested");
            return;
        }

        // Deceptive redirect query target? Drop immediately
        if (IsAdBlockerEnabled && BrowserSecurityGuard.HasDeceptiveRedirectTarget(url))
        {
            AppLogger.Info($"[Browser Window {_windowId}] Blocked popup window with deceptive redirect target: {url}");
            IncrementBlockedCount(url, "DeceptiveRedirectTarget");
            return;
        }

        // 1. Direct download links: Intercept directly into Reepax Queue without navigating main window away!
        if (BrowserSecurityGuard.IsDirectDownloadUrl(url))
        {
            AppLogger.Info($"[Browser Window {_windowId}] Direct download intercepted from popup: {url}");
            try
            {
                var userAgent = !string.IsNullOrWhiteSpace(_webView.CoreWebView2?.Settings?.UserAgent)
                    ? _webView.CoreWebView2.Settings.UserAgent
                    : SystemIntegration.HttpUserAgentService.CurrentUserAgent;
                var referer = _webView.Source?.ToString() ?? _currentTopLevelUrl;
                var cookieHeader = await GetCookiesForUrlAsync(url);

                await QueueManager.Instance.OnDownloadInterceptedAsync(
                    _windowId,
                    url,
                    cookieHeader,
                    userAgent,
                    referer,
                    null);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[Browser Window {_windowId}] Error intercepting direct download from popup: {ex.Message}");
            }
            return;
        }

        var currentUrl = _webView.Source?.ToString() ?? _currentTopLevelUrl;
        var currentHost = GetHostFromUrl(currentUrl);
        var currentDomain = !string.IsNullOrEmpty(currentHost) ? BrowserSecurityGuard.GetRegistrableDomain(currentHost) : null;
        var targetHost = GetHostFromUrl(url);
        var targetDomain = !string.IsNullOrEmpty(targetHost) ? BrowserSecurityGuard.GetRegistrableDomain(targetHost) : null;

        // 2. Determine if popup is legitimate navigation:
        // - Same domain or subdomain (e.g. hoster internal links)
        // - Known safe hoster, crypter, or storage CDN
        // - Whitelisted CAPTCHA/challenge platform
        // - Origin is a portal/search engine and user clicked
        bool isSameDomain = !string.IsNullOrEmpty(currentDomain) &&
                            !string.IsNullOrEmpty(targetDomain) &&
                            string.Equals(currentDomain, targetDomain, StringComparison.OrdinalIgnoreCase);

        bool isSafeHoster = BrowserSecurityGuard.IsKnownSafeOrHosterDomain(targetDomain) ||
                            BrowserSecurityGuard.IsKnownSafeOrHosterDomain(targetHost);

        bool isWhitelisted = BrowserSecurityGuard.IsWhitelisted(url);

        bool isPortalClick = e.IsUserInitiated && BrowserSecurityGuard.IsSearchEngineOrPortal(currentDomain);

        bool isLegitimatePopup = isSameDomain || isSafeHoster || isWhitelisted || isPortalClick;

        if (IsAdBlockerEnabled && !isLegitimatePopup)
        {
            AppLogger.Info($"[Browser Window {_windowId}] Discarded untrusted third-party popup: {url}");
            IncrementBlockedCount(url, "ThirdPartyPopup");
            return;
        }

        // Execute legitimate popup navigations in the current window
        AppLogger.Info($"[Browser Window {_windowId}] Navigating current window to legitimate popup target: {url}");
        try
        {
            _webView.CoreWebView2?.Navigate(url);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[Browser Window {_windowId}] Failed to navigate current window to popup target {url}: {ex.Message}");
        }
    }

    private async void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        try
        {
            var directUrl = e.DownloadOperation.Uri;

            // blob: downloads exist only in browser context -> HttpClient cannot fetch them.
            // WebView2 downloads natively; we configure destination path & track progress.
            if (directUrl.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
            {
                HandleBlobDownload(e, directUrl);
                return;
            }

            // 1. SUPPRESS the browser's native download UI / prompt
            e.Cancel = true;
            e.Handled = true;

            var suggestedFileName = Path.GetFileName(e.ResultFilePath);
            if (string.IsNullOrWhiteSpace(suggestedFileName) || suggestedFileName.Equals("download", StringComparison.OrdinalIgnoreCase))
            {
                suggestedFileName = Path.GetFileName(e.DownloadOperation.ResultFilePath);
            }

            var userAgent = !string.IsNullOrWhiteSpace(_webView.CoreWebView2.Settings.UserAgent) 
                ? _webView.CoreWebView2.Settings.UserAgent 
                : SystemIntegration.HttpUserAgentService.CurrentUserAgent;
            var referer = _webView.Source?.ToString();

            // 2. Extract session cookies for the direct download link domain
            string? cookieHeader = null;
            try
            {
                var cookies = await _webView.CoreWebView2.CookieManager.GetCookiesAsync(directUrl);
                if (cookies != null && cookies.Count > 0)
                {
                    cookieHeader = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[Browser Window {_windowId}] Cookie extract error: {ex.Message}", ex);
            }

            AppLogger.Info($"[Browser Window {_windowId}] Intercepted Download! URL: {directUrl}, File: {suggestedFileName}");

            // 3. Hand off to QueueManager -> DownloadEngine
            await QueueManager.Instance.OnDownloadInterceptedAsync(
                _windowId, 
                directUrl, 
                cookieHeader, 
                userAgent, 
                referer, 
                suggestedFileName);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[Browser Window {_windowId}] Error during download interception", ex);
        }
    }

    /// <summary>
    /// blob: downloads run natively via WebView2 (they exist only in the browser context).
    /// Target path is set to the package directory; progress/completion are forwarded to app UI.
    /// </summary>
    private void HandleBlobDownload(CoreWebView2DownloadStartingEventArgs e, string blobUrl)
    {
        var item = QueueManager.Instance.GetWindowItem(_windowId);
        if (item == null)
        {
            e.Cancel = true;
            e.Handled = true;
            return;
        }

        var suggestedFileName = Path.GetFileName(e.ResultFilePath);
        var targetPath = item.SaveFilePath;
        if (!string.IsNullOrWhiteSpace(suggestedFileName) &&
            !suggestedFileName.Equals("download", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(targetPath))
        {
            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                targetPath = Path.Combine(dir, suggestedFileName);
            }
        }

        // Browser saves directly to target (no prompt)
        e.Cancel = false;
        e.Handled = true;
        if (!string.IsNullOrWhiteSpace(targetPath))
        {
            e.ResultFilePath = targetPath;
        }

        var op = e.DownloadOperation;
        var lastBytes = 0L;
        var lastTime = DateTime.Now;

        item.Status = DownloadStatus.Downloading;
        item.StatusMessage = "Wird heruntergeladen...";
        if (!string.IsNullOrWhiteSpace(targetPath))
        {
            item.Rename(Path.GetFileName(targetPath));
            var pkg = QueueManager.Instance.Packages.FirstOrDefault(p => p.Id == item.PackageId || p.Items.Contains(item));
            if (pkg != null)
            {
                Extractor.LinkMetadataResolverService.TryUpdatePackageName(pkg);
            }
        }

        AppLogger.Info($"[Browser Window {_windowId}] Blob-Download nativ übernommen: {blobUrl}");

        op.BytesReceivedChanged += (_, _) =>
        {
            try
            {
                var now = DateTime.Now;
                var dt = (now - lastTime).TotalSeconds;
                if (dt >= 0.25)
                {
                    var speed = (op.BytesReceived - lastBytes) / dt;
                    lastBytes = op.BytesReceived;
                    lastTime = now;
                    long totalBytes = op.TotalBytesToReceive.HasValue ? (long)op.TotalBytesToReceive.Value : 0;
                    item.UpdateProgress(op.BytesReceived, totalBytes, speed);
                }
            }
            catch { }
        };

        op.StateChanged += (_, _) =>
        {
            try
            {
                if (op.State == CoreWebView2DownloadState.Completed)
                {
                    QueueManager.Instance.OnBrowserNativeDownloadCompleted(_windowId, true, null, targetPath);
                }
                else if (op.State == CoreWebView2DownloadState.Interrupted)
                {
                    QueueManager.Instance.OnBrowserNativeDownloadCompleted(_windowId, false, op.InterruptReason.ToString(), targetPath);
                }
            }
            catch { }
        };
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.WebMessageAsJson;
            if (string.IsNullOrWhiteSpace(json))
                return;

            if (json.Contains("redirect_blocked"))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    var blockedUrl = root.TryGetProperty("url", out var u) ? u.GetString() : null;
                    var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : "JS";
                    IncrementBlockedCount(blockedUrl ?? "JS", reason ?? "JS");
                }
                catch { }
            }
            else if (json.Contains("direct_url"))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("url", out var urlProp))
                {
                    var directUrl = urlProp.GetString();
                    if (!string.IsNullOrWhiteSpace(directUrl))
                    {
                        var userAgent = !string.IsNullOrWhiteSpace(_webView.CoreWebView2.Settings.UserAgent) 
                            ? _webView.CoreWebView2.Settings.UserAgent 
                            : SystemIntegration.HttpUserAgentService.CurrentUserAgent;
                        var referer = _webView.Source?.ToString();
                        var cookieHeader = await GetCookiesForUrlAsync(directUrl);

                        AppLogger.Info($"[Browser Window {_windowId}] Direct URL extracted from page: {directUrl}");
                        await QueueManager.Instance.OnDownloadInterceptedAsync(
                            _windowId,
                            directUrl,
                            cookieHeader,
                            userAgent,
                            referer,
                            null);
                    }
                }
            }
            else if (json.Contains("\"open_url\""))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("url", out var urlProp))
                {
                    var targetUrl = urlProp.GetString();
                    if (!string.IsNullOrWhiteSpace(targetUrl))
                    {
                        if (BrowserSecurityGuard.IsDirectDownloadUrl(targetUrl))
                        {
                            AppLogger.Info($"[Browser Window {_windowId}] Direct download intercepted from open_url: {targetUrl}");
                            var userAgent = !string.IsNullOrWhiteSpace(_webView.CoreWebView2.Settings.UserAgent) 
                                ? _webView.CoreWebView2.Settings.UserAgent 
                                : SystemIntegration.HttpUserAgentService.CurrentUserAgent;
                            var referer = _webView.Source?.ToString();
                            var cookieHeader = await GetCookiesForUrlAsync(targetUrl);

                            await QueueManager.Instance.OnDownloadInterceptedAsync(
                                _windowId,
                                targetUrl,
                                cookieHeader,
                                userAgent,
                                referer,
                                null);
                        }
                        else
                        {
                            var currentUrl = _webView.Source?.ToString() ?? _currentTopLevelUrl;
                            var currentHost = GetHostFromUrl(currentUrl);
                            var currentDomain = !string.IsNullOrEmpty(currentHost) ? BrowserSecurityGuard.GetRegistrableDomain(currentHost) : null;
                            var targetHost = GetHostFromUrl(targetUrl);
                            var targetDomain = !string.IsNullOrEmpty(targetHost) ? BrowserSecurityGuard.GetRegistrableDomain(targetHost) : null;

                            bool isSameDomain = !string.IsNullOrEmpty(currentDomain) &&
                                                !string.IsNullOrEmpty(targetDomain) &&
                                                string.Equals(currentDomain, targetDomain, StringComparison.OrdinalIgnoreCase);

                            bool isSafeHoster = BrowserSecurityGuard.IsKnownSafeOrHosterDomain(targetDomain) ||
                                                BrowserSecurityGuard.IsKnownSafeOrHosterDomain(targetHost);

                            bool isWhitelisted = BrowserSecurityGuard.IsWhitelisted(targetUrl);
                            bool isPortal = BrowserSecurityGuard.IsSearchEngineOrPortal(currentDomain);

                            bool isAllowed = (isSameDomain || isSafeHoster || isWhitelisted || isPortal) &&
                                             !AdBlockRustEngine.Instance.ShouldBlock(targetUrl) &&
                                             !BrowserSecurityGuard.HasDeceptiveRedirectTarget(targetUrl);

                            if (isAllowed)
                            {
                                AppLogger.Info($"[Browser Window {_windowId}] Navigating current window from open_url message: {targetUrl}");
                                _webView.CoreWebView2?.Navigate(targetUrl);
                            }
                            else
                            {
                                IncrementBlockedCount(targetUrl, "open_url_blocked");
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[Browser Window {_windowId}] WebMessage process error: {ex.Message}");
        }
    }

    private void IncrementBlockedCount(string url, string reason)
    {
        BlockedCount++;
        AppLogger.Info($"[Browser Window {_windowId}] Redirect Blocker intercepted: {BlockedCount} ({reason} -> {url})");
        try
        {
            App.Current?.Dispatcher?.InvokeAsync(() => BlockedCountChanged?.Invoke(BlockedCount));
        }
        catch { }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            AppLogger.Warn($"[Browser Window {_windowId}] Navigation Status: {e.WebErrorStatus}");
            return;
        }

        try
        {
            // Polling watcher: scans for up to 60s for direct link / download button.
            // Resilient against Cloudflare challenge reloads and countdown pages.
            var extractScript = @"
            (function() {
                if (window.__sdExtractWatcher) return;
                window.__sdExtractWatcher = true;
                var tries = 0;
                var tick = function() {
                    tries++;
                    try {
                        // Cloudflare challenge page? -> wait
                        var t = document.title || '';
                        if (t.indexOf('Just a moment') >= 0 || document.querySelector('.cf-turnstile, #challenge-form, #challenge-stage')) {
                            if (tries < 120) setTimeout(tick, 500);
                            return;
                        }

                        // Check for direct dl URLs in DOM
                        var dlLinks = Array.from(document.querySelectorAll('a[href*=""/dl/""]'));
                        if (dlLinks.length > 0 && dlLinks[0].href) {
                            window.chrome.webview.postMessage(JSON.stringify({ type: 'direct_url', url: dlLinks[0].href }));
                            return;
                        }

                        // Check script tags for direct links or window.open
                        var scripts = document.querySelectorAll('script');
                        for (var i = 0; i < scripts.length; i++) {
                            var text = scripts[i].textContent || '';
                            var match = text.match(/(https?:\/\/(?:dl[0-9]*\.)?[a-z0-9\-\.]+\.[a-z]+\/dl\/[^\s""'<>]+)/i);
                            if (match && match[1]) {
                                window.chrome.webview.postMessage(JSON.stringify({ type: 'direct_url', url: match[1] }));
                                return;
                            }
                            var matchOpen = text.match(/window\.open\s*\(\s*['""](https?:\/\/[^'""]+\/dl\/[^'""]+)['""]/i);
                            if (matchOpen && matchOpen[1]) {
                                window.chrome.webview.postMessage(JSON.stringify({ type: 'direct_url', url: matchOpen[1] }));
                                return;
                            }
                        }

                        // Auto click download button if available
                        var btn = document.querySelector('button.download-btn, button#download, a.download-btn, .btn-download, button[onclick*=""download""], a[onclick*=""download""]');
                        if (btn) {
                            btn.click();
                            return;
                        }
                    } catch(err) {}
                    if (tries < 120) setTimeout(tick, 500);
                };
                tick();
            })();";
            await _webView.CoreWebView2.ExecuteScriptAsync(extractScript);
        }
        catch { }
    }

    public void Navigate(string url)
    {
        _currentNavigatingUrl = url;
        try
        {
            _webView.CoreWebView2?.Navigate(url);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[Browser Window {_windowId}] Navigate Error", ex);
        }
    }

    public void Clear()
    {
        _currentNavigatingUrl = null;
        try
        {
            _webView.CoreWebView2?.Stop();
            _webView.CoreWebView2?.Navigate("about:blank");
        }
        catch { }
    }
}
