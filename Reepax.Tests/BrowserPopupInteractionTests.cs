using System.Text.Json;
using System.Threading.Tasks;
using Reepax.Services.Browser;
using Xunit;

namespace Reepax.Tests;

public sealed class BrowserPopupInteractionTests : IClassFixture<BrowserScriptTestHost>
{
    private readonly BrowserScriptTestHost _browser;
    public BrowserPopupInteractionTests(BrowserScriptTestHost browser) => _browser = browser;

    [Theory]
    [InlineData("mousedown")]
    [InlineData("click")]
    public Task BlockedGlobalPopupHandler_PreservesTheDownloadClick(string eventType) => _browser.OnUi(async () =>
    {
        await _browser.LoadAsync("<button id='download'>Download</button>", $$"""
            window.downloads = 0;
            document.addEventListener({{JsonSerializer.Serialize(eventType)}}, function(event) {
                window.open('https://ads.unrelated.test/landing');
                event.stopImmediatePropagation();
                event.preventDefault();
            }, true);
            document.querySelector('button').addEventListener('click', () => window.downloads++);
            """);
        await _browser.ClickAsync();
        Assert.Equal(1, await _browser.EvaluateAsync<int>("window.downloads"));
        Assert.Equal(1, _browser.BlockedPopups);
        Assert.Empty(_browser.NativePopups);
    });

    [Theory]
    [InlineData("auto", false, false)]
    [InlineData("pointer", false, false)]
    [InlineData("pointer", true, false)]
    [InlineData("auto", true, true)]
    [InlineData("pointer", false, true)]
    public Task ConfirmedPopupOverlay_ForwardsOneClickAndPreservesUnrelatedOverlays(string cursor, bool hideWhileOpening, bool useNativeListener) => _browser.OnUi(async () =>
    {
        await _browser.LoadAsync("""
            <button id='download'>Download</button>
            <div id='overlay' style='position:fixed;left:8px;top:8px;width:200px;height:60px;z-index:2147483647;cursor:pointer'></div>
            <div id='unrelated' role='dialog' style='position:fixed;left:500px;top:300px;z-index:2147483647;cursor:pointer'></div>
            """.Replace("cursor:pointer", "cursor:" + cursor), $$"""
            window.downloads = 0;
            const addListener = {{JsonSerializer.Serialize(useNativeListener)}} ? window.__nativeAdd : document.addEventListener;
            addListener.call(document, 'mousedown', function(event) {
                if ({{JsonSerializer.Serialize(hideWhileOpening)}}) document.getElementById('overlay').style.display = 'none';
                window.open('https://ads.unrelated.test/landing');
                document.getElementById('overlay').style.display = 'block';
                event.stopImmediatePropagation();
                event.preventDefault();
            }, true);
            document.querySelector('button').addEventListener('click', () => window.downloads++);
            """);
        await _browser.ClickAsync();
        Assert.Equal(1, await _browser.EvaluateAsync<int>("window.downloads"));
        Assert.Equal("none", await _browser.EvaluateAsync<string>("getComputedStyle(document.getElementById('overlay')).pointerEvents"));
        Assert.False(await _browser.EvaluateAsync<bool>("document.getElementById('unrelated').hasAttribute('data-reepax-popup-overlay')"));
        Assert.Empty(_browser.NativePopups);
    });

    [Fact]
    public Task LegitimateDelegatedHandler_StillControlsDefaultBehavior() => _browser.OnUi(async () =>
    {
        await _browser.LoadAsync("<button>Download</button>", """
            document.addEventListener('click', event => {
                window.open('https://ads.unrelated.test/landing');
                event.preventDefault();
            }, true);
            document.addEventListener('click', event => {
                event.preventDefault();
                window.applicationPrevented = event.defaultPrevented;
            }, true);
            """);
        await _browser.ClickAsync();
        Assert.True(await _browser.EvaluateAsync<bool>("window.applicationPrevented"));
        Assert.Empty(_browser.NativePopups);
    });

    [Fact]
    public Task GlobalListeners_PreserveRemovalDuplicationOnceAndAbortSemantics() => _browser.OnUi(async () =>
    {
        await _browser.LoadAsync("<button>Download</button>", """
            window.calls = 0;
            const removed = () => window.calls += 100;
            document.addEventListener('click', removed, {capture:true});
            document.removeEventListener('click', removed, true);
            const repeated = () => window.calls++;
            document.addEventListener('click', repeated);
            document.addEventListener('click', repeated);
            document.addEventListener('click', {handleEvent() { window.calls += 10; }}, {once:true});
            const abort = new AbortController();
            document.addEventListener('click', () => window.calls += 1000, {signal:abort.signal});
            abort.abort();
            """);
        await _browser.ClickAsync();
        await _browser.ClickAsync();
        Assert.Equal(12, await _browser.EvaluateAsync<int>("window.calls"));
    });

    [Fact]
    public Task AllowedPopupOutsideAnEvent_DoesNotThrowAndReachesNativeChecks() => _browser.OnUi(async () =>
    {
        await _browser.LoadAsync("<button>Download</button>", "");
        var result = await _browser.Core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new
        {
            expression = "window.open('https://page.example.test/next'); true", userGesture = true, returnByValue = true
        }));
        using var document = JsonDocument.Parse(result);
        Assert.False(document.RootElement.TryGetProperty("exceptionDetails", out _));
        Assert.Equal("https://page.example.test/next", Assert.Single(_browser.NativePopups));
    });

    [Fact]
    public Task CosmeticRules_PreserveDownloadAndLoginControlsWhileHidingAdMarkers() => _browser.OnUi(async () =>
    {
        await _browser.LoadAsync("""
            <button class='download-button' id='download-link'>Download</button>
            <div id='download-modal'>Download options</div><div class='login-popup' id='login'>Login</div>
            <div class='cookie-banner' id='consent'>Consent</div><div class='ad-banner' id='advert'>Advertisement</div>
            """, "const style = document.createElement('style'); style.textContent = " +
            JsonSerializer.Serialize(BrowserSecurityGuard.GetCosmeticFilterCss()) + "; document.head.appendChild(style);");
        foreach (var id in new[] { "download-link", "download-modal", "login", "consent" })
            Assert.NotEqual("none", await _browser.EvaluateAsync<string>($"getComputedStyle(document.getElementById('{id}')).display"));
        Assert.Equal("none", await _browser.EvaluateAsync<string>("getComputedStyle(document.getElementById('advert')).display"));
    });

    [Fact]
    public Task DisabledPopupGuard_PreservesNormalEventCancellation() => _browser.OnUi(async () =>
    {
        await _browser.LoadAsync("<button>Download</button>", """
            document.addEventListener('click', event => {
                window.open('https://ads.unrelated.test/landing');
                event.preventDefault();
                window.prevented = event.defaultPrevented;
            }, true);
            """);
        _browser.BlockPopups = false;
        await _browser.ClickAsync();
        Assert.True(await _browser.EvaluateAsync<bool>("window.prevented"));
        Assert.Equal(0, _browser.BlockedPopups);
        Assert.Single(_browser.NativePopups);
    });

    [Theory]
    [InlineData("https://page.example.test/item", "https://unknown.unrelated.test/landing", true, true)]
    [InlineData("https://page.example.test/item", "https://page.example.test/next", false, false)]
    [InlineData("https://page.example.test/item", "https://cdn.example.test/prepare", true, false)]
    [InlineData("https://page.example.test/item", "https://files.unrelated.test/archive.zip", true, false)]
    public void PopupPolicy_PreservesDownloadRoutesWithoutAllowingUnknownThirdParties(string source, string target, bool userInitiated, bool blocked)
        => Assert.Equal(blocked, BrowserSecurityGuard.ShouldBlockPopup(source, target, userInitiated));
}
