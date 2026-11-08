using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace Reepax.Tests;

internal sealed class PipelineHttpSource : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<Task> _connections = new();
    private readonly IReadOnlyDictionary<string, byte[]> _files;
    private readonly IReadOnlyDictionary<string, string>? _responseHeaders;
    private readonly Func<string, string>? _contentType;
    private readonly Func<string, long, Stream, ReadOnlyMemory<byte>, CancellationToken, Task>? _writeBody;
    private readonly Task _accept;
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ArchiveRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal ConcurrentDictionary<string, int> Requests { get; } = new();
    internal ConcurrentQueue<(string File, long? Start)> Ranges { get; } = new();

    internal PipelineHttpSource(IReadOnlyDictionary<string, byte[]> files,
        Func<string, long, Stream, ReadOnlyMemory<byte>, CancellationToken, Task>? writeBody = null,
        IReadOnlyDictionary<string, string>? responseHeaders = null, Func<string, string>? contentType = null)
    {
        _files = files;
        _writeBody = writeBody;
        _responseHeaders = responseHeaders;
        _contentType = contentType;
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
                string? requestLine = await reader.ReadLineAsync(_stop.Token);
                // Browsers may close speculative connections without sending a request.
                if (requestLine == null) return;
                string[] request = requestLine.Split(' ');
                if (request.Length < 2) throw new InvalidDataException("Invalid local HTTP request.");
                string? requestedRange = null;
                for (int index = 0; index < 128; index++)
                {
                    string? header = await reader.ReadLineAsync(_stop.Token);
                    if (string.IsNullOrEmpty(header)) break;
                    if (header.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) requestedRange = header[6..].Trim();
                }
                string name = Uri.UnescapeDataString(request[1].TrimStart('/'));
                if (!_files.TryGetValue(name, out var body)) throw new InvalidDataException("Unexpected local HTTP target.");
                Requests.AddOrUpdate(name, 1, (_, count) => count + 1);
                long first = 0, last = body.Length - 1;
                if (requestedRange != null)
                {
                    if (!RangeHeaderValue.TryParse(requestedRange, out var parsed) || parsed.Ranges.Count != 1)
                        throw new InvalidDataException("Invalid local HTTP range.");
                    var range = parsed.Ranges.Single();
                    first = range.From ?? throw new InvalidDataException("A range start is required.");
                    last = range.To ?? last;
                    if (first < 0 || first > last || last >= body.Length) throw new InvalidDataException("Invalid local HTTP range bounds.");
                }
                Ranges.Enqueue((name, requestedRange == null ? null : first));
                string status = requestedRange == null ? "200 OK" : "206 Partial Content";
                string contentRange = requestedRange == null ? "" : $"Content-Range: bytes {first}-{last}/{body.Length}\r\n";
                string extraHeaders = _responseHeaders == null ? "ETag: \"fixture-v1\"\r\n"
                    : string.Concat(_responseHeaders.Select(header => $"{header.Key}: {header.Value}\r\n"));
                string headers = $"HTTP/1.1 {status}\r\nContent-Length: {last - first + 1}\r\n{contentRange}Content-Type: {_contentType?.Invoke(name) ?? "application/octet-stream"}\r\n{extraHeaders}Connection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), _stop.Token);
                await stream.FlushAsync(_stop.Token);
                if (request[0] == "HEAD") return;
                if (name == "archive.zip")
                {
                    ArchiveRequested.TrySetResult();
                    await _release.Task.WaitAsync(_stop.Token);
                }
                var content = body.AsMemory((int)first, (int)(last - first + 1));
                if (_writeBody != null) await _writeBody(name, first, stream, content, _stop.Token);
                else await stream.WriteAsync(content, _stop.Token);
                await stream.FlushAsync(_stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (IOException) when (_stop.IsCancellationRequested) { }
            catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted }) { }
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
