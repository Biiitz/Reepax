using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Reepax.Services.Storage;

namespace Reepax.Services.Download;

internal sealed class LocalDownloadIntegrity : IDisposable
{
    internal const int BlockSize = 4 * 1024 * 1024;
    private readonly object _sync = new();
    private readonly List<string> _completeBlocks = new();
    private readonly IncrementalHash _partial = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private int _partialLength;
    private long _length;

    internal void Append(ReadOnlySpan<byte> bytes)
    {
        lock (_sync)
        {
            while (!bytes.IsEmpty)
            {
                int count = Math.Min(bytes.Length, BlockSize - _partialLength);
                _partial.AppendData(bytes[..count]);
                _partialLength += count;
                _length += count;
                bytes = bytes[count..];
                if (_partialLength == BlockSize)
                {
                    _completeBlocks.Add(Convert.ToHexString(_partial.GetHashAndReset()));
                    _partialLength = 0;
                }
            }
        }
    }

    internal (long Length, List<string> Hashes) Snapshot()
    {
        lock (_sync)
        {
            var hashes = new List<string>(_completeBlocks);
            if (_partialLength > 0) hashes.Add(Convert.ToHexString(_partial.GetCurrentHash()));
            return (_length, hashes);
        }
    }

    internal static LocalDownloadIntegrity ReadVerifiedPrefix(SafeFileHandle handle, long start, long length,
        IReadOnlyList<string>? expected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = new LocalDownloadIntegrity();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BlockSize);
        try
        {
            long offset = 0;
            while (offset < length)
            {
                token.ThrowIfCancellationRequested();
                int count = (int)Math.Min(BlockSize, length - offset);
                int read = 0;
                while (read < count)
                {
                    token.ThrowIfCancellationRequested();
                    int received = RandomAccess.Read(handle, buffer.AsSpan(read, count - read), start + offset + read);
                    if (received == 0) return result;
                    read += received;
                }
                if (expected != null)
                {
                    string hash = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, count)));
                    if (offset / BlockSize >= expected.Count || !string.Equals(hash, expected[(int)(offset / BlockSize)], StringComparison.OrdinalIgnoreCase))
                        return result;
                    if (count == BlockSize)
                    {
                        result._completeBlocks.Add(hash);
                        result._length += count;
                    }
                    else result.Append(buffer.AsSpan(0, count));
                }
                else result.Append(buffer.AsSpan(0, count));
                offset += count;
            }
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    internal void VerifyFile(SafeFileHandle handle, long start, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        VerificationFileIo.LowerPriority(handle);
        var snapshot = Snapshot();
        using var actual = ReadVerifiedPrefix(handle, start, snapshot.Length, snapshot.Hashes, token);
        if (actual.Snapshot().Length != snapshot.Length)
            throw new InvalidDataException("Downloaded bytes changed after being written.");
    }

    public void Dispose() => _partial.Dispose();
}
