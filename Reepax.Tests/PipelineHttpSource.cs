using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Reepax.Tests;

internal sealed class PipelineHttpSource : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<Task> _connections = new();
    private readonly IReadOnlyDictionary<string, byte[]> _files;
    private readonly Task _accept;
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ArchiveRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal ConcurrentDictionary<string, int> Requests { get; } = new();

    internal PipelineHttpSource(IReadOnlyDictionary<string, byte[]> files)
    {
        _files = files;
        _listener.Start();
        _accept = AcceptAsync();
    }

    internal string Url(string file) => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/{file}";

    internal void ReleaseArchive() => _release.TrySetResult();

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _connections.Add(ServeAsync(client));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                string[] request = (await reader.ReadLineAsync(_stop.Token) ?? "").Split(' ');
                if (request.Length < 2) throw new InvalidDataException("Invalid local HTTP request.");
                for (int index = 0; index < 128; index++)
                {
                    string? header = await reader.ReadLineAsync(_stop.Token);
                    if (string.IsNullOrEmpty(header)) break;
                }
                string name = Uri.UnescapeDataString(request[1].TrimStart('/'));
                if (!_files.TryGetValue(name, out var body)) throw new InvalidDataException("Unexpected local HTTP target.");
                Requests.AddOrUpdate(name, 1, (_, count) => count + 1);
                string headers = $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nContent-Type: application/octet-stream\r\nETag: \"fixture-v1\"\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), _stop.Token);
                await stream.FlushAsync(_stop.Token);
                if (request[0] == "HEAD") return;
                if (name == "archive.zip")
                {
                    ArchiveRequested.TrySetResult();
                    await _release.Task.WaitAsync(_stop.Token);
                }
                await stream.WriteAsync(body, _stop.Token);
                await stream.FlushAsync(_stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (IOException) when (_stop.IsCancellationRequested) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            await _accept;
            await Task.WhenAll(_connections);
        }
        finally
        {
            _stop.Dispose();
        }
    }
}
