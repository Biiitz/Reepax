using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Reepax.Services.Browser;
using Xunit;

namespace Reepax.Tests;

public sealed class BrowserScriptTestHost : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "BrowserScriptCase_" + Guid.NewGuid().ToString("N"));
    private readonly TaskCompletionSource<Dispatcher> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private Dispatcher _dispatcher = null!;
    private CoreWebView2Controller _controller = null!;
    private nint _window;
    internal CoreWebView2 Core => _controller.CoreWebView2;
    internal int BlockedPopups { get; private set; }
    internal List<string> NativePopups { get; } = new();
    internal bool BlockPopups { get; set; } = true;

    public BrowserScriptTestHost()
    {
        _thread = new Thread(() =>
        {
            _ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        }) { IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public async Task InitializeAsync()
    {
        _dispatcher = await _ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await OnUi(async () =>
        {
            _window = CreateWindowEx(0x08000080, "STATIC", "Browser test", unchecked((int)0x80000000),
                -20000, -20000, 800, 600, 0, 0, 0, 0);
            if (_window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            ShowWindow(_window, 4);
            var pages = Path.Combine(_root, "Pages");
            Directory.CreateDirectory(pages);
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(_root, "Profile"),
                new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--disable-background-timer-throttling --disable-renderer-backgrounding" });
            _controller = await environment.CreateCoreWebView2ControllerAsync(_window);
            _controller.Bounds = new Rectangle(0, 0, 800, 600);
            _controller.IsVisible = true;
            Core.SetVirtualHostNameToFolderMapping("page.example.test", pages, CoreWebView2HostResourceAccessKind.DenyCors);
            Core.AddHostObjectToScript("reepaxPopupGuard", new BrowserWindowController.PopupGuardBridge((url, _) =>
            {
                bool blocked = BlockPopups && new Uri(url).Host == "ads.unrelated.test";
                if (blocked) BlockedPopups++;
                return blocked;
            }, () => true));
            await Core.AddScriptToExecuteOnDocumentCreatedAsync("window.__nativeAdd = EventTarget.prototype.addEventListener;");
            await Core.AddScriptToExecuteOnDocumentCreatedAsync(BrowserSecurityGuard.GetAntiPopupScript());
            Core.NewWindowRequested += (_, args) => { NativePopups.Add(args.Uri); args.Handled = true; };
        });
    }

    internal Task OnUi(Func<Task> action) => _dispatcher.InvokeAsync(action).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(40));

    internal async Task LoadAsync(string body, string script)
    {
        BlockedPopups = 0;
        NativePopups.Clear();
        BlockPopups = true;
        var html = "<!doctype html><html><head><style>button { width:200px; height:60px; }</style></head><body>" +
            body + "<script>" + script + "</script></body></html>";
        File.WriteAllText(Path.Combine(_root, "Pages", "index.html"), html);
        var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs args) => loaded.TrySetResult(args.IsSuccess);
        Core.NavigationCompleted += Completed;
        try
        {
            Core.Navigate("https://page.example.test/index.html?run=" + Guid.NewGuid().ToString("N"));
            Assert.True(await loaded.Task.WaitAsync(TimeSpan.FromSeconds(20)));
        }
        finally { Core.NavigationCompleted -= Completed; }
    }

    internal async Task ClickAsync()
    {
        using var point = JsonDocument.Parse(await Core.ExecuteScriptAsync(
            "(()=>{let r=document.querySelector('button').getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2}})()"));
        double x = point.RootElement.GetProperty("x").GetDouble(), y = point.RootElement.GetProperty("y").GetDouble();
        foreach (string type in new[] { "mouseMoved", "mousePressed", "mouseReleased" })
            await Core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                JsonSerializer.Serialize(new { type, x, y, button = "left", clickCount = 1 }));
        await Core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new
        {
            expression = "new Promise(resolve => setTimeout(resolve, 0))", awaitPromise = true, returnByValue = true
        }));
    }

    internal async Task<T> EvaluateAsync<T>(string expression)
        => JsonSerializer.Deserialize<T>(await Core.ExecuteScriptAsync(expression))!;

    public async Task DisposeAsync()
    {
        await OnUi(() =>
        {
            _controller?.Close();
            if (_window != 0) DestroyWindow(_window);
            return Task.CompletedTask;
        });
        _dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        _thread.Join(TimeSpan.FromSeconds(10));
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(int extendedStyle, string className, string title, int style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);
}
