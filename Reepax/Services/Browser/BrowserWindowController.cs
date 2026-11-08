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
using Reepax.Services.Localization;
using System.Text.Json;

namespace Reepax.Services.Browser;

/// <summary>
/// Controls the WebView2 instance of an external browser window (Scenario C):
/// AdBlock, popup redirection, download interception, and direct link extraction.
/// </summary>
public partial class BrowserWindowController : IDisposable
{
    private readonly int _windowId;
    private readonly WebView2 _webView;
    private readonly QueueManager _queue;
    private bool _downloadClaimed;
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
        : this(windowId, webView, QueueManager.Instance) { }

    internal BrowserWindowController(int windowId, WebView2 webView, QueueManager queue)
    {
        _windowId = windowId;
        _webView = webView;
        _queue = queue;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        StopResponseTransfers();

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
                _webView.CoreWebView2.RemoveHostObjectFromScript(PopupGuardHostName);
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
                AppLogger.Warn($"[Extensions] Installation failed", ex);
            }
            finally
            {
                _extensionsLock.Release();
            }

            if (_isDisposed) return;

            await ConfigureBrowserAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[Browser Window {_windowId}] Init Error", ex);
        }
    }

    internal async Task ConfigureBrowserAsync()
    {
        if (_isInitialized || _isDisposed || _webView.CoreWebView2 == null) return;
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
        _webView.CoreWebView2.AddHostObjectToScript(PopupGuardHostName,
            new PopupGuardBridge(TryBlockPopup, ShouldApplyCosmeticFiltering));
        await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(BrowserSecurityGuard.GetAntiPopupScript());

        // 7. Inject Cosmetic Filter CSS
        var cosmeticScript = $@"
        (function() {{
            try {{
                if (!window.chrome.webview.hostObjects.sync.{PopupGuardHostName}.ShouldApplyCosmeticFiltering()) return;
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

        await ConfigureResponseTransfersAsync();
        _isInitialized = true;
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
                BrowserSecurityGuard.HasDirectDownloadPath(targetUri.ToString()) ||
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

    private async Task<DownloadCookieContext?> GetCookiesForUrlAsync(string url)
    {
        try
        {
            if (_webView.CoreWebView2?.CookieManager != null)
            {
                var cookies = await _webView.CoreWebView2.CookieManager.GetCookiesAsync(url);
                if (cookies != null && cookies.Count > 0)
                {
                    return DownloadCookieContext.FromBrowserCookies(url, cookies.Select(cookie => cookie.ToSystemNetCookie()));
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

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Suppress opening separate desktop window
        e.Handled = true;

        var url = e.Uri;
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase))
            return;

        if (!IsNavigableAddress(url) || _downloadClaimed || TryBlockPopup(url, e.IsUserInitiated))
            return;

        // Execute legitimate popup navigations in the current window
        AppLogger.Info($"[Browser Window {_windowId}] Navigating current window to legitimate popup target: {url}");
        try
        {
            // Keep the document's referrer policy when following an allowed popup link.
            _ = _webView.CoreWebView2?.ExecuteScriptAsync("location.assign(" + JsonSerializer.Serialize(url) + ")");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[Browser Window {_windowId}] Failed to navigate current window to popup target {url}", ex);
        }
    }

    private async void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        if (_isDisposed || _downloadClaimed || _queue.GetWindowItem(_windowId) is not { IsUnavailableForProcessing: false })
        {
            e.Cancel = true;
            e.Handled = true;
            return;
        }
        _downloadClaimed = true;
        try
        {
            var directUrl = e.DownloadOperation.Uri;

            // blob: downloads exist only in browser context -> HttpClient cannot fetch them.
            // WebView2 downloads natively; we configure destination path & track progress.
            if (directUrl.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
            {
                var deferral = e.GetDeferral();
                e.Cancel = true;
                e.Handled = true;
                try
                {
                    using (await PackagePathOperationGate.Instance.EnterAsync())
                    {
                        if (!_isDisposed)
                            HandleBlobDownload(e, directUrl);
                    }
                }
                finally
                {
                    deferral.Complete();
                }
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

            var cookieContext = await GetCookiesForUrlAsync(directUrl);

            AppLogger.Info($"[Browser Window {_windowId}] Intercepted Download! URL: {directUrl}, File: {suggestedFileName}");

            // 3. Hand off to QueueManager -> DownloadEngine
            await _queue.OnDownloadInterceptedAsync(
                _windowId, 
                directUrl, 
                cookieContext?.GetCookieHeader(),
                userAgent, 
                referer, 
                suggestedFileName,
                cookieContext);
        }
        catch (Exception ex)
        {
            if (!_isDisposed && _queue.GetWindowItem(_windowId) != null) _downloadClaimed = false;
            AppLogger.Error($"[Browser Window {_windowId}] Error during download interception", ex);
        }
    }

    /// <summary>
    /// blob: downloads run natively via WebView2 (they exist only in the browser context).
    /// Target path is set to the package directory; progress/completion are forwarded to app UI.
    /// </summary>
    private void HandleBlobDownload(CoreWebView2DownloadStartingEventArgs e, string blobUrl)
    {
        var item = _queue.GetWindowItem(_windowId);
        if (item == null || item.IsUnavailableForProcessing)
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
        e.Cancel = true;
        e.Handled = true;
        if (!string.IsNullOrWhiteSpace(targetPath))
        {
            e.ResultFilePath = targetPath;
        }

        var op = e.DownloadOperation;
        var lastBytes = 0L;
        var lastTime = DateTime.Now;

        item.ClearDownloadFailure();
        item.Status = DownloadStatus.Downloading;
        item.StatusMessage = Loc.Get("Status_Downloading");
        if (!string.IsNullOrWhiteSpace(targetPath))
        {
            if (!item.IsCustomName)
            {
                item.Rename(Path.GetFileName(targetPath), isUserAction: false);
            }
            var pkg = _queue.Packages.FirstOrDefault(p => p.Id == item.PackageId || p.Items.Contains(item));
            if (pkg != null && !pkg.IsCustomName)
            {
                Extractor.LinkMetadataResolverService.TryUpdatePackageNameUnderLease(pkg);
            }
        }

        AppLogger.Info($"[Browser Window {_windowId}] Native blob download adopted: {blobUrl}");

        op.BytesReceivedChanged += (_, _) =>
        {
            try
            {
                if (item.IsUnavailableForProcessing)
                    return;
                var now = DateTime.Now;
                var dt = (now - lastTime).TotalSeconds;
                if (dt >= 0.3)
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

        var nativeSession = RegisterNativeTransfer(item, op.Cancel);
        if (nativeSession == null)
            return;
        void HandleNativeState()
        {
            try
            {
                var state = op.State;
                if (state is not (CoreWebView2DownloadState.Completed or CoreWebView2DownloadState.Interrupted) ||
                    !CompleteNativeTransfer(nativeSession))
                    return;

                if (state == CoreWebView2DownloadState.Completed)
                {
                    _queue.OnBrowserNativeDownloadCompleted(_windowId, true, null, targetPath);
                }
                else if (nativeSession.StopRequested)
                {
                    _queue.OnNativeTransferStopped(_windowId);
                }
                else
                {
                    _queue.OnBrowserNativeDownloadCompleted(_windowId, false, op.InterruptReason.ToString(), targetPath);
                }
            }
            catch { }
        }

        op.StateChanged += (_, _) => HandleNativeState();
        HandleNativeState();
        e.Cancel = false;
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_isDisposed || _downloadClaimed || !IsCurrentDocument(e.Source)) return;
        try
        {
            using var message = ParseBrowserMessage(e.WebMessageAsJson);
            if (message?.RootElement is not { ValueKind: JsonValueKind.Object } root ||
                !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("url", out var address) || address.ValueKind != JsonValueKind.String)
                return;
            string? url = address.GetString();
            if (!IsNavigableAddress(url)) return;
            switch (type.GetString())
            {
                case "redirect_blocked":
                    IncrementBlockedCount(url!, "Script");
                    break;
                case "direct_url" when ShouldAutomaticallyResolve():
                case "open_url":
                    if (!string.Equals(url, _webView.CoreWebView2.Source, StringComparison.Ordinal) && !TryBlockPopup(url!, false))
                        _ = _webView.CoreWebView2.ExecuteScriptAsync("location.assign(" + JsonSerializer.Serialize(url) + ")");
                    break;
            }
        }
        catch (JsonException) { }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }

    internal static JsonDocument? ParseBrowserMessage(string json)
    {
        if (json.Length > 32768) return null;
        var message = JsonDocument.Parse(json);
        if (message.RootElement.ValueKind != JsonValueKind.String) return message;
        string? nested = message.RootElement.GetString();
        message.Dispose();
        return string.IsNullOrWhiteSpace(nested) ? null : JsonDocument.Parse(nested);
    }

    private bool IsCurrentDocument(string source)
        => Uri.TryCreate(source, UriKind.Absolute, out var sender) &&
           Uri.TryCreate(_webView.CoreWebView2?.Source, UriKind.Absolute, out var current) &&
           Uri.Compare(sender, current, UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
               UriFormat.SafeUnescaped, StringComparison.Ordinal) == 0;

    private static bool IsNavigableAddress(string? address)
        => Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
           (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == "blob");

    private bool ShouldAutomaticallyResolve()
    {
        var item = _queue.GetWindowItem(_windowId);
        return item is { IsUnavailableForProcessing: false } &&
            _queue.Packages.FirstOrDefault(package => package.Id == item.PackageId || package.Items.Contains(item))?.AutoResolveHostLinks == true;
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
        if (_isDisposed || _downloadClaimed || !ShouldAutomaticallyResolve()) return;
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
                            window.chrome.webview.postMessage({ type: 'direct_url', url: dlLinks[0].href });
                            return;
                        }

                        // Check script tags for direct links or window.open
                        var scripts = document.querySelectorAll('script');
                        for (var i = 0; i < scripts.length; i++) {
                            var text = scripts[i].textContent || '';
                            var match = text.match(/(https?:\/\/(?:dl[0-9]*\.)?[a-z0-9\-\.]+\.[a-z]+\/dl\/[^\s""'<>]+)/i);
                            if (match && match[1]) {
                                window.chrome.webview.postMessage({ type: 'direct_url', url: match[1] });
                                return;
                            }
                            var matchOpen = text.match(/window\.open\s*\(\s*['""](https?:\/\/[^'""]+\/dl\/[^'""]+)['""]/i);
                            if (matchOpen && matchOpen[1]) {
                                window.chrome.webview.postMessage({ type: 'direct_url', url: matchOpen[1] });
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
