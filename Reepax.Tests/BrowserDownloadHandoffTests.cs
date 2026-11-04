using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Reepax.Models;
using Reepax.Services.Browser;
using Reepax.Services.Download;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;

namespace Reepax.Tests;

public sealed partial class BrowserDownloadHandoffTests
{
    [Theory]
    [InlineData("/popup-query")]
    [InlineData("/popup-fragment")]
    public Task MisleadingPopupFileName_DoesNotReplacePageOrPreventTheDownload(string endpoint) => RunAsync(async fixture =>
    {
        SettingsService.Instance.Settings.EnableAdBlocker = true;
        await fixture.LoadAsync(endpoint);
        var popup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Requested(object? sender, CoreWebView2NewWindowRequestedEventArgs args) => popup.TrySetResult();
        fixture.WebView.CoreWebView2.NewWindowRequested += Requested;
        try
        {
            await fixture.ClickControlAsync();
            await popup.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { fixture.WebView.CoreWebView2.NewWindowRequested -= Requested; }
        Assert.Equal(1, fixture.BlockedPopups);
        Assert.Equal(endpoint, fixture.WebView.Source.AbsolutePath);
        Assert.Equal(DownloadStatus.InBrowser, fixture.Item.Status);
        Assert.Equal(Loc.Format("Status_BrowserWindowWaiting", 1), fixture.Item.StatusMessage);
        Assert.Equal(0, fixture.ClosedWindows);
        Assert.Null(fixture.Item.DirectDownloadUrl);
        Assert.DoesNotContain(fixture.Source.Requests, request => request.Path == "/popup");
        Assert.Equal(0, await fixture.EvaluateAsync<int>("window.downloadClicks"));

        await fixture.ClickControlAsync();
        await fixture.Finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(DownloadStatus.Completed, fixture.Item.Status);
        Assert.Equal(fixture.Source.Bytes, File.ReadAllBytes(fixture.Item.SaveFilePath!));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(fixture.Source.Bytes)).ToLowerInvariant(), fixture.Item.CalculatedChecksum);
        Assert.Contains(fixture.Source.Requests, request => request.Path == "/prepare" && request.Method == "POST");
        Assert.Equal(1, fixture.Source.Requests.Count(request => request.Path == "/file" && request.Method == "GET"));
        Assert.Equal(1, fixture.PublishedDownloads);
        Assert.Equal(1, fixture.CompletedPackages);
        Assert.Equal(1, fixture.ClosedWindows);
        Assert.Equal(1, fixture.OpenedWindows);
        Assert.False(File.Exists(fixture.Item.SaveFilePath + ".part"));
    });

    [Fact]
    public Task PopupIntermediatePage_RemainsInBrowserUntilARealDownloadAndCompletesOnce() => RunAsync(async fixture =>
    {
        await fixture.LoadAsync("/start");
        await fixture.NavigateThroughAsync("/dl/intermediate", "window.open('/dl/intermediate')");
        Assert.Equal(0, fixture.ClosedWindows);
        Assert.Equal(DownloadStatus.InBrowser, fixture.Item.Status);
        Assert.Null(fixture.Item.DirectDownloadUrl);
        Assert.False(File.Exists(fixture.Item.SaveFilePath));
        await fixture.ClickDownloadAsync();
        await fixture.Finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(DownloadStatus.Completed, fixture.Item.Status);
        Assert.Equal(fixture.Source.Bytes, File.ReadAllBytes(fixture.Item.SaveFilePath!));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(fixture.Source.Bytes)).ToLowerInvariant(), fixture.Item.CalculatedChecksum);
        Assert.Equal(1, fixture.ClosedWindows);
        Assert.Equal(1, fixture.PublishedDownloads);
        Assert.Equal(1, fixture.CompletedPackages);
        Assert.Equal(1, fixture.OpenedWindows);
        Assert.Contains(fixture.Source.Requests, request => request.Path == "/file" && request.Browser && request.Cookie.Contains("example_session=ready") && request.Referer.EndsWith("/dl/intermediate"));
        Assert.Equal(1, fixture.Source.Requests.Count(request => request.Path == "/file" && request.Method == "GET"));
        Assert.False(File.Exists(fixture.Item.SaveFilePath + ".part"));
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task CandidateMessages_NavigateOnlyInAutomaticModeAndNeverClaimHtml(bool automatic, bool stringMessage) => RunAsync(async fixture =>
    {
        fixture.Package.AutoResolveHostLinks = automatic;
        await fixture.LoadAsync("/start");
        string message = JsonSerializer.Serialize(new { type = "direct_url", url = fixture.Source.Url("/dl/intermediate") });
        string expression = "chrome.webview.postMessage(" + (stringMessage ? JsonSerializer.Serialize(message) : message) + ")";
        if (automatic) await fixture.NavigateThroughAsync("/dl/intermediate", expression);
        else
        {
            await fixture.WebView.ExecuteScriptAsync(expression);
            await fixture.WebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new
            {
                expression = "new Promise(resolve => setTimeout(resolve, 0))", awaitPromise = true
            }));
            Assert.EndsWith("/start", fixture.WebView.Source.ToString());
        }
        Assert.Equal(DownloadStatus.InBrowser, fixture.Item.Status);
        Assert.Null(fixture.Item.DirectDownloadUrl);
        Assert.Equal(0, fixture.ClosedWindows);
        Assert.False(File.Exists(fixture.Item.SaveFilePath));
    });

    [Fact]
    public Task ManualMode_DoesNotAutomaticallyClickDownloadButtons() => RunAsync(async fixture =>
    {
        fixture.Package.AutoResolveHostLinks = false;
        await fixture.LoadAsync("/manual");
        Assert.True(await fixture.EvaluateAsync<bool>("document.querySelector('button').onclick !== null"));
        Assert.False(await fixture.EvaluateAsync<bool>("window.__sdExtractWatcher === true"));
        Assert.Equal(0, fixture.ClosedWindows);
        await fixture.ClickDownloadAsync();
        await fixture.Finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(DownloadStatus.Completed, fixture.Item.Status);
        Assert.Equal(fixture.Source.Bytes, File.ReadAllBytes(fixture.Item.SaveFilePath!));
    });

    [Theory]
    [InlineData("/post")]
    [InlineData("/once")]
    [InlineData("/post", "_blank")]
    [InlineData("/post", "new-window")]
    [InlineData("/post", "open")]
    [InlineData("/post", "_blank", true)]
    public Task NonReplayableBrowserTransfers_CompleteFromTheOriginalResponseExactlyOnce(string endpoint, string? target = null, bool submitterTarget = false) => RunAsync(async fixture =>
    {
        await fixture.LoadAsync("/start");
        string expression = endpoint == "/post"
            ? "(()=>{let form=document.createElement('form');form.method='POST';form.action='/post';form.innerHTML='<input name=example value=payload>';" +
                "document.body.append(form);" + (submitterTarget
                    ? "let button=document.createElement('button');button.formTarget=" + JsonSerializer.Serialize(target) + ";form.append(button);form.requestSubmit(button)"
                    : "form.target=" + JsonSerializer.Serialize(target ?? "_self") + ";form.submit()") + "})()"
            : "location.href='/once'";
        await fixture.WebView.ExecuteScriptAsync(expression);
        await fixture.Finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(DownloadStatus.Completed, fixture.Item.Status);
        Assert.Equal(fixture.Source.Bytes, File.ReadAllBytes(fixture.Item.SaveFilePath!));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(fixture.Source.Bytes)).ToLowerInvariant(), fixture.Item.CalculatedChecksum);
        Assert.Equal(1, fixture.CompletedPackages);
        Assert.Equal(1, fixture.PublishedDownloads);
        Assert.True(fixture.Package.HasCompletedNotified);
        var request = Assert.Single(fixture.Source.Requests.Where(request => request.Path == endpoint));
        Assert.Equal(endpoint == "/post" ? "POST" : "GET", request.Method);
        Assert.Equal(endpoint == "/post" ? "example=payload" : "", request.Body);
        Assert.True(request.Browser);
        Assert.Equal(1, fixture.ClosedWindows);
        Assert.Equal(1, fixture.OpenedWindows);
        Assert.False(File.Exists(fixture.Item.SaveFilePath + ".part"));
        Assert.False(File.Exists(fixture.Item.SaveFilePath + ".part.segments"));
    });

    private static async Task RunAsync(Func<BrowserHandoffFixture, Task> action, int payloadLength = 8192)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await using var fixture = await BrowserHandoffFixture.CreateAsync(payloadLength);
                    await action(fixture);
                    done.TrySetResult();
                }
                catch (Exception exception) { done.TrySetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(75)); }
        finally { Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Browser test dispatcher did not stop."); }
    }
}

internal sealed class BrowserHandoffFixture : IAsyncDisposable, IBrowserWindowHost
{
    private readonly string _root = Directory.CreateTempSubdirectory("BrowserHandoff_").FullName;
    private readonly QueueManager _queue = QueueManager.Instance;
    private readonly DownloadEngine _engine = DownloadEngine.Instance;
    private readonly DownloadPackage[] _previousPackages;
    private readonly IBrowserWindowHost? _previousHost;
    private readonly bool _testMode;
    private readonly (bool Adblock, bool Verification, int Retries, bool Notification, bool CompletionSound, bool ErrorSound) _settings;
    private readonly int _previousConnections;
    private readonly int _previousMaximum;
    private readonly Func<int>? _previousActiveCount;
    private readonly long _previousSpeedLimit;
    private readonly PostDownloadAction _previousAction;
    private readonly string _previousUserAgent;
    private Window _window = null!;
    private BrowserWindowController _controller = null!;
    private CoreWebView2Environment _environment = null!;
    private readonly HashSet<int> _closedWindowIds = new();
    internal WebView2 WebView { get; private set; } = new();
    internal BrowserHandoffSource Source { get; }
    internal QueueManager Queue => _queue;
    internal DownloadItem Item { get; private set; } = null!;
    internal DownloadPackage Package { get; private set; } = null!;
    internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int ClosedWindows { get; private set; }
    internal int OpenedWindows { get; private set; }
    internal int PublishedDownloads { get; private set; }
    internal int CompletedPackages { get; private set; }
    internal int BlockedPopups => _controller.BlockedCount;
    public event Action? WindowCapacityChanged { add { } remove { } }
    public int ActiveWindowCount => OpenedWindows - ClosedWindows;

    private BrowserHandoffFixture(int payloadLength)
    {
        Source = new BrowserHandoffSource(payloadLength);
        _previousUserAgent = HttpUserAgentService.CurrentUserAgent;
        _testMode = DownloadPersistenceService.IsTestEnvironment;
        DownloadPersistenceService.IsTestEnvironment = true;
        _previousPackages = _queue.Packages.ToArray();
        _previousHost = _queue.BrowserHost;
        _queue.StopQueue();
        _queue.Packages.Clear();
        _queue.BrowserHost = this;
        _previousMaximum = _queue.MaxConcurrentDownloads;
        _previousActiveCount = _queue.ActiveDownloadsCountOverride;
        _queue.ActiveDownloadsCountOverride = null;
        _queue.MaxConcurrentDownloads = 1;
        var settings = SettingsService.Instance.Settings;
        _settings = (settings.EnableAdBlocker, settings.EnableChecksumVerification, settings.MaxAutoRetryOnCorruption,
            settings.EnableCompletionNotifications, settings.EnableCompletionSound, settings.EnableErrorSound);
        settings.EnableAdBlocker = false;
        settings.EnableChecksumVerification = true;
        settings.MaxAutoRetryOnCorruption = 0;
        settings.EnableCompletionNotifications = settings.EnableCompletionSound = settings.EnableErrorSound = false;
        _previousAction = PostDownloadActionService.Instance.CurrentAction;
        PostDownloadActionService.Instance.CurrentAction = PostDownloadAction.None;
        _previousConnections = _engine.MaxConnectionsPerDownload;
        _engine.MaxConnectionsPerDownload = 1;
        _previousSpeedLimit = _engine.CurrentSpeedLimit;
        _engine.SetSpeedLimit(0);
    }

    internal static async Task<BrowserHandoffFixture> CreateAsync(int payloadLength = 8192)
    {
        var fixture = new BrowserHandoffFixture(payloadLength);
        try
        {
            fixture.Package = new DownloadPackage
            {
                Name = "Example package", SaveDirectory = Path.Combine(fixture._root, "downloads"),
                AutoExtractArchives = false, AutoResolveHostLinks = false, IsCustomName = true
            };
            fixture.Item = new DownloadItem
            {
                PackageId = fixture.Package.Id, OriginalUrl = fixture.Source.Url("/start"), FileName = "payload.zip",
                SaveFilePath = Path.Combine(fixture.Package.SaveDirectory, "payload.zip"), IsCustomName = true,
                Status = DownloadStatus.Queued
            };
            fixture.Package.Items.Add(fixture.Item);
            fixture._queue.Packages.Add(fixture.Package);
            fixture._engine.DownloadCompleted += fixture.OnCompleted;
            fixture.Item.PropertyChanged += fixture.OnChanged;
            fixture.Package.PropertyChanged += fixture.OnChanged;
            fixture._window = new Window { Width = 500, Height = 400, Left = -20000, Top = -20000, ShowInTaskbar = false, Content = fixture.WebView };
            fixture._window.Show();
            fixture._environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(fixture._root, "profile"));
            await fixture.WebView.EnsureCoreWebView2Async(fixture._environment);
            fixture._controller = new BrowserWindowController(1, fixture.WebView, fixture._queue);
            await fixture._controller.ConfigureBrowserAsync();
            fixture._queue.StartQueue();
            Assert.Same(fixture.Item, fixture._queue.GetWindowItem(1));
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }

    public bool TryOpenWindow(DownloadItem item, bool hidden, out int windowId)
    {
        OpenedWindows++;
        windowId = OpenedWindows;
        return true;
    }
    public void CloseWindow(int windowId)
    {
        lock (_closedWindowIds)
            if (!_closedWindowIds.Add(windowId)) return;
        ClosedWindows++;
        _queue.OnBrowserWindowClosed(windowId);
        TryFinish();
    }
    public void CloseAllWindows() { }
    private void OnCompleted(DownloadItem item)
    {
        if (item != Item) return;
        PublishedDownloads++;
        TryFinish();
    }
    private void OnChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender == Package && args.PropertyName == nameof(DownloadPackage.IsNewlyCompleted) && Package.IsNewlyCompleted)
            CompletedPackages++;
        TryFinish();
    }
    private void TryFinish()
    {
        // Publication and package-completion callbacks may arrive in either order.
        if (ClosedWindows > 0 && (Item.Status == DownloadStatus.Failed || (PublishedDownloads > 0 && CompletedPackages > 0 &&
            Item.Status == DownloadStatus.Completed && !Item.IsVerifyingIntegrity && Package.HasCompletedNotified)))
            Finished.TrySetResult();
    }
    internal async Task LoadAsync(string endpoint) => await NavigateThroughAsync(endpoint, null);
    internal async Task NavigateThroughAsync(string endpoint, string? expression)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (WebView.Source.AbsolutePath == endpoint && args.IsSuccess) reached.TrySetResult();
        }
        WebView.CoreWebView2.NavigationCompleted += Completed;
        try
        {
            if (expression == null) _controller.Navigate(Source.Url(endpoint));
            else await WebView.ExecuteScriptAsync(expression);
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally { WebView.CoreWebView2.NavigationCompleted -= Completed; }
    }
    internal async Task ClickDownloadAsync()
        => await WebView.ExecuteScriptAsync("document.querySelector('button').click();document.querySelector('button').click()");
    internal async Task ClickControlAsync()
    {
        using var point = JsonDocument.Parse(await WebView.ExecuteScriptAsync(
            "(()=>{const r=document.querySelector('button').getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2}})()"));
        foreach (string type in new[] { "mouseMoved", "mousePressed", "mouseReleased" })
            await WebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new
            {
                type, x = point.RootElement.GetProperty("x").GetDouble(), y = point.RootElement.GetProperty("y").GetDouble(),
                button = "left", clickCount = 1
            }));
    }
    internal async Task<T> EvaluateAsync<T>(string expression)
        => JsonSerializer.Deserialize<T>(await WebView.ExecuteScriptAsync(expression))!;

    internal async Task ReopenAsync()
    {
        _controller.Dispose();
        _window.Close();
        WebView = new WebView2();
        _window = new Window { Width = 500, Height = 400, Left = -20000, Top = -20000, ShowInTaskbar = false, Content = WebView };
        _window.Show();
        await WebView.EnsureCoreWebView2Async(_environment);
        _controller = new BrowserWindowController(OpenedWindows, WebView, _queue);
        await _controller.ConfigureBrowserAsync();
    }

    internal void CloseBrowser()
    {
        _controller.Dispose();
        _window.Close();
        CloseWindow(OpenedWindows);
    }

    internal Task SubmitAsync(string endpoint)
        => WebView.ExecuteScriptAsync("(()=>{const f=document.createElement('form');f.method='POST';f.action=" + JsonSerializer.Serialize(endpoint) + ";document.body.append(f);f.submit()})()");

    internal static async Task UntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition()) await Task.Delay(20, deadline.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _queue.StopQueue();
        if (Item != null)
        {
            await _engine.PauseAllAsync();
            _engine.DownloadCompleted -= OnCompleted;
            Item.PropertyChanged -= OnChanged;
            Package.PropertyChanged -= OnChanged;
        }
        var environment = _environment;
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void BrowserExited(object? sender, CoreWebView2BrowserProcessExitedEventArgs args) => exited.TrySetResult();
        if (environment != null) environment.BrowserProcessExited += BrowserExited;
        _controller?.Dispose();
        _window?.Close();
        await Source.DisposeAsync();
        _queue.Packages.Clear();
        foreach (var package in _previousPackages) _queue.Packages.Add(package);
        _queue.BrowserHost = _previousHost;
        _queue.MaxConcurrentDownloads = _previousMaximum;
        _queue.ActiveDownloadsCountOverride = _previousActiveCount;
        _engine.SetSpeedLimit(_previousSpeedLimit);
        _engine.MaxConnectionsPerDownload = _previousConnections;
        HttpUserAgentService.UpdateUserAgent(_previousUserAgent);
        PostDownloadActionService.Instance.CancelCountdown();
        PostDownloadActionService.Instance.CurrentAction = _previousAction;
        var settings = SettingsService.Instance.Settings;
        (settings.EnableAdBlocker, settings.EnableChecksumVerification, settings.MaxAutoRetryOnCorruption,
            settings.EnableCompletionNotifications, settings.EnableCompletionSound, settings.EnableErrorSound) = _settings;
        DownloadPersistenceService.IsTestEnvironment = _testMode;
        if (environment != null)
        {
            try { await exited.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { environment.BrowserProcessExited -= BrowserExited; }
        }
        Directory.Delete(_root, recursive: true);
    }
}

internal sealed class BrowserHandoffSource : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<Task> _connections = [];
    private readonly Task _accept;
    private int _once;
    internal ConcurrentQueue<(string Method, string Path, string Cookie, string Referer, bool Browser, string Body,
        string Range, string IfRange, string UserAgent)> Requests { get; } = new();
    internal byte[] Bytes { get; }
    internal bool IncludeEntityTag { get; set; } = true;

    internal bool HoldBody { get; set; }
    internal TaskCompletionSource BodyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ReleaseBody { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal BrowserHandoffSource(int payloadLength = 8192)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("example.bin", CompressionLevel.NoCompression);
            entry.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var output = entry.Open();
            output.Write(Enumerable.Range(0, payloadLength).Select(index => (byte)(index * 31)).ToArray());
        }
        Bytes = memory.ToArray();
        _listener.Start();
        _accept = AcceptAsync();
    }
    internal string Url(string endpoint) => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{endpoint}";
    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
                _connections.Add(ServeAsync(await _listener.AcceptTcpClientAsync(_stop.Token)));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                string[] line = (await reader.ReadLineAsync(_stop.Token) ?? "").Split(' ');
                if (line.Length < 2) return;
                string cookie = "", referer = "", range = "", ifRange = "", userAgent = "";
                int contentLength = 0;
                bool browser = false;
                while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } header)
                {
                    if (header.StartsWith("Sec-Fetch-Mode:", StringComparison.OrdinalIgnoreCase)) browser = true;
                    if (header.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)) cookie = header[7..].Trim();
                    if (header.StartsWith("Referer:", StringComparison.OrdinalIgnoreCase)) referer = header[8..].Trim();
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) contentLength = int.Parse(header[15..].Trim());
                    if (header.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) range = header[6..].Trim();
                    if (header.StartsWith("If-Range:", StringComparison.OrdinalIgnoreCase)) ifRange = header[9..].Trim();
                    if (header.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase)) userAgent = header[11..].Trim();
                }
                char[] requestBody = new char[contentLength];
                int read = contentLength > 0 ? await reader.ReadBlockAsync(requestBody.AsMemory(), _stop.Token) : 0;
                if (read != contentLength) return;
                string endpoint = line[1].Split('?')[0];
                Requests.Enqueue((line[0], endpoint, cookie, referer, browser, new string(requestBody), range, ifRange, userAgent));
                bool file = endpoint is "/file" or "/gzip" or "/binary" or "/empty" or "/html" or "/short" ||
                    (endpoint == "/resume" && cookie.Contains("example_session=ready") && referer.EndsWith("/start")) ||
                    (endpoint == "/post" && line[0] == "POST") || (endpoint == "/once" && Interlocked.Increment(ref _once) == 1);
                string body = endpoint == "/start" ? "<p>Choose a download.</p>" : endpoint is "/dl/intermediate" or "/manual"
                    ? $"<button id='{(endpoint == "/manual" ? "download" : "manual-choice")}' onclick=\"window.open('/file')\">Download</button>" : "<p>File request expired.</p>";
                if (endpoint is "/popup-query" or "/popup-fragment") body = PopupPage(endpoint == "/popup-fragment");
                byte[] bytes = file ? Bytes : endpoint == "/prepare" ? Encoding.UTF8.GetBytes("{\"url\":\"/file\"}")
                    : Encoding.UTF8.GetBytes("<!doctype html><html><body>" + body + "</body></html>");
                if (endpoint == "/empty") bytes = [];
                if (endpoint == "/html") bytes = Encoding.UTF8.GetBytes("<!doctype html><html><body>Request expired</body></html>");
                if (endpoint == "/gzip")
                {
                    using var compressed = new MemoryStream();
                    using (var gzip = new GZipStream(compressed, CompressionMode.Compress, true)) gzip.Write(bytes);
                    bytes = compressed.ToArray();
                }
                int offset = 0;
                bool partial = endpoint == "/resume" && file && range.StartsWith("bytes=", StringComparison.Ordinal) &&
                    int.TryParse(range[6..].Split('-')[0], out offset) && offset > 0 && offset < Bytes.Length;
                if (partial) bytes = bytes[offset..];
                string contentType = endpoint == "/binary" ? "application/x-download" : file ? "application/zip" : endpoint == "/prepare" ? "application/json" : "text/html";
                string status = partial ? "206 Partial Content" : "200 OK";
                string headers = $"HTTP/1.1 {status}\r\nContent-Length: {bytes.Length}\r\nContent-Type: {contentType}\r\n" +
                    (partial ? $"Content-Range: bytes {offset}-{Bytes.Length - 1}/{Bytes.Length}\r\n" : "") +
                    (endpoint == "/resume" ? "Accept-Ranges: bytes\r\n" + (IncludeEntityTag ? "ETag: \"sample-v1\"\r\n" : "") : "") +
                    (endpoint == "/gzip" ? "Content-Encoding: gzip\r\n" : "") +
                    (file && endpoint != "/binary" ? "Content-Disposition: attachment; filename=payload.zip\r\n" : "") + "Set-Cookie: example_session=ready; Path=/\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), _stop.Token);
                if (line[0] != "HEAD")
                {
                    if (file && HoldBody && !partial)
                    {
                        int prefix = Math.Min(bytes.Length, 128 * 1024);
                        await stream.WriteAsync(bytes.AsMemory(0, prefix), _stop.Token);
                        BodyStarted.TrySetResult();
                        await ReleaseBody.Task.WaitAsync(_stop.Token);
                        await stream.WriteAsync(bytes.AsMemory(prefix), _stop.Token);
                    }
                    else await stream.WriteAsync(bytes.AsMemory(0, endpoint == "/short" ? bytes.Length - 17 : bytes.Length), _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (IOException) { }
        }
    }
    private string PopupPage(bool fragment)
    {
        string popupUrl = Url("/popup" + (fragment ? "#setup.rar" : "?file=setup.zip")).Replace("127.0.0.1", "localhost");
        return $$"""
            <button id='download'>Download</button>
            <script>
            let firstClick = true;
            window.downloadClicks = 0;
            document.addEventListener('click', event => {
                if (!firstClick || event.target.id !== 'download') return;
                firstClick = false;
                event.preventDefault();
                event.stopPropagation();
                const popup = document.createElement('a');
                popup.href = {{JsonSerializer.Serialize(popupUrl)}};
                popup.target = '_blank';
                popup.rel = 'noopener';
                document.body.append(popup);
                popup.click();
                popup.remove();
            }, true);
            document.querySelector('button').addEventListener('click', async () => {
                window.downloadClicks++;
                const response = await fetch('/prepare', { method: 'POST' });
                location.href = (await response.json()).url;
            });
            </script>
            """;
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _accept; await Task.WhenAll(_connections); }
        finally { _stop.Dispose(); }
    }
}
