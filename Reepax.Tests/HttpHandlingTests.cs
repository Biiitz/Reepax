using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Helpers;
using Reepax.Models;
using Reepax.Services.Download;
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

    [Fact]
    public void DownloadEngine_DoesNotModifyDefaultRequestHeadersOnUserAgentUpdate()
    {
        var initialUa = HttpUserAgentService.CurrentUserAgent;
        var engine = new DownloadEngine();

        var initialEngineUa = engine.HttpClient.DefaultRequestHeaders.UserAgent.ToString();

        // Simulate WebView2 providing a new live user agent
        string newLiveUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36 Edg/134.0.0.0";
        HttpUserAgentService.UpdateUserAgent(newLiveUa);

        try
        {
            // DefaultRequestHeaders on the active HttpClient must NOT have been mutated!
            Assert.Equal(initialEngineUa, engine.HttpClient.DefaultRequestHeaders.UserAgent.ToString());
            Assert.Equal(newLiveUa, HttpUserAgentService.CurrentUserAgent);
        }
        finally
        {
            HttpUserAgentService.UpdateUserAgent(initialUa);
        }
    }

    [Fact]
    public async Task DownloadEngine_ApplyRequestHeaders_SendsLiveUserAgentAndChromiumHintsPerRequest()
    {
        var initialUa = HttpUserAgentService.CurrentUserAgent;
        string liveUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36 Edg/135.0.0.0";
        HttpUserAgentService.UpdateUserAgent(liveUa);

        try
        {
            string? capturedUa = null;
            string? capturedSecChUa = null;

            var handler = new TestMockHttpMessageHandler((req, ct) =>
            {
                capturedUa = req.Headers.UserAgent?.ToString();
                if (req.Headers.TryGetValues("Sec-Ch-Ua", out var values))
                {
                    capturedSecChUa = values.FirstOrDefault();
                }
                var res = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[10]) };
                return Task.FromResult(res);
            });

            using var httpClient = new HttpClient(handler);
            var engine = new DownloadEngine(httpClient);

            var tempFile = Path.Combine(Path.GetTempPath(), "ua_test_" + Guid.NewGuid().ToString("N") + ".bin");
            var item = new DownloadItem
            {
                FileName = Path.GetFileName(tempFile),
                SaveFilePath = tempFile,
                DirectDownloadUrl = "https://example.com/test.bin",
                Status = DownloadStatus.Queued
            };

            await engine.StartDownloadAsync(item, item.DirectDownloadUrl, null, null, null, item.FileName);

            Assert.Equal(liveUa, capturedUa);
            Assert.NotNull(capturedSecChUa);
            Assert.Contains("135", capturedSecChUa);

            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            try { if (File.Exists(tempFile + ".part")) File.Delete(tempFile + ".part"); } catch { }
        }
        finally
        {
            HttpUserAgentService.UpdateUserAgent(initialUa);
        }
    }

    [Fact]
    public async Task DownloadEngine_ConcurrentRequestsAndLiveUserAgentUpdates_DoNotThrowOrDropHeaders()
    {
        var initialUa = HttpUserAgentService.CurrentUserAgent;
        var capturedHeaders = new ConcurrentBag<(string? UA, string? SecCh)>();
        var exceptions = new ConcurrentBag<Exception>();

        var handler = new TestMockHttpMessageHandler(async (req, ct) =>
        {
            var ua = req.Headers.UserAgent?.ToString();
            string? secCh = req.Headers.TryGetValues("Sec-Ch-Ua", out var vals) ? vals.FirstOrDefault() : null;
            capturedHeaders.Add((ua, secCh));

            await Task.Delay(Random.Shared.Next(1, 5), ct);
            var res = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[100]) };
            return res;
        });

        using var httpClient = new HttpClient(handler);
        var engine = new DownloadEngine(httpClient);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        // 20 parallel workers simulating download requests
        var workers = Enumerable.Range(0, 20).Select(async i =>
        {
            var tempFile = Path.Combine(Path.GetTempPath(), $"conc_ua_{i}_{Guid.NewGuid():N}.bin");
            try
            {
                var item = new DownloadItem
                {
                    FileName = Path.GetFileName(tempFile),
                    SaveFilePath = tempFile,
                    DirectDownloadUrl = $"https://example.com/file_{i}.bin",
                    Status = DownloadStatus.Queued
                };

                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await engine.StartDownloadAsync(item, item.DirectDownloadUrl, null, null, null, item.FileName);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                try { if (File.Exists(tempFile + ".part")) File.Delete(tempFile + ".part"); } catch { }
            }
        });

        // Parallel thread rapidly updating User-Agent from WebView2
        var updater = Task.Run(async () =>
        {
            int ver = 130;
            while (!cts.Token.IsCancellationRequested)
            {
                ver = ver == 130 ? 136 : 130;
                string testUa = $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{ver}.0.0.0 Safari/537.36 Edg/{ver}.0.0.0";
                HttpUserAgentService.UpdateUserAgent(testUa);
                await Task.Delay(5);
            }
        });

        try
        {
            await Task.WhenAll(workers.Concat(new[] { updater }));
        }
        catch (OperationCanceledException) { }
        finally
        {
            HttpUserAgentService.UpdateUserAgent(initialUa);
        }

        // Verify NO exceptions (no InvalidOperationException, no NullReferenceException)
        Assert.Empty(exceptions);

        // Verify every captured request had a non-empty User-Agent
        Assert.NotEmpty(capturedHeaders);
        foreach (var (ua, _) in capturedHeaders)
        {
            Assert.False(string.IsNullOrWhiteSpace(ua), "Request was sent without User-Agent header!");
            Assert.StartsWith("Mozilla/5.0", ua);
        }
    }
}
