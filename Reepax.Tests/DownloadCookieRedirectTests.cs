using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Extractor;
using Xunit;

namespace Reepax.Tests;

public class DownloadCookieRedirectTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransferRequest_RedirectToDifferentHost_DoesNotForwardSourceCookies(bool metadata)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var source = new Uri($"http://localhost:{port}/download");
        var target = new Uri($"http://127.0.0.1:{port}/payload");
        var receivedCookies = new List<string?>();
        var server = Task.Run(async () =>
        {
            for (int hop = 0; hop < 2; hop++)
            {
                using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                string? cookie = null;
                while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } line)
                {
                    if (line.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
                        cookie = line[7..].Trim();
                }
                receivedCookies.Add(cookie);
                var response = hop == 0
                    ? $"HTTP/1.1 302 Found\r\nLocation: {target}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                    : "HTTP/1.1 200 OK\r\nContent-Length: 4\r\nConnection: close\r\n\r\ndata";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), timeout.Token);
            }
        }, timeout.Token);
        try
        {
            var serviceType = metadata ? typeof(LinkMetadataResolverService) : typeof(DownloadEngine);
            using var client = metadata
                ? (HttpClient)serviceType.GetField("_httpClient", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(new LinkMetadataResolverService())!
                : new DownloadEngine().HttpClient;
            using var request = new HttpRequestMessage(HttpMethod.Get, source);
            var item = new DownloadItem
            {
                OriginalUrl = source.AbsoluteUri,
                DirectDownloadUrl = source.AbsoluteUri,
                Cookies = "session=synthetic"
            };
            serviceType.GetMethod("ApplyRequestHeaders", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { request, item });
            using var response = await client.SendAsync(request, timeout.Token);
            Assert.Equal("data", await response.Content.ReadAsStringAsync(timeout.Token));
            await server;
            Assert.Equal("session=synthetic", receivedCookies[0]);
            Assert.Null(receivedCookies[1]);
        }
        finally
        {
            timeout.Cancel();
            listener.Stop();
            await server.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
}
