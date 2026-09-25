using System.Linq;
using Reepax.Services.Extractor;
using Xunit;

namespace Reepax.Tests;

public class LinkExtractorTests
{
    [Fact]
    public void ExtractLinks_FromMultilineForumPost_ExtractsAllUniqueUrls()
    {
        // Arrange
        string forumPost = @"
[b]My Awesome Archive 2024 (1080p)[/b]
Here are the download links for Rapidgator and DDownload:

Rapidgator:
https://rapidgator.net/file/abc12345/My.Awesome.Archive.2024.part01.rar.html
https://rapidgator.net/file/abc12346/My.Awesome.Archive.2024.part02.rar.html
https://rapidgator.net/file/abc12347/My.Awesome.Archive.2024.part03.rar.html

Mirror (DDownload):
https://ddownload.com/ddl12345/My.Awesome.Archive.2024.part01.rar
https://ddownload.com/ddl12346/My.Awesome.Archive.2024.part02.rar

Duplicate check:
https://rapidgator.net/file/abc12345/My.Awesome.Archive.2024.part01.rar.html

Random text at the end: Enjoy the release!
";

        // Act
        var links = LinkExtractor.ExtractLinks(forumPost);

        // Assert
        Assert.Equal(5, links.Count); // 3 rapidgator + 2 ddownload (duplicate excluded)
        
        Assert.Contains(links, l => l.Url == "https://rapidgator.net/file/abc12345/My.Awesome.Archive.2024.part01.rar.html");
        Assert.Contains(links, l => l.Url == "https://ddownload.com/ddl12345/My.Awesome.Archive.2024.part01.rar");
    }

    [Theory]
    [InlineData("https://rapidgator.net/file/12345/archive.part1.rar", "Rapidgator")]
    [InlineData("https://ddownload.com/abc/movie.mkv", "DDownload")]
    [InlineData("https://1fichier.com/?abcdefghij", "1Fichier")]
    [InlineData("https://katfile.com/xyz123/setup.iso", "Katfile")]
    [InlineData("https://turbobit.net/turbo123.html", "Turbobit")]
    [InlineData("https://mega.nz/file/sample#key", "Mega")]
    [InlineData("https://mediafire.com/file/my_doc.pdf", "MediaFire")]
    public void ExtractLinks_IdentifiesKnownHostersCorrectly(string url, string expectedHoster)
    {
        // Act
        var links = LinkExtractor.ExtractLinks(url);

        // Assert
        Assert.Single(links);
        Assert.Equal(expectedHoster, links[0].Hoster.DisplayName);
    }

    [Fact]
    public void ExtractFileNameFromUrl_ExtractsCorrectFileName()
    {
        // Act & Assert
        Assert.Equal("MyArchive.part01.rar", LinkExtractor.ExtractFileNameFromUrl("https://rapidgator.net/file/123/MyArchive.part01.rar.html"));
        Assert.Equal("Setup_v1.0.exe", LinkExtractor.ExtractFileNameFromUrl("https://example.com/downloads/Setup_v1.0.exe"));
        Assert.Equal("Document.pdf", LinkExtractor.ExtractFileNameFromUrl("https://example.com/get?filename=Document.pdf"));
    }

    [Fact]
    public void ExtractLinks_BrowserHtmlFragmentWithMultipleAnchors_ExtractsOnlyRealHrefs()
    {
        // Simulates drag & drop of a highlighted browser selection:
        // the fragment header (Version/StartHTML/...) must not pass through as garbage
        string htmlFragment = @"Version:1.0
StartHTML:0000000126
EndHTML:0000001899
StartFragment:0000000162
EndFragment:0000001863
<html><body>
<!--StartFragment-->
<div class=""post"">
  <h2>My Game Release</h2>
  <a href=""https://rapidgator.net/file/101/MyGame.part1.rar"">MyGame Part 1</a>
  <span>und</span>
  <a href=""https://ddownload.com/abc/MyGame.part2.rar"">MyGame Part 2</a>
  <img src=""https://tracking.example.com/pixel.png"" />
</div>
<!--EndFragment-->
</body></html>";

        // Act
        var links = LinkExtractor.ExtractLinks(htmlFragment);

        // Assert: exactly the two real href links, no fragment header garbage
        Assert.Equal(2, links.Count);
        Assert.Equal("https://rapidgator.net/file/101/MyGame.part1.rar", links[0].Url);
        Assert.Equal("https://ddownload.com/abc/MyGame.part2.rar", links[1].Url);
        Assert.Equal("MyGame Part 1", links[0].ContextTitle);
        Assert.DoesNotContain(links, l => l.Url.Contains("StartHTML") || l.Url.Contains("tracking.example"));
    }

    [Fact]
    public void ExtractLinks_MixedHtmlAndPlainText_FindsBoth()
    {
        string mixed = @"Release Notes
<a href=""https://rapidgator.net/file/1/pack1.rar"">Pack 1</a>
https://ddownload.com/xyz/pack2.rar
";

        var links = LinkExtractor.ExtractLinks(mixed);

        Assert.Equal(2, links.Count);
        Assert.Contains(links, l => l.Url == "https://rapidgator.net/file/1/pack1.rar");
        Assert.Contains(links, l => l.Url == "https://ddownload.com/xyz/pack2.rar");
    }

    [Fact]
    public void NormalizeInputText_ConvertsHtmlFragmentToCleanLinkList()
    {
        string fragment = $@"Version:0.9
StartHTML:0000000193
EndHTML:0000000410
StartFragment:0000000229
EndFragment:0000000374
SourceURL:https://{FastHostResolver.CanonicalDomain}/cwxslzwhhl06#PengPong_Game.rar
<html>
<body>
<!--StartFragment--><a href=""https://{FastHostResolver.CanonicalDomain}/cwxslzwhhl06#PengPong_Game.rar"" target=""_blank"" rel=""noopener"">Filehoster: FastHost</a><!--EndFragment-->
</body>
</html>";

        var normalized = LinkExtractor.NormalizeInputText(fragment);

        Assert.Equal($"https://{FastHostResolver.CanonicalDomain}/cwxslzwhhl06#PengPong_Game.rar", normalized);
    }

    [Fact]
    public void NormalizeInputText_LeavesPlainTextUntouched()
    {
        string plain = "Meine Release\nhttps://rapidgator.net/file/1/pack1.rar";

        Assert.Equal(plain, LinkExtractor.NormalizeInputText(plain));
    }

    [Fact]
    public void ExtractFileNameFromUrl_UsesFragmentAsFileName()
    {
        // Fragment links carry the actual filename in the #-fragment
        Assert.Equal("PengPong_Game.rar",
            LinkExtractor.ExtractFileNameFromUrl($"https://{FastHostResolver.CanonicalDomain}/cwxslzwhhl06#PengPong_Game.rar"));
        Assert.Equal("Adventure_Game.part01.rar",
            LinkExtractor.ExtractFileNameFromUrl($"https://{FastHostResolver.CanonicalDomain}/yjoj5ds9m14e#Adventure_Game.part01.rar"));
    }

    [Fact]
    public void ExtractLinks_FastHostFragmentLink_IsNotDirectDownload()
    {
        // Page link vs direct link (/dl/) - distinction for host auto-resolver
        var pageLink = $"https://{FastHostResolver.CanonicalDomain}/yjoj5ds9m14e#Game.part01.rar";
        var directLink = $"https://dl.{FastHostResolver.CanonicalDomain}/dl/bqJazo6Gf-pUZQ6fsJ9EL4cyKinMMnb";

        Assert.True(FastHostResolver.IsFastHostUrl(pageLink));
        Assert.False(FastHostResolver.IsDirectDownloadUrl(pageLink));
        Assert.True(FastHostResolver.IsFastHostUrl(directLink));
        Assert.True(FastHostResolver.IsDirectDownloadUrl(directLink));
    }

    [Fact]
    public void ExtractFileNameFromUrl_FastHostDirectDownloadToken_ReturnsDownloadFile()
    {
        string tokenUrl = $"https://dl1.{FastHostResolver.CanonicalDomain}/dl/y9PN0SnSOrlAL+VPqdI+HnfkzH7uS7rpsC8nKLtiMlZPDgPin0Ar-tWT9ZJ-fD6lDCMmQ3FhrA2B79qnCIhoC7LNv8IkTwElQaYaGMRKfHEXQkWCzJW8pfEf5MkNHb7TJ8xkKDkqG5dskmlx6s9jBU7m7yJLevY";
        var fileName = LinkExtractor.ExtractFileNameFromUrl(tokenUrl);
        Assert.Equal("download_file", fileName);

        string rawToken = "y9PN0SnSOrlAL VPqdI HnfkzH7uS7rpsC8nKLtiMlZPDgPin0Ar-tWT9ZJ-fD6lDCMmQ3FhrA2B79qnCIhoC7LNv8IkTwElQaYaGMRKfHEXQkWCzJW8pfEf5MkNHb7TJ8xkKDkqG5dskmlx6s9jBU7m7yJLevY";
        Assert.True(LinkExtractor.IsPureNumericOrHash(rawToken));
    }
}
