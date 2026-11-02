using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Reepax.Models;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;
using Xunit;

namespace Reepax.Tests;

public class CookieScopeTests
{
    [Theory]
    [InlineData("https://files.example.test/allowed/file", "session=synthetic")]
    [InlineData("https://files.example.test/allowed", "session=synthetic")]
    [InlineData("https://files.example.test/allowed-other", "")]
    [InlineData("https://files.example.test/elsewhere", "")]
    [InlineData("https://child.files.example.test/allowed/file", "")]
    [InlineData("https://files.example.test.other.invalid/allowed/file", "")]
    [InlineData("http://files.example.test/allowed/file", "")]
    public async Task BrowserCookie_EnforcesHostPathAndSecureScope(string target, string expected)
    {
        var context = BrowserContext();
        Assert.Equal(expected, await CaptureCookieAsync(context, target));
    }

    [Fact]
    public async Task DomainCookie_RemainsAvailableToAllowedSubdomains()
    {
        var context = DownloadCookieContext.FromBrowserCookies("https://files.example.test/allowed/file",
            [new Cookie("session", "synthetic", "/allowed", ".example.test") { Secure = true }]);
        Assert.Equal("session=synthetic", await CaptureCookieAsync(context, "https://cdn.example.test/allowed/file"));
        Assert.Equal("", await CaptureCookieAsync(context, "https://other.invalid/allowed/file"));
    }

    [Fact]
    public async Task ExpiredAndInvalidCookies_AreNotSent()
    {
        var context = new DownloadCookieContext
        {
            SourceUrl = "https://files.example.test/allowed/file",
            Cookies =
            [
                new() { Name = "expired", Value = "synthetic", Domain = "files.example.test", HostOnly = true,
                    ExpiresUtc = DateTime.UtcNow.AddDays(-1) },
                new() { Name = "foreign", Value = "synthetic", Domain = ".other.invalid" },
                new() { Name = "invalid;name", Value = "synthetic", Domain = "files.example.test", HostOnly = true }
            ]
        };
        Assert.Equal("", await CaptureCookieAsync(context, "https://files.example.test/allowed/file"));
    }

    [Fact]
    public async Task LegacyCookie_IsPinnedBeforeTheDownloadUrlChanges()
    {
        var item = new DownloadItem
        {
            DirectDownloadUrl = "https://files.example.test/file",
            Cookies = "session=synthetic; token=value=with=equals"
        };
        var context = item.GetCookieContext();
        item.DirectDownloadUrl = "https://other.invalid/file";
        Assert.Same(context, item.GetCookieContext());
        Assert.Contains("token=value=with=equals", await CaptureCookieAsync(context, "https://files.example.test/file"));
        Assert.Equal("", await CaptureCookieAsync(context, "https://other.invalid/file"));
        Assert.Equal("", await CaptureCookieAsync(context, "https://child.files.example.test/file"));
        Assert.Equal("", await CaptureCookieAsync(context, "http://files.example.test/file"));
        item.Cookies = "session=replaced";
        Assert.NotSame(context, item.GetCookieContext());
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.SeeOther)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task Redirect_PreservesHeadRangeAndFinalUri(HttpStatusCode status)
    {
        int requests = 0;
        using var client = Client((request, _) =>
        {
            requests++;
            Assert.Equal(HttpMethod.Head, request.Method);
            Assert.Equal("bytes=10-19", request.Headers.Range!.ToString());
            Assert.Equal("identity", request.Headers.AcceptEncoding.ToString());
            if (requests == 1)
                return Task.FromResult(Redirect(status, "/allowed/final"));
            Assert.Null(request.Headers.Authorization);
            Assert.Null(request.Headers.Host);
            Assert.Equal("session=synthetic", CookieHeader(request));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var request = Request(BrowserContext(), "https://files.example.test/allowed/file", HttpMethod.Head);
        request.Headers.Range = new RangeHeaderValue(10, 19);
        request.Headers.AcceptEncoding.ParseAdd("identity");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic");
        request.Headers.Host = "files.example.test";
        using var response = await client.SendAsync(request);
        Assert.Equal(2, requests);
        Assert.Equal("/allowed/final", response.RequestMessage!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task RedirectAcrossHostsAndBack_ReevaluatesCookiesAtEveryHop()
    {
        var received = new List<string>();
        using var client = Client((request, _) =>
        {
            received.Add(CookieHeader(request));
            var response = received.Count switch
            {
                1 => Redirect(HttpStatusCode.Redirect, "https://other.invalid/file"),
                2 => Redirect(HttpStatusCode.Redirect, "https://files.example.test/allowed/final"),
                _ => new HttpResponseMessage(HttpStatusCode.OK)
            };
            if (received.Count == 1) response.Headers.TryAddWithoutValidation("Set-Cookie", "hop=source; Path=/allowed; Secure");
            if (received.Count == 2) response.Headers.TryAddWithoutValidation("Set-Cookie", "foreign=target; Path=/; Secure");
            return Task.FromResult(response);
        });
        using var request = Request(BrowserContext(), "https://files.example.test/allowed/file");
        using var response = await client.SendAsync(request);
        Assert.Equal(3, received.Count);
        Assert.Equal("session=synthetic", received[0]);
        Assert.Equal("", received[1]);
        Assert.Contains("session=synthetic", received[2]);
        Assert.Contains("hop=source", received[2]);
        Assert.DoesNotContain("foreign", received[2]);
    }

    [Fact]
    public async Task SharedClient_IsolatesConcurrentDownloadSessionsAndResponseCookies()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int arrived = 0;
        using var client = Client(async (request, token) =>
        {
            var session = request.RequestUri!.Query[1..];
            Assert.Equal($"session={session}", CookieHeader(request));
            if (Interlocked.Increment(ref arrived) == 2) ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.TryAddWithoutValidation("Set-Cookie", $"response={session}; Path=/");
            return response;
        });
        var first = DownloadCookieContext.FromHeader("https://files.example.test/file", "session=first");
        var second = DownloadCookieContext.FromHeader("https://files.example.test/file", "session=second");
        using var firstRequest = Request(first, "https://files.example.test/file?first");
        using var secondRequest = Request(second, "https://files.example.test/file?second");
        var firstSend = client.SendAsync(firstRequest);
        var secondSend = client.SendAsync(secondRequest);
        using var firstResponse = await firstSend;
        using var secondResponse = await secondSend;
        Assert.Contains("response=first", await CaptureCookieAsync(first, "https://files.example.test/file"));
        Assert.DoesNotContain("second", await CaptureCookieAsync(first, "https://files.example.test/file"));
        Assert.Contains("response=second", await CaptureCookieAsync(second, "https://files.example.test/file"));
    }

    [Fact]
    public async Task SecureRedirect_DoesNotDowngradeToHttp()
    {
        int requests = 0;
        using var client = Client((_, _) =>
        {
            requests++;
            return Task.FromResult(Redirect(HttpStatusCode.Redirect, "http://files.example.test/allowed/file"));
        });
        using var request = Request(BrowserContext(), "https://files.example.test/allowed/file");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task RedirectLoop_IsBounded()
    {
        int requests = 0;
        using var client = Client((_, _) =>
        {
            requests++;
            return Task.FromResult(Redirect(HttpStatusCode.Redirect, "/allowed/file"));
        });
        using var request = Request(BrowserContext(), "https://files.example.test/allowed/file");
        using var response = await client.SendAsync(request);
        Assert.Equal(11, requests);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_StopsTheRedirectChain(bool cancelDuringResponse)
    {
        using var cancellation = new CancellationTokenSource();
        int requests = 0;
        using var client = Client((_, _) =>
        {
            requests++;
            cancellation.Cancel();
            return Task.FromResult(Redirect(HttpStatusCode.Redirect, "/allowed/final"));
        });
        if (!cancelDuringResponse) cancellation.Cancel();
        using var request = Request(BrowserContext(), "https://files.example.test/allowed/file");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(request, cancellation.Token));
        Assert.Equal(cancelDuringResponse ? 1 : 0, requests);
    }

    [Fact]
    public async Task LegacyPersistence_BindsCookiesBeforeAReplacementUrlIsResolved()
    {
        var root = Path.Combine(Path.GetTempPath(), "legacy-cookie-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "downloads.json");
            var package = new DownloadPackageDto
            {
                Id = Guid.NewGuid(), Name = "Sample", SaveDirectory = root,
                Items = [new DownloadItemDto
                {
                    Id = Guid.NewGuid(), FileName = "sample.bin",
                    OriginalUrl = "https://page.example.test/file",
                    DirectDownloadUrl = "https://files.example.test/file",
                    Cookies = "session=synthetic"
                }]
            };
            File.WriteAllText(file, JsonSerializer.Serialize(new[] { package }));
            using var service = new DownloadPersistenceService(file);
            var item = Assert.Single(Assert.Single(service.LoadDownloads()).Items);
            item.DirectDownloadUrl = "https://other.invalid/file";
            Assert.Equal("", await CaptureCookieAsync(item.GetCookieContext(), item.DirectDownloadUrl));
            Assert.Equal("session=synthetic", await CaptureCookieAsync(item.GetCookieContext(), "https://files.example.test/file"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Persistence_RestoresCookieScopeForDownloadsAndHistory(bool history)
    {
        var root = Path.Combine(Path.GetTempPath(), "cookie-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var service = new DownloadPersistenceService(Path.Combine(root, "downloads.json"));
            var package = new DownloadPackage { Name = "Sample", SaveDirectory = root };
            package.Items.Add(new DownloadItem
            {
                FileName = "sample.bin",
                OriginalUrl = "https://files.example.test/allowed/file",
                DirectDownloadUrl = "https://files.example.test/allowed/file",
                Cookies = "session=synthetic",
                CookieContext = BrowserContext()
            });
            if (history) service.SaveHistory([package], sync: true);
            else service.SaveDownloads([package]);
            var restored = Assert.Single(Assert.Single(history ? service.LoadHistory() : service.LoadDownloads()).Items);
            Assert.NotNull(restored.CookieContext);
            Assert.Equal("session=synthetic", await CaptureCookieAsync(restored.CookieContext, "https://files.example.test/allowed/file"));
            Assert.Equal("", await CaptureCookieAsync(restored.CookieContext, "https://files.example.test/elsewhere"));
            Assert.Equal("", await CaptureCookieAsync(restored.CookieContext, "https://child.files.example.test/allowed/file"));
            var dto = new DownloadItemDto { CookieContext = restored.CookieContext };
            var serialized = JsonSerializer.Serialize(dto);
            Assert.DoesNotContain("Container", serialized);
            var roundTrip = JsonSerializer.Deserialize<DownloadItemDto>(serialized)!;
            Assert.True(Assert.Single(roundTrip.CookieContext!.Cookies).HttpOnly);
            Assert.NotNull(Assert.Single(roundTrip.CookieContext.Cookies).ExpiresUtc);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static DownloadCookieContext BrowserContext()
        => DownloadCookieContext.FromBrowserCookies("https://files.example.test/allowed/file",
            [new Cookie("session", "synthetic", "/allowed", "files.example.test")
                { Secure = true, HttpOnly = true, Expires = DateTime.UtcNow.AddHours(1) }]);

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        => new(new CookieRedirectHandler(new TestMockHttpMessageHandler(send)));

    private static HttpRequestMessage Request(DownloadCookieContext context, string url, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, url);
        var item = new DownloadItem { CookieContext = context };
        CookieRedirectHandler.ApplyCookies(request, item);
        return request;
    }

    private static HttpResponseMessage Redirect(HttpStatusCode status, string location)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static string CookieHeader(HttpRequestMessage request)
        => request.Headers.TryGetValues("Cookie", out var values) ? string.Join("; ", values) : "";

    private static async Task<string> CaptureCookieAsync(DownloadCookieContext context, string url)
    {
        string? captured = null;
        using var client = Client((request, _) =>
        {
            captured = CookieHeader(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var request = Request(context, url);
        using var response = await client.SendAsync(request);
        return captured!;
    }
}
