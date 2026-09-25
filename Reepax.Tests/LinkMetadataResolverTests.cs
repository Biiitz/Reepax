using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Extractor;
using Xunit;

namespace Reepax.Tests;

public class LinkMetadataResolverTests
{
    [Theory]
    [InlineData("24.5 GB", 26306674688)]
    [InlineData("24.5GB", 26306674688)]
    [InlineData("500 MB", 524288000)]
    [InlineData("500MB", 524288000)]
    [InlineData("1.5 GiB", 1610612736)]
    [InlineData("750 KB", 768000)]
    [InlineData("1024 Bytes", 1024)]
    [InlineData("2 TB", 2199023255552)]
    [InlineData("0 MB", 0)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    [InlineData("InvalidString", 0)]
    public void ParseSizeToBytes_CorrectlyParsesVariousUnitsAndDecimals(string? input, long expectedBytes)
    {
        var result = LinkMetadataResolverService.ParseSizeToBytes(input);
        Assert.Equal(expectedBytes, result);
    }

    private class MockHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public MockHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    [Fact]
    public async Task ResolveItemMetadataAsync_WithHeadRequest_ExtractsContentLengthAndDisposition()
    {
        var mockHandler = new MockHttpHandler(req =>
        {
            if (req.Method == HttpMethod.Head)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Content.Headers.ContentLength = 1048576000; // 1000 MB
                response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
                {
                    FileName = "\"game.part01.rar\""
                };
                response.Headers.ETag = new EntityTagHeaderValue("\"a1b2c3d4e5f60718293a4b5c6d7e8f90\"");
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var httpClient = new HttpClient(mockHandler);
        var resolver = new LinkMetadataResolverService(httpClient);

        var item = new DownloadItem
        {
            OriginalUrl = "https://cdn.example.com/downloads/game.part01.rar",
            FileName = "download_file"
        };

        var success = await resolver.ResolveItemMetadataAsync(item);

        Assert.True(success);
        Assert.Equal(1048576000, item.TotalBytes);
        Assert.Equal("game.part01.rar", item.FileName);
        Assert.Equal("a1b2c3d4e5f60718293a4b5c6d7e8f90", item.ExpectedChecksum);
    }

    [Fact]
    public async Task ResolveItemMetadataAsync_WithRangeFallback_ExtractsContentRangeLength()
    {
        var mockHandler = new MockHttpHandler(req =>
        {
            if (req.Method == HttpMethod.Head)
            {
                // Simulate hoster rejecting HEAD request
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            }

            if (req.Method == HttpMethod.Get && req.Headers.Range != null)
            {
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent);
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 0, 524288000); // 500 MB
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var httpClient = new HttpClient(mockHandler);
        var resolver = new LinkMetadataResolverService(httpClient);

        var item = new DownloadItem
        {
            OriginalUrl = "https://hoster.example.com/file/12345",
            FileName = "part1.rar"
        };

        var success = await resolver.ResolveItemMetadataAsync(item);

        Assert.True(success);
        Assert.Equal(524288000, item.TotalBytes);
    }

    [Fact]
    public async Task ResolvePackageMetadataAsync_ResolvesAllItemsAndRecalculatesPackageAggregates()
    {
        var mockHandler = new MockHttpHandler(req =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content.Headers.ContentLength = 500 * 1024 * 1024; // 500 MB per item
            return response;
        });

        using var httpClient = new HttpClient(mockHandler);
        var resolver = new LinkMetadataResolverService(httpClient);

        var package = new DownloadPackage
        {
            Name = "MultiPart Game"
        };

        for (int i = 1; i <= 3; i++)
        {
            package.Items.Add(new DownloadItem
            {
                OriginalUrl = $"https://example.com/part{i}.rar",
                FileName = $"part{i}.rar"
            });
        }

        Assert.Equal(0, package.TotalBytes);

        var reportedMessages = new System.Collections.Generic.List<string>();
        var progress = new Progress<string>(msg => reportedMessages.Add(msg));

        await resolver.ResolvePackageMetadataAsync(package, progress);

        // 3 items of 500 MB = 1500 MB
        Assert.Equal(1500L * 1024 * 1024, package.TotalBytes);
        Assert.Equal(3 * (1500L * 1024 * 1024), package.RequiredDiskSpaceBytes);
        Assert.NotEmpty(reportedMessages);
    }

    [Fact]
    public async Task ResolveItemMetadataAsync_WithHtmlHosterPage_ExtractsSizeFromHtmlBody()
    {
        var mockHandler = new MockHttpHandler(req =>
        {
            if (req.Method == HttpMethod.Head)
            {
                var headRes = new HttpResponseMessage(HttpStatusCode.OK);
                headRes.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
                headRes.Content.Headers.ContentLength = 8192; // HTML page length, not file size!
                return headRes;
            }

            var htmlContent = @"
                <html>
                    <body>
                        <div class=""file-info"">
                            <span class=""filename"">Game_Setup.iso</span>
                            <span class=""file-size"">2.5 GB</span>
                        </div>
                    </body>
                </html>";

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(htmlContent, System.Text.Encoding.UTF8, "text/html")
            };
            return response;
        });

        using var httpClient = new HttpClient(mockHandler);
        var resolver = new LinkMetadataResolverService(httpClient);

        var item = new DownloadItem
        {
            OriginalUrl = "https://customhoster.com/file/98765",
            FileName = "download_item"
        };

        var success = await resolver.ResolveItemMetadataAsync(item);

        Assert.True(success);
        // 2.5 GB = 2.5 * 1024 * 1024 * 1024 = 2684354560
        Assert.Equal(2684354560, item.TotalBytes);
    }

    [Fact]
    public async Task ResolveItemMetadataAsync_DirectFileResponse_MarksItemAsDirectDownload()
    {
        // Scenario A: Server responds with file content (attachment) instead of HTML page
        var mockHandler = new MockHttpHandler(req =>
        {
            if (req.Method == HttpMethod.Head)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Content.Headers.ContentLength = 1048576;
                response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
                {
                    FileName = "\"setup.exe\""
                };
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var httpClient = new HttpClient(mockHandler);
        var resolver = new LinkMetadataResolverService(httpClient);

        var item = new DownloadItem
        {
            OriginalUrl = "https://cdn.example.com/files/setup.exe",
            FileName = "setup.exe"
        };

        var success = await resolver.ResolveItemMetadataAsync(item);

        Assert.True(success);
        Assert.Equal(item.OriginalUrl, item.DirectDownloadUrl);
    }

    [Fact]
    public async Task ResolveItemMetadataAsync_KnownHosterPage_IsNotMarkedAsDirectDownload()
    {
        // Known hoster web pages must never be marked as direct download
        var mockHandler = new MockHttpHandler(req =>
        {
            if (req.Method == HttpMethod.Head)
            {
                var headRes = new HttpResponseMessage(HttpStatusCode.OK);
                headRes.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
                return headRes;
            }

            if (req.Method == HttpMethod.Get && req.Headers.Range != null)
            {
                var rangeRes = new HttpResponseMessage(HttpStatusCode.OK);
                rangeRes.Content = new StringContent("<html></html>", System.Text.Encoding.UTF8, "text/html");
                return rangeRes;
            }

            var html = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"<html><body><span class=""file-size"">100 MB</span></body></html>", System.Text.Encoding.UTF8, "text/html")
            };
            return html;
        });

        using var httpClient = new HttpClient(mockHandler);
        var resolver = new LinkMetadataResolverService(httpClient);

        var item = new DownloadItem
        {
            OriginalUrl = "https://1fichier.com/?abc123",
            FileName = "file.rar"
        };

        await resolver.ResolveItemMetadataAsync(item);

        Assert.Null(item.DirectDownloadUrl);
    }

    [Fact]
    public async Task ResolveItemMetadataAsync_FastHost_AutoResolveDisabled_DoesNotResolveDirectUrl()
    {
        // Toggle OFF: Page is scanned for size, but NO direct link is extracted
        var mockHandler = new MockHttpHandler(req =>
        {
            var html = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($@"<html><body>
                    <span class=""file-size"">500 MB</span>
                    <script>window.open(""https://dl.{FastHostResolver.CanonicalDomain}/dl/abcdef#secret"")</script>
                    </body></html>", System.Text.Encoding.UTF8, "text/html")
            };
            html.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
            return html;
        });

        using var httpClient = new HttpClient(mockHandler);
        var resolver = new LinkMetadataResolverService(httpClient);

        var item = new DownloadItem
        {
            OriginalUrl = $"https://{FastHostResolver.CanonicalDomain}/abc123/file.rar",
            FileName = "file.rar"
        };

        await resolver.ResolveItemMetadataAsync(item, cancellationToken: default, allowFastHostResolve: false);

        Assert.Null(item.DirectDownloadUrl);
        Assert.Equal($"https://{FastHostResolver.CanonicalDomain}/abc123/file.rar", item.OriginalUrl);
    }

    [Fact]
    public async Task ResolveItemMetadataAsync_FastHost_AutoResolveEnabled_ResolvesDirectUrl()
    {
        // Toggle ON: Direct link is extracted
        var mockHandler = new MockHttpHandler(req =>
        {
            var url = req.RequestUri?.ToString() ?? "";
            if (url.Contains("/dl/"))
            {
                var fileRes = new HttpResponseMessage(HttpStatusCode.OK);
                fileRes.Content.Headers.ContentLength = 524288000;
                fileRes.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
                {
                    FileName = "\"file.rar\""
                };
                return fileRes;
            }

            var html = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($@"<html><body>
                    <script>window.open(""https://dl.{FastHostResolver.CanonicalDomain}/dl/abcdef#secret"")</script>
                    </body></html>", System.Text.Encoding.UTF8, "text/html")
            };
            html.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
            return html;
        });

        using var httpClient = new HttpClient(mockHandler);
        var resolver = new LinkMetadataResolverService(httpClient);

        var item = new DownloadItem
        {
            OriginalUrl = $"https://{FastHostResolver.CanonicalDomain}/abc123/file.rar",
            FileName = "file.rar"
        };

        await resolver.ResolveItemMetadataAsync(item, cancellationToken: default, allowFastHostResolve: true);

        Assert.Equal($"https://dl.{FastHostResolver.CanonicalDomain}/dl/abcdef#secret", item.DirectDownloadUrl);
    }
}
