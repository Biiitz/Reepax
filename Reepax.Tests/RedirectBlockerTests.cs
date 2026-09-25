using Reepax.Services.Browser;
using Xunit;

namespace Reepax.Tests;

public class RedirectBlockerTests
{
    [Theory]
    [InlineData("popads.net", "popads.net")]
    [InlineData("sub.popads.net", "popads.net")]
    [InlineData("a.b.sub.popads.net", "popads.net")]
    [InlineData("dl1.downloadsite.com", "downloadsite.com")]
    [InlineData("downloadsite.com", "downloadsite.com")]
    [InlineData("www.example.com", "example.com")]
    // Known second-level TLDs: registrable domain includes three labels
    [InlineData("example.co.uk", "example.co.uk")]
    [InlineData("www.example.co.uk", "example.co.uk")]
    [InlineData("cdn.example.com.au", "example.com.au")]
    [InlineData("shop.example.co.jp", "example.co.jp")]
    [InlineData("files.example.com.br", "example.com.br")]
    [InlineData("blog.example.org.uk", "example.org.uk")]
    // Edge cases: Hosts without registrable domain remain unchanged
    [InlineData("localhost", "localhost")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("::1", "::1")]
    [InlineData("192.168.0.10", "192.168.0.10")]
    [InlineData("co.uk", "co.uk")]
    // Normalization: Uppercase and trailing dot
    [InlineData("Sub.PopAds.NET", "popads.net")]
    [InlineData("www.example.com.", "example.com")]
    public void GetRegistrableDomain_ReturnsExpectedDomain(string host, string expected)
    {
        Assert.Equal(expected, BrowserSecurityGuard.GetRegistrableDomain(host));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetRegistrableDomain_NullOrEmpty_ReturnsEmpty(string? host)
    {
        Assert.Equal(string.Empty, BrowserSecurityGuard.GetRegistrableDomain(host));
    }

    [Theory]
    // Non-user-initiated cross-domain redirects to unknown domains -> block
    [InlineData("https://downloadsite.com/file/abc", "https://somerandomsite.org/lander", false, true)]
    [InlineData("https://rapidgator.net/file/123", "https://unknown-tracker.info/click", false, true)]
    // Known ad networks even on click (overlay hijack) or without origin -> block
    [InlineData(null, "https://popads.net/serve.js", false, true)]
    [InlineData("https://downloadsite.com/file/abc", "https://sub.popcash.net/pop.php", false, true)]
    [InlineData("https://downloadsite.com/file/abc", "https://popads.net/serve.js", true, true)]
    [InlineData("https://downloadsite.com/file/abc", "https://monetag.com/tag.js", true, true)]
    [InlineData("https://rapidgator.net/file/123", "https://deloton.com/pop.js", true, true)]
    [InlineData("https://rapidgator.net/file/123", "https://adsterra.com/tag", false, true)]
    public void ShouldBlockRedirect_BlocksAdAndCrossDomainRedirects(
        string? currentUrl, string targetUrl, bool isUserInitiated, bool expected)
    {
        Assert.Equal(expected, BrowserSecurityGuard.ShouldBlockRedirect(currentUrl, targetUrl, isUserInitiated));
    }

    [Theory]
    // User-initiated navigations (clicks) to legitimate sites always allowed - including cross-domain
    [InlineData("https://downloadsite.com/file/abc", "https://somerandomsite.org/lander", true)]
    [InlineData("https://downloadsite.com/file/abc", "https://github.com", true)]
    [InlineData("https://forum.site/thread/1", "https://rapidgator.net/file/456", true)]
    [InlineData("https://forum.site/thread/1", "https://ddownload.com/abc", true)]
    // Legitimate filehosters allowed on automatic 302 redirects (e.g. FileCrypt -> hoster)
    [InlineData("https://filecrypt.cc/Container/123", "https://rapidgator.net/file/456", false)]
    [InlineData("https://filecrypt.cc/Container/123", "https://ddownload.com/abc", false)]
    [InlineData("https://filecrypt.cc/Container/123", "https://mega.nz/file/xyz", false)]
    [InlineData("https://filecrypt.cc/Container/123", "https://1fichier.com/?123", false)]
    [InlineData("https://filecrypt.cc/Container/123", "https://gofile.io/d/abc", false)]
    // Same-eTLD+1 redirects (hoster countdown -> CDN) allowed
    [InlineData("https://downloadsite.com/file/abc", "https://dl1.downloadsite.com/dl/abc123", false)]
    [InlineData("https://example.co.uk/page", "https://cdn.example.co.uk/dl/file.zip", false)]
    // Genuine direct download links always allowed
    [InlineData("https://downloadsite.com/file/abc", "https://othercdn.net/files/game.rar", false)]
    // CAPTCHA / challenge domains never blocked (including cross-domain)
    [InlineData("https://downloadsite.com/file/abc", "https://challenges.cloudflare.com/turnstile/v0/api.js", false)]
    [InlineData("https://downloadsite.com/file/abc", "https://js.hcaptcha.com/1/api.js", false)]
    // Without known origin domain (initial navigation, about:blank), do not block
    [InlineData(null, "https://somerandomsite.org/lander", false)]
    [InlineData("", "https://somerandomsite.org/lander", false)]
    [InlineData("about:blank", "https://somerandomsite.org/lander", false)]
    // Invalid / empty target URL do not block
    [InlineData("https://downloadsite.com/file/abc", "", false)]
    [InlineData("https://downloadsite.com/file/abc", null, false)]
    public void ShouldBlockRedirect_AllowsLegitimateNavigation(
        string? currentUrl, string? targetUrl, bool isUserInitiated)
    {
        Assert.False(BrowserSecurityGuard.ShouldBlockRedirect(currentUrl, targetUrl, isUserInitiated));
    }

    [Fact]
    public void IsWhitelisted_CaptchaDomains_AreWhitelisted()
    {
        Assert.True(BrowserSecurityGuard.IsWhitelisted("https://challenges.cloudflare.com/turnstile/v0/api.js"));
        Assert.True(BrowserSecurityGuard.IsWhitelisted("https://www.google.com/recaptcha/api.js"));
        Assert.True(BrowserSecurityGuard.IsWhitelisted("https://js.hcaptcha.com/1/api.js"));
        Assert.False(BrowserSecurityGuard.IsWhitelisted("https://somerandomsite.org/lander"));
        Assert.False(BrowserSecurityGuard.IsWhitelisted(""));
    }

    [Fact]
    public void ShouldBlockRedirect_BlocksDynamicallyDiscovered30xDomains()
    {
        BrowserSecurityGuard.DynamicallyBlockedRedirectDomains["ad-landing-zone.com"] = 1;
        try
        {
            Assert.True(BrowserSecurityGuard.ShouldBlockRedirect("https://downloadsite.com/file/123", "https://sub.ad-landing-zone.com/click", false));
        }
        finally
        {
            BrowserSecurityGuard.DynamicallyBlockedRedirectDomains.TryRemove("ad-landing-zone.com", out _);
        }
    }

    [Fact]
    public void ShouldBlockRedirect_BlocksDeceptiveQueryRedirects()
    {
        Assert.True(BrowserSecurityGuard.ShouldBlockRedirect(
            "https://hoster.com/file/1",
            "https://hoster.com/out?url=https%3A%2F%2Fpopads.net%2Fserve.js",
            true));
    }

    [Fact]
    public void IsDirectDownloadUrl_DetectsFilesCorrectly()
    {
        Assert.True(BrowserSecurityGuard.IsDirectDownloadUrl("https://example.com/files/archive.zip"));
        Assert.True(BrowserSecurityGuard.IsDirectDownloadUrl("https://example.com/files/archive.rar"));
        Assert.True(BrowserSecurityGuard.IsDirectDownloadUrl("https://example.com/dl/xyz123"));
        Assert.False(BrowserSecurityGuard.IsDirectDownloadUrl("https://example.com/page.html"));
    }

    [Fact]
    public void IsKnownSafeOrHosterDomain_RecognizesMajorHostersAndCrypters()
    {
        Assert.True(BrowserSecurityGuard.IsKnownSafeOrHosterDomain("rapidgator.net"));
        Assert.True(BrowserSecurityGuard.IsKnownSafeOrHosterDomain("sub.rapidgator.net"));
        Assert.True(BrowserSecurityGuard.IsKnownSafeOrHosterDomain("ddownload.com"));
        Assert.True(BrowserSecurityGuard.IsKnownSafeOrHosterDomain("mega.nz"));
        Assert.True(BrowserSecurityGuard.IsKnownSafeOrHosterDomain("filecrypt.cc"));
        Assert.True(BrowserSecurityGuard.IsKnownSafeOrHosterDomain("1fichier.com"));
        Assert.True(BrowserSecurityGuard.IsKnownSafeOrHosterDomain("mixdrop.co"));
        Assert.True(BrowserSecurityGuard.IsKnownSafeOrHosterDomain("streamtape.com"));
        Assert.True(BrowserSecurityGuard.IsKnownSafeOrHosterDomain("clicknupload.net"));
        Assert.True(BrowserSecurityGuard.IsKnownSafeOrHosterDomain("filelion.to"));
        Assert.False(BrowserSecurityGuard.IsKnownSafeOrHosterDomain("popads.net"));
        Assert.False(BrowserSecurityGuard.IsKnownSafeOrHosterDomain("somerandomsite.org"));
    }

    [Fact]
    public void IsSearchEngineOrPortal_DetectsPortalsCorrectly()
    {
        Assert.True(BrowserSecurityGuard.IsSearchEngineOrPortal("google.com"));
        Assert.True(BrowserSecurityGuard.IsSearchEngineOrPortal("www.google.de"));
        Assert.True(BrowserSecurityGuard.IsSearchEngineOrPortal("bing.com"));
        Assert.True(BrowserSecurityGuard.IsSearchEngineOrPortal("duckduckgo.com"));
        Assert.True(BrowserSecurityGuard.IsSearchEngineOrPortal("wikipedia.org"));

        Assert.False(BrowserSecurityGuard.IsSearchEngineOrPortal("rapidgator.net"));
        Assert.False(BrowserSecurityGuard.IsSearchEngineOrPortal("ddownload.com"));
        Assert.False(BrowserSecurityGuard.IsSearchEngineOrPortal("popads.net"));
        Assert.False(BrowserSecurityGuard.IsSearchEngineOrPortal(""));
        Assert.False(BrowserSecurityGuard.IsSearchEngineOrPortal(null));
    }

    [Fact]
    public void IsDirectDownloadUrl_DetectsQueryAndPathDownloads()
    {
        Assert.True(BrowserSecurityGuard.IsDirectDownloadUrl("https://rapidgator.net/file/123/download?token=xyz"));
        Assert.True(BrowserSecurityGuard.IsDirectDownloadUrl("https://cdn.example.com/stream?file=video.mp4"));
        Assert.True(BrowserSecurityGuard.IsDirectDownloadUrl("https://example.com/files/archive.part1.rar"));
        Assert.True(BrowserSecurityGuard.IsDirectDownloadUrl("https://example.com/getfile/789"));
        Assert.False(BrowserSecurityGuard.IsDirectDownloadUrl("https://rapidgator.net/file/123/game.rar.html"));
    }
}
