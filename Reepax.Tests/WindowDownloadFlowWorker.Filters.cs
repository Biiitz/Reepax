using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Web.WebView2.Core;
using Reepax.Models;
using Reepax.Services.AdBlock;
using Reepax.Services.AdBlock.Native;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.Views;
using Xunit;

namespace Reepax.Tests;

internal static partial class WindowDownloadFlowWorker
{
    private static async Task<CoreWebView2Environment> CheckFilterUpdatesAsync(string language)
    {
        SettingsService.Instance.Settings.EnableAdBlocker = true;
        var engine = AdBlockRustEngine.Instance;
        Assert.True(engine.IsNativeAvailable);
        var files = new Dictionary<string, byte[]>
        {
            ["index.html"] = Encoding.UTF8.GetBytes("<!doctype html><html><body>Filter update test</body></html>"),
            ["sample-popup"] = Encoding.UTF8.GetBytes("<!doctype html><html><body>Popup test</body></html>"),
            ["favicon.ico"] = []
        };
        foreach (string name in new[] { "sample-one", "sample-two", "sample-allowed" })
            files[name + ".js"] = Encoding.UTF8.GetBytes("window.ruleProbe = true;");
        files["sample-one.js?file=example.zip"] = files["sample-one.js"];
        await using var source = new PipelineHttpSource(files,
            responseHeaders: new Dictionary<string, string> { ["Cache-Control"] = "no-store" },
            contentType: name => name.Split('?')[0].EndsWith(".js", StringComparison.Ordinal) ? "application/javascript" : "text/html");
        var browser = new BrowserWindow(1, new DownloadItem { OriginalUrl = source.Url("index.html"), HosterName = "Example" });
        CoreWebView2Environment? environment = null;
        string popupRule = "\n|" + source.Url("sample-popup");
        string rules = "|" + source.Url("sample-one.js") + "$script" + popupRule;
        int compilations = 0;
        try
        {
            browser.Show();
            await UntilAsync(() => browser.WebView.CoreWebView2 != null);
            var core = browser.WebView.CoreWebView2;
            environment = core.Environment;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                while (await core.ExecuteScriptAsync("document.readyState === 'complete' && location.href === " + JsonSerializer.Serialize(source.Url("index.html"))) != "true")
                    await Task.Delay(20, timeout.Token);
            var shield = Assert.IsType<Button>(browser.FindName("ShieldButton"));
            Invoke(shield);
            var popup = Assert.IsType<Popup>(browser.FindName("ShieldPopup"));
            await UntilAsync(() => popup.IsOpen);
            var update = Assert.IsType<Button>(browser.FindName("UpdateListsButton"));
            engine.FilterListFetcherForTesting = _ => Task.FromResult<string?>(rules);
            engine.OnEngineCompiledForTesting = _ => compilations++;

            async Task<bool> Runs(string name, string suffix = "")
            {
                string url = JsonSerializer.Serialize(source.Url(name + ".js" + suffix));
                string requestTarget = name + ".js" + suffix.Split('#')[0];
                int before = source.Requests.GetValueOrDefault(requestTarget);
                int blocked = engine.TotalBlockedCount;
                var result = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new
                {
                    expression = "new Promise(resolve => { window.ruleProbe = false; const s = document.createElement('script'); s.src = " + url +
                        "; s.onload = () => resolve(window.ruleProbe === true); s.onerror = () => resolve(false); document.body.append(s); })",
                    awaitPromise = true, returnByValue = true
                }));
                using var json = JsonDocument.Parse(result);
                Assert.False(json.RootElement.TryGetProperty("exceptionDetails", out _), result);
                bool ran = json.RootElement.GetProperty("result").GetProperty("value").GetBoolean();
                Assert.Equal(before + (ran ? 1 : 0), source.Requests.GetValueOrDefault(requestTarget));
                if (!ran) Assert.True(engine.TotalBlockedCount > blocked, "The filter must block the script before it reaches the local source.");
                return ran;
            }

            async Task ClickUpdate(string message)
            {
                var fetch = engine.FilterListFetcherForTesting;
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (fetch != null) engine.FilterListFetcherForTesting = async token =>
                {
                    await release.Task.WaitAsync(token);
                    return await fetch(token);
                };
                try
                {
                    Invoke(update);
                    await UntilAsync(() => !update.IsEnabled);
                    if (fetch != null) Assert.Equal(Loc.Get("Browser_Shield_UpdatingLists"), update.Content);
                    release.TrySetResult();
                    await UntilAsync(() => Equals(update.Content, Loc.Get(message)));
                    await UntilAsync(() => update.IsEnabled);
                    Assert.Equal(Loc.Get("Browser_Shield_UpdateLists"), update.Content);
                }
                finally { release.TrySetResult(); engine.FilterListFetcherForTesting = fetch; }
            }

            Assert.True(await Runs("sample-one"));
            Assert.True(await Runs("sample-one", "?file=example.zip"));
            Assert.True(await Runs("sample-one", "#example.zip"));
            Assert.True(await Runs("sample-two"));
            Assert.False(Reepax.Services.Browser.BrowserSecurityGuard.ShouldBlockPopup(source.Url("index.html"), source.Url("sample-popup"), true));
            await ClickUpdate("Browser_Shield_ListsUpdatedNew");
            Assert.Equal(1, compilations);
            Assert.False(await Runs("sample-one"));
            Assert.False(await Runs("sample-one", "?file=example.zip"));
            Assert.False(await Runs("sample-one", "#example.zip"));
            Assert.Equal(source.Url("index.html"), core.Source);
            Assert.True(await Runs("sample-two"));
            Assert.True(await Runs("sample-allowed"));
            int blockedBefore = engine.TotalBlockedCount;
            await core.ExecuteScriptAsync("(() => { const b = document.createElement('button'); b.textContent = 'Popup test'; b.style = 'width:180px;height:60px'; b.onclick = () => window.open(" +
                JsonSerializer.Serialize(source.Url("sample-popup")) + ", '_blank'); document.body.append(b); })()");
            using (var position = JsonDocument.Parse(await core.ExecuteScriptAsync("(() => { const r = document.querySelector('button').getBoundingClientRect(); return {x:r.x+r.width/2,y:r.y+r.height/2}; })()")))
                foreach (string type in new[] { "mouseMoved", "mousePressed", "mouseReleased" })
                    await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new
                    {
                        type, x = position.RootElement.GetProperty("x").GetDouble(), y = position.RootElement.GetProperty("y").GetDouble(),
                        button = "left", clickCount = 1
                    }));
            await UntilAsync(() => engine.TotalBlockedCount > blockedBefore);
            Assert.Equal(0, source.Requests.GetValueOrDefault("sample-popup"));
            Assert.Equal(source.Url("index.html"), core.Source);
            await ClickUpdate("Browser_Shield_ListsUpToDate");
            Assert.Equal(1, compilations);
            Assert.False(await Runs("sample-one"));

            rules = "|" + source.Url("sample-two.js") + "$script" + popupRule;
            await ClickUpdate("Browser_Shield_ListsUpdatedNew");
            Assert.Equal(2, compilations);
            Assert.True(await Runs("sample-one"));
            Assert.False(await Runs("sample-two"));
            engine.FilterListFetcherForTesting = _ => throw new HttpRequestException("Sample source unavailable.");
            await ClickUpdate("Browser_Shield_ListsUpdateFailed");
            Assert.Equal(2, compilations);
            Assert.False(await Runs("sample-two"));
            Assert.True(await Runs("sample-allowed"));

            string cache = Path.Combine(SettingsService.AppDataDirectory, "adblock_cache.dat");
            var expectedCache = AdBlockRustNative.Serialize(engine.EngineHandle)!;
            Assert.NotEmpty(expectedCache);
            await UntilAsync(() =>
            {
                try { return File.ReadAllBytes(cache).SequenceEqual(expectedCache); }
                catch (IOException) { return false; }
            });
            using (var restored = new AdBlockRustEngine())
            {
                Assert.True(restored.IsNativeAvailable);
                Assert.True(restored.ShouldBlock(source.Url("sample-two.js"), source.Url("index.html"), "script"));
                Assert.False(restored.ShouldBlock(source.Url("sample-one.js"), source.Url("index.html"), "script"));
            }
            if (Environment.GetEnvironmentVariable("REEPAX_LIVE_FILTER_CHECK") == "1" && language == "en")
            {
                engine.FilterListFetcherForTesting = null;
                await ClickUpdate("Browser_Shield_ListsUpdatedNew");
                Assert.True(await Runs("sample-two"));
                await ClickUpdate("Browser_Shield_ListsUpToDate");
                Console.WriteLine("Live HTTPS filter update and unchanged-list detection passed");
            }
            Console.WriteLine("Browser rules changed live; duplicate update, failed update and persisted rules passed");
            return environment;
        }
        finally
        {
            engine.FilterListFetcherForTesting = null;
            engine.OnEngineCompiledForTesting = null;
            browser.Close();
            if (environment != null)
            {
                var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Exit(object? sender, CoreWebView2BrowserProcessExitedEventArgs args) => exited.TrySetResult();
                environment.BrowserProcessExited += Exit;
                try { if (environment.GetProcessInfos().Count > 0) await exited.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
                finally { environment.BrowserProcessExited -= Exit; }
            }
        }
    }
}
