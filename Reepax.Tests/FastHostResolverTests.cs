using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Extractor;
using Xunit;

namespace Reepax.Tests;

public class FastHostResolverTests
{
    private static readonly string Host = FastHostResolver.CanonicalDomain;
    private static readonly string AltHost = FastHostResolver.CanonicalDomain.Replace(".co", ".net");

    public static IEnumerable<object[]> IsFastHostUrlData()
    {
        yield return new object[] { $"https://{Host}/dl/12345", true };
        yield return new object[] { $"http://{Host}/sample_file", true };
        yield return new object[] { $"https://dl.{Host}/dl/abc", true };
        yield return new object[] { $"https://dl1.{Host}/dl/abc", true };
        yield return new object[] { $"https://dl2.{AltHost}/dl/xyz", true };
        yield return new object[] { $"https://sub.{AltHost}/file", true };
        yield return new object[] { $"https://deep.sub.{Host}/file", true };
        yield return new object[] { $"https://fake{Host}/dl/123", false };
        yield return new object[] { $"https://not{AltHost}/dl/123", false };
        yield return new object[] { $"https://example.com/dl/123", false };
        yield return new object[] { $"https://{Host}.other-domain.com/dl/123", false };
        yield return new object[] { $"https://{AltHost}.other-domain.org/dl/123", false };
        yield return new object[] { $"https://untrusted.com/?redirect=https://{Host}", false };
        yield return new object[] { $"http://untrusted.com/{Host}", false };
        yield return new object[] { "", false };
        yield return new object[] { "   ", false };
        yield return new object[] { null!, false };
        yield return new object[] { "invalid-url-string", false };
    }

    [Theory]
    [MemberData(nameof(IsFastHostUrlData))]
    public void IsFastHostUrl_MatchesExactDomainAndSubdomainsOnly(string? url, bool expected)
    {
        bool result = FastHostResolver.IsFastHostUrl(url);
        Assert.Equal(expected, result);
    }

    public static IEnumerable<object[]> IsDirectDownloadUrlData()
    {
        yield return new object[] { $"https://{Host}/dl/12345", true };
        yield return new object[] { $"https://{AltHost}/dl/xyz_file.zip", true };
        yield return new object[] { $"https://dl.{Host}/my_file.rar", true };
        yield return new object[] { $"https://dl1.{Host}/archive.zip", true };
        yield return new object[] { $"https://dl2.{AltHost}/package.7z", true };
        yield return new object[] { $"https://{Host}/view/12345", false };
        yield return new object[] { $"https://{AltHost}/info/12345", false };
        yield return new object[] { $"https://{Host}/", false };
        yield return new object[] { "https://untrusted.com/dl/12345", false };
        yield return new object[] { $"https://fake{Host}/dl/12345", false };
        yield return new object[] { "", false };
        yield return new object[] { null!, false };
    }

    [Theory]
    [MemberData(nameof(IsDirectDownloadUrlData))]
    public void IsDirectDownloadUrl_ValidatesExactHostAndDirectPath(string? url, bool expected)
    {
        bool result = FastHostResolver.IsDirectDownloadUrl(url);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ExtractDirectUrlFromHtml_ExtractsFromWindowOpen()
    {
        string expected = $"https://dl1.{Host}/dl/my_game.part1.rar";
        string html = $"<html><body><script>window.open('{expected}');</script></body></html>";
        var directUrl = FastHostResolver.ExtractDirectUrlFromHtml(html);
        Assert.Equal(expected, directUrl);
    }

    [Fact]
    public void ExtractDirectUrlFromHtml_ExtractsFromLocationHref()
    {
        string expected = $"https://dl2.{AltHost}/dl/movie_1080p.mkv";
        string html = $"<script>location.href = \"{expected}\";</script>";
        var directUrl = FastHostResolver.ExtractDirectUrlFromHtml(html);
        Assert.Equal(expected, directUrl);
    }

    [Fact]
    public void ExtractDirectUrlFromHtml_ExtractsFromAnchorTag()
    {
        string expected = $"https://dl.{Host}/dl/document.pdf";
        string html = $"<div class=\"btn\"><a href=\"{expected}\">Direct Download</a></div>";
        var directUrl = FastHostResolver.ExtractDirectUrlFromHtml(html);
        Assert.Equal(expected, directUrl);
    }

    [Fact]
    public void ExtractDirectUrlFromHtml_DecodesHtmlEntities()
    {
        string expected = $"https://dl1.{Host}/dl/archive&name=sample.zip";
        string html = $"window.open('https://dl1.{Host}/dl/archive&amp;name=sample.zip')";
        var directUrl = FastHostResolver.ExtractDirectUrlFromHtml(html);
        Assert.Equal(expected, directUrl);
    }

    [Fact]
    public void ExtractDirectUrlFromHtml_ReturnsNullOnEmptyOrIrrelevantHtml()
    {
        Assert.Null(FastHostResolver.ExtractDirectUrlFromHtml(""));
        Assert.Null(FastHostResolver.ExtractDirectUrlFromHtml(null));
        Assert.Null(FastHostResolver.ExtractDirectUrlFromHtml("<div>Hello world without links</div>"));
    }

    [Fact]
    public async Task ResolveDirectUrlAsync_WhenAlreadyDirect_ReturnsImmediatelyWithoutHttp()
    {
        using var client = new HttpClient();
        var directUrl = $"https://dl1.{Host}/dl/file.rar";

        var result = await FastHostResolver.ResolveDirectUrlAsync(client, directUrl);
        Assert.Equal(directUrl, result);
    }

    [Fact]
    public async Task ResolveDirectUrlAsync_ParsesHtmlAndReturnsDirectUrl()
    {
        var expected = $"https://dl3.{Host}/dl/resolved.zip";
        var htmlContent = $"<html><body><script>window.open('{expected}');</script></body></html>";
        var handler = new TestMockHttpMessageHandler((req, ct) =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(htmlContent)
            };
            return Task.FromResult(res);
        });

        using var client = new HttpClient(handler);
        var result = await FastHostResolver.ResolveDirectUrlAsync(client, $"https://{Host}/view/12345");

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task ResolveLinksInParallelAsync_ResolvesOnlyFastHostLinks()
    {
        var links = new List<ExtractedLink>
        {
            new() { Url = $"https://{Host}/101", Hoster = new HosterInfo { DisplayName = "FastHost" } },
            new() { Url = "https://rapidgator.net/file/202", Hoster = new HosterInfo { DisplayName = "Rapidgator" } }
        };

        var handler = new TestMockHttpMessageHandler((req, ct) =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"<script>window.open('https://dl1.{Host}/dl/resolved_101.rar');</script>")
            };
            return Task.FromResult(res);
        });

        using var client = new HttpClient(handler);
        await FastHostResolver.ResolveLinksInParallelAsync(client, links);

        Assert.Equal($"https://dl1.{Host}/dl/resolved_101.rar", links[0].DirectDownloadUrl);
        // Original page URL is preserved (for re-resolve with expired links)
        Assert.Equal($"https://{Host}/101", links[0].Url);
        Assert.Null(links[1].DirectDownloadUrl);
        Assert.Equal("https://rapidgator.net/file/202", links[1].Url);
    }

    private class TestMockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public TestMockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }
}
