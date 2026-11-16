using System.IO;
using System.Net;
using System.Net.Sockets;
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
        var receivedEncodings = new List<string?>();
        var directory = Directory.CreateTempSubdirectory("RedirectTransfer_");
        var server = Task.Run(async () =>
        {
            for (int hop = 0; hop < 2; hop++)
            {
                using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                Assert.StartsWith(metadata ? "HEAD " : "GET ", await reader.ReadLineAsync(timeout.Token));
                string? cookie = null;
                string? encoding = null;
                while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } line)
                {
                    if (line.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
                        cookie = line[7..].Trim();
                    if (line.StartsWith("Accept-Encoding:", StringComparison.OrdinalIgnoreCase))
                        encoding = line[16..].Trim();
                }
                receivedCookies.Add(cookie);
                receivedEncodings.Add(encoding);
                var response = hop == 0
                    ? $"HTTP/1.1 302 Found\r\nLocation: {target}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                    : "HTTP/1.1 200 OK\r\nContent-Length: 4\r\nConnection: close\r\n\r\n" + (metadata ? "" : "data");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), timeout.Token);
            }
        }, timeout.Token);
        try
        {
            string path = Path.Combine(directory.FullName, "payload.bin");
            var item = new DownloadItem
            {
                OriginalUrl = source.AbsoluteUri,
                DirectDownloadUrl = source.AbsoluteUri,
                Cookies = "session=synthetic",
                FileName = "payload.bin",
                SaveFilePath = path,
                IsCustomName = true
            };
            if (metadata)
            {
                Assert.True(await LinkMetadataResolverService.Instance.ResolveItemMetadataAsync(item, timeout.Token));
                Assert.Equal(4, item.TotalBytes);
                Assert.False(File.Exists(path));
            }
            else
            {
                var engine = new DownloadEngine { MaxConnectionsPerDownload = 1 };
                try { await engine.ResumeDownloadAsync(item).WaitAsync(timeout.Token); }
                finally { await engine.PauseAllAsync(); }
                Assert.Equal(DownloadStatus.Completed, item.Status);
                Assert.Equal("data", File.ReadAllText(path));
            }
            await server;
            Assert.Equal("session=synthetic", receivedCookies[0]);
            Assert.Null(receivedCookies[1]);
            Assert.Equal(new[] { "identity", "identity" }, receivedEncodings);
        }
        finally
        {
            timeout.Cancel();
            listener.Stop();
            await server.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            directory.Delete(recursive: true);
        }
    }
}
