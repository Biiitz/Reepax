using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Localization;

namespace Reepax.Services.Download;

public partial class DownloadEngine
{
    private TimeSpan _networkReadTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Maximum network read wait, excluding local bandwidth and disk waits.</summary>
    public TimeSpan NetworkReadTimeout
    {
        get => _networkReadTimeout;
        set
        {
            if (value <= TimeSpan.Zero || value > TimeSpan.FromDays(1))
                throw new ArgumentOutOfRangeException(nameof(value));
            _networkReadTimeout = value;
        }
    }

    private sealed class DownloadReadTimeoutException(Exception inner)
        : IOException(Loc.Get("Error_DownloadReadTimeout"), inner);

    private async Task ExecuteSingleConnectionWithRetriesAsync(DownloadItem item, string tempFilePath,
        CancellationToken cancellationToken, byte[]? buffer, SegmentFileState? checkpoint)
    {
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ExecuteSingleConnectionCoreAsync(item, tempFilePath, cancellationToken, buffer, checkpoint);
                return;
            }
            catch (DownloadReadTimeoutException) when (attempt < 2 && !cancellationToken.IsCancellationRequested)
            {
                tempFilePath = item.SaveFilePath + ".part";
                checkpoint = LoadSegmentState(tempFilePath + ".segments");
                await Task.Delay(Random.Shared.Next(400, 1200), cancellationToken);
            }
        }
    }

    private sealed class NetworkReadTimeoutStream : Stream
    {
        private readonly Stream _inner;
        private readonly TimeSpan _timeout;
        private readonly CancellationToken _downloadToken;
        private readonly CancellationTokenSource _readCancellation;
        private readonly CancellationTokenRegistration _abortRegistration;

        internal NetworkReadTimeoutStream(Stream inner, TimeSpan timeout, CancellationToken downloadToken)
        {
            _inner = inner;
            _timeout = timeout;
            _downloadToken = downloadToken;
            _readCancellation = CancellationTokenSource.CreateLinkedTokenSource(downloadToken);
            // Closing the response also aborts reads that ignore cancellation tokens.
            _abortRegistration = _readCancellation.Token.Register(() =>
            {
                try { _inner.Dispose(); } catch { }
            });
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var extraCancellation = cancellationToken != _downloadToken && cancellationToken.CanBeCanceled
                ? cancellationToken.Register(() => _readCancellation.Cancel()) : default;
            try
            {
                _readCancellation.Token.ThrowIfCancellationRequested();
                var read = _inner.ReadAsync(buffer, _readCancellation.Token);
                // Arm one reusable timer only while an actual network read is pending.
                if (!read.IsCompleted) _readCancellation.CancelAfter(_timeout);
                // Finish the cancelled read before its pooled buffer can be reused.
                int count = await read.ConfigureAwait(false);
                _readCancellation.Token.ThrowIfCancellationRequested();
                return count;
            }
            catch (Exception ex) when (_readCancellation.IsCancellationRequested)
            {
                _downloadToken.ThrowIfCancellationRequested();
                cancellationToken.ThrowIfCancellationRequested();
                throw new DownloadReadTimeoutException(ex);
            }
            finally
            {
                _readCancellation.CancelAfter(Timeout.InfiniteTimeSpan);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _abortRegistration.Dispose();
                _readCancellation.Dispose();
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
