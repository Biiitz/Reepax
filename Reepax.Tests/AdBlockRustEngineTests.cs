using System;
using Reepax.Services.AdBlock;
using Reepax.Services.AdBlock.Native;
using Xunit;

namespace Reepax.Tests;

public class AdBlockRustEngineTests
{
    [Fact]
    public void NativeMethods_OrFallback_InitializesWithoutException()
    {
        var engine = AdBlockRustEngine.Instance;
        Assert.NotNull(engine);
        // Engine should be enabled by default
        Assert.True(engine.IsEnabled);
    }

    [Theory]
    [InlineData("https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js", "script")]
    [InlineData("https://adservice.google.com/adsid/google/ui", "script")]
    [InlineData("https://securepubads.g.doubleclick.net/gampad/ads", "script")]
    [InlineData("https://popads.net/serve.js", "script")]
    [InlineData("https://clickadu.com/banner.png", "image")]
    [InlineData("https://monetag.com/push/service.js", "script")]
    public void ShouldBlock_BlocksKnownAdNetworks(string url, string type)
    {
        var engine = AdBlockRustEngine.Instance;
        var blocked = engine.ShouldBlock(url, "https://example.com", type, out _);
        Assert.True(blocked, $"Expected {url} to be blocked by adblock engine.");
    }

    [Theory]
    [InlineData("https://challenges.cloudflare.com/turnstile/v0/api.js")]
    [InlineData("https://www.google.com/recaptcha/api.js")]
    [InlineData("https://js.hcaptcha.com/1/api.js")]
    [InlineData("https://static.geetest.com/v4/gt4.js")]
    public void ShouldBlock_NeverBlocksEssentialCaptchas(string captchaUrl)
    {
        var engine = AdBlockRustEngine.Instance;
        var blocked = engine.ShouldBlock(captchaUrl, "https://rapidgator.net", "script", out _);
        Assert.False(blocked, $"CAPTCHA challenge {captchaUrl} must never be blocked!");
    }

    [Theory]
    [InlineData("https://rapidgator.net/file/12345/archive.zip")]
    [InlineData("https://ddownload.com/dl/file.rar")]
    [InlineData("https://mega.nz/file/abc#xyz")]
    public void ShouldBlock_NeverBlocksLegitimateFilehosterDirectDownloads(string downloadUrl)
    {
        var engine = AdBlockRustEngine.Instance;
        var blocked = engine.ShouldBlock(downloadUrl, "https://rapidgator.net", "other", out _);
        Assert.False(blocked, $"Direct download URL {downloadUrl} must never be blocked!");
    }

    [Fact]
    public void Whitelisting_TogglingDomainAllowsExemptions()
    {
        var engine = AdBlockRustEngine.Instance;
        var testDomain = "test-ad-portal-" + Guid.NewGuid().ToString("N") + ".org";
        var testUrl = $"https://doubleclick.net/ad.js";
        var sourceUrl = $"https://{testDomain}/page";

        // Without whitelisting -> blocked
        Assert.False(engine.IsDomainWhitelisted(testDomain));
        Assert.True(engine.ShouldBlock(testUrl, sourceUrl, "script", out _));

        // Whitelist the domain
        engine.SetDomainWhitelisted(testDomain, true);
        Assert.True(engine.IsDomainWhitelisted(testDomain));

        // Now requests from that domain should pass through!
        Assert.False(engine.ShouldBlock(testUrl, sourceUrl, "script", out _));

        // Un-whitelist
        engine.SetDomainWhitelisted(testDomain, false);
        Assert.False(engine.IsDomainWhitelisted(testDomain));
        Assert.True(engine.ShouldBlock(testUrl, sourceUrl, "script", out _));
    }

    [Fact]
    public void GetCosmeticResources_ReturnsValidCosmeticRules()
    {
        var engine = AdBlockRustEngine.Instance;
        var (css, _) = engine.GetCosmeticResources("https://youtube.com");
        Assert.NotNull(css);
        if (engine.IsNativeAvailable)
        {
            Assert.Contains("display: none", css);
        }
    }

    [Fact]
    public void FilterUpdateResult_EnumValuesDefined()
    {
        Assert.True(Enum.IsDefined(typeof(FilterUpdateResult), FilterUpdateResult.NewFiltersApplied));
        Assert.True(Enum.IsDefined(typeof(FilterUpdateResult), FilterUpdateResult.AlreadyUpToDate));
        Assert.True(Enum.IsDefined(typeof(FilterUpdateResult), FilterUpdateResult.Failed));
    }
}
