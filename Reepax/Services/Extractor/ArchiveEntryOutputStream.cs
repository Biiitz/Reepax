using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Reepax.Services.Extractor;

internal sealed class ArchiveEntryOutputStream(Stream destination, Action<int> beforeWrite) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _written;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => destination.Flush();
    public override void Write(byte[] buffer, int offset, int count)
    {
        beforeWrite(count);
        destination.Write(buffer, offset, count);
        _hash.AppendData(buffer, offset, count);
        _written += count;
    }
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        beforeWrite(buffer.Length);
        destination.Write(buffer);
        _hash.AppendData(buffer);
        _written += buffer.Length;
    }

    internal void VerifyWrittenBytes(SafeFileHandle handle, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var actual = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            long offset = 0;
            int read;
            while ((read = RandomAccess.Read(handle, buffer, offset)) > 0)
            {
                token.ThrowIfCancellationRequested();
                actual.AppendData(buffer.AsSpan(0, read));
                offset += read;
            }
            if (offset != _written || !CryptographicOperations.FixedTimeEquals(_hash.GetCurrentHash(), actual.GetHashAndReset()))
                throw new InvalidDataException("Extracted bytes changed after being written.");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hash.Dispose();
        base.Dispose(disposing);
    }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
