using Reepax.Services.Extractor;
using Xunit;

namespace Reepax.Tests;

public class WebPageLinkExtractorTests
{
    [Theory]
    [InlineData("https://example.com/software-release-package/", "https://example.com/software-release-package/")]
    [InlineData("Check this out: https://example.com/downloads/great-app/ is available!", "https://example.com/downloads/great-app/")]
    [InlineData("http://www.example.org/project-a/", "http://www.example.org/project-a/")]
    [InlineData("https://example.org/suite-b/.", "https://example.org/suite-b/")]
    public void FindExtractablePageUrl_FindsUrlInText(string input, string expected)
    {
        Assert.Equal(expected, WebPageLinkExtractor.FindExtractablePageUrl(input));
    }

    [Theory]
    [InlineData("https://example.com/archive.rar")]
    [InlineData("https://example.com/file.zip")]
    [InlineData("https://example.com/image.iso")]
    [InlineData("https://example.com/package.7z")]
    [InlineData("example.com without scheme")]
    [InlineData("")]
    [InlineData(null)]
    public void FindExtractablePageUrl_RejectsNonPageUrls(string? input)
    {
        Assert.Null(WebPageLinkExtractor.FindExtractablePageUrl(input));
    }

    [Theory]
    [InlineData("<title>My Project Suite - Download</title>", "My Project Suite")]
    [InlineData("<title>Open Software Release | Free Download</title>", "Open Software Release")]
    [InlineData("<title>Developer Toolkit – Home</title>", "Developer Toolkit")]
    [InlineData("<title>Pure Utility</title>", "Pure Utility")]
    [InlineData("", "Download Package")]
    public void ExtractPageTitle_ParsesAndCleansTitle(string html, string expectedTitle)
    {
        var title = WebPageLinkExtractor.ExtractPageTitle(html);
        Assert.Equal(expectedTitle, title);
    }
}
