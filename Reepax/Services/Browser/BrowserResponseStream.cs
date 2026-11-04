using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace Reepax.Services.Browser;

internal sealed class BrowserResponseStream(CoreWebView2 browser, Dispatcher dispatcher, string requestId, string handle) : Stream
{
    private int _closed;
    private readonly object _closeLock = new();
    private Task? _closeTask;

    public override bool CanRead => Volatile.Read(ref _closed) == 0;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(!CanRead, this);
        if (buffer.Length == 0) return 0;
        while (true)
        {
            string json = await CallAsync("IO.read", new { handle, size = Math.Min(buffer.Length, 512 * 1024) }).WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            using var message = JsonDocument.Parse(json);
            string data = message.RootElement.GetProperty("data").GetString()!;
            byte[] bytes = message.RootElement.TryGetProperty("base64Encoded", out var encoded) && encoded.GetBoolean()
                ? Convert.FromBase64String(data) : Encoding.UTF8.GetBytes(data);
            if (bytes.Length > buffer.Length) throw new IOException("The browser response exceeded the requested buffer.");
            bytes.CopyTo(buffer);
            if (bytes.Length > 0 || message.RootElement.GetProperty("eof").GetBoolean()) return bytes.Length;
        }
    }

    private Task<string> CallAsync(string method, object parameters)
        => dispatcher.InvokeAsync(() => browser.CallDevToolsProtocolMethodAsync(method, JsonSerializer.Serialize(parameters))).Task.Unwrap();

    private Task CloseAsync()
    {
        lock (_closeLock) return _closeTask ??= CloseCoreAsync();
    }

    private async Task CloseCoreAsync()
    {
        Interlocked.Exchange(ref _closed, 1);
        try { await CallAsync("IO.close", new { handle }).ConfigureAwait(false); }
        catch (Exception) { }
        try
        {
            // Finish the intercepted navigation without replaying it or replacing the current page.
            await CallAsync("Fetch.fulfillRequest", new { requestId, responseCode = 204, body = "" }).ConfigureAwait(false);
        }
        catch (Exception) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _ = CloseAsync();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
