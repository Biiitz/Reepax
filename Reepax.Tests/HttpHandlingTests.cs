using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Helpers;
using Reepax.Services.SystemIntegration;
using Xunit;

namespace Reepax.Tests;

public class HttpHandlingTests
{
    [Fact]
    public void HttpUserAgentService_ReturnsValidBrowserUserAgent()
    {
        var ua = HttpUserAgentService.CurrentUserAgent;

        Assert.NotNull(ua);
        Assert.NotEmpty(ua);
        Assert.StartsWith("Mozilla/5.0", ua);
        Assert.Contains("Windows NT", ua);
        Assert.Contains("AppleWebKit", ua);
        Assert.Contains("Chrome", ua);
        Assert.NotEmpty(HttpUserAgentService.ChromiumMajorVersion);
    }

    [Fact]
    public void HttpUserAgentService_UpdateUserAgent_UpdatesCurrentAndFiresEvent()
    {
        string newLiveUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36 Edg/133.0.3065.59";
        string? eventReceivedUa = null;

        Action<string> handler = ua => eventReceivedUa = ua;
        HttpUserAgentService.UserAgentChanged += handler;

        try
        {
            HttpUserAgentService.UpdateUserAgent($"\"{newLiveUa}\""); // Should strip JSON quotes

            Assert.Equal(newLiveUa, HttpUserAgentService.CurrentUserAgent);
            Assert.Equal(newLiveUa, eventReceivedUa);
            Assert.Equal("133", HttpUserAgentService.ChromiumMajorVersion);
        }
        finally
        {
            HttpUserAgentService.UserAgentChanged -= handler;
        }
    }

    [Fact]
    public void HttpUserAgentService_IgnoresInvalidOrEmptyUpdate()
    {
        var originalUa = HttpUserAgentService.CurrentUserAgent;

        HttpUserAgentService.UpdateUserAgent("");
        Assert.Equal(originalUa, HttpUserAgentService.CurrentUserAgent);

        HttpUserAgentService.UpdateUserAgent("InvalidAgent/1.0");
        Assert.Equal(originalUa, HttpUserAgentService.CurrentUserAgent);
    }

    [Theory]
    [InlineData(HttpContentType.Html)]
    [InlineData(HttpContentType.Image)]
    [InlineData(HttpContentType.BinaryOrAny)]
    public void HttpUserAgentService_AppliesStandardBrowserHeaders(HttpContentType contentType)
    {
        using var client = HttpUserAgentService.CreateHttpClient(TimeSpan.FromSeconds(10), contentType);
        var headers = client.DefaultRequestHeaders;

        // User-Agent
        Assert.True(headers.Contains("User-Agent"));
        var actualUserAgent = headers.UserAgent.ToString();
        Assert.Equal(HttpUserAgentService.CurrentUserAgent, actualUserAgent);

        // Accept
        Assert.True(headers.Contains("Accept"));
        var accept = headers.GetValues("Accept").AsEnumerable().First();
        if (contentType == HttpContentType.Html)
            Assert.Contains("text/html", accept);
        else if (contentType == HttpContentType.Image)
            Assert.Contains("image/", accept);
        else
            Assert.Contains("*/*", accept);

        // Accept-Language
        Assert.True(headers.Contains("Accept-Language"));

        // Accept-Encoding
        Assert.True(headers.Contains("Accept-Encoding"));
        Assert.Contains("gzip", headers.GetValues("Accept-Encoding").AsEnumerable().First());

        // Modern Client Hints
        Assert.True(headers.Contains("Sec-Ch-Ua"));
        Assert.True(headers.Contains("Sec-Ch-Ua-Mobile"));
        Assert.True(headers.Contains("Sec-Ch-Ua-Platform"));
        Assert.True(headers.Contains("Upgrade-Insecure-Requests"));
    }

    [Fact]
    public async Task HttpJitterHelper_DelayWithinExpectedRange()
    {
        HttpJitterHelper.IsEnabled = true;

        int min = 80;
        int max = 180;

        var sw = Stopwatch.StartNew();
        var delayReturned = await HttpJitterHelper.DelayJitterAsync(min, max);
        sw.Stop();

        Assert.InRange(delayReturned, min, max);
        Assert.True(sw.ElapsedMilliseconds >= min - 25, $"Elapsed was {sw.ElapsedMilliseconds}ms, expected >= {min - 25}ms");
    }

    [Fact]
    public async Task HttpJitterHelper_RespectsDisabledFlag()
    {
        HttpJitterHelper.IsEnabled = false;

        try
        {
            var sw = Stopwatch.StartNew();
            var delayReturned = await HttpJitterHelper.DelayJitterAsync(500, 1000);
            sw.Stop();

            Assert.Equal(0, delayReturned);
            Assert.True(sw.ElapsedMilliseconds < 100);
        }
        finally
        {
            HttpJitterHelper.IsEnabled = true;
        }
    }

    [Fact]
    public async Task HttpJitterHelper_RespectsCancellationToken()
    {
        HttpJitterHelper.IsEnabled = true;

        using var cts = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await HttpJitterHelper.DelayJitterAsync(1000, 2000, cts.Token);
        });
    }
}
