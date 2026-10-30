using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Models;

namespace Reepax.Services.Download;

public partial class DownloadEngine
{
    private sealed class RepresentationChangedException : IOException
    {
        internal RepresentationChangedException() : base("The remote file version changed.") { }
    }

    private static string GetSourceKey(DownloadItem item) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(new Uri(item.DirectDownloadUrl!, UriKind.Absolute)
            .GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped))));

    private static string? GetStrongEntityTag(HttpResponseMessage response) =>
        response.Headers.ETag is { IsWeak: false } tag ? tag.ToString() : null;

    private static bool MatchesSource(DownloadItem item, SegmentFileState state) =>
        state.EntityTag == null || string.Equals(state.SourceKey, GetSourceKey(item), StringComparison.Ordinal);

    private static void ApplyRangeValidator(HttpRequestMessage request, SegmentFileState? state)
    {
        if (state?.EntityTag != null)
            request.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(state.EntityTag));
    }

    private static bool MatchesRepresentation(HttpResponseMessage response, SegmentFileState state) =>
        state.EntityTag == null || string.Equals(state.EntityTag, GetStrongEntityTag(response), StringComparison.Ordinal);

    private static bool HasEncodedContent(HttpResponseMessage response) =>
        response.Content.Headers.ContentEncoding.Any(encoding => !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase));

    private static async Task<Stream> DecodeContentAsync(Stream stream, HttpResponseMessage response, CancellationToken token)
    {
        try
        {
            foreach (var encoding in response.Content.Headers.ContentEncoding.Reverse())
            {
                if (encoding.Equals("deflate", StringComparison.OrdinalIgnoreCase))
                {
                    var prefix = new byte[2];
                    int count = 0;
                    while (count < prefix.Length)
                    {
                        int read = await stream.ReadAsync(prefix.AsMemory(count), token);
                        if (read == 0) break;
                        count += read;
                    }
                    bool wrapped = count == 2 && (prefix[0] & 15) == 8 && ((prefix[0] << 8) + prefix[1]) % 31 == 0;
                    var input = new PrefixReadStream(prefix.AsMemory(0, count), stream);
                    stream = wrapped ? new ZLibStream(input, CompressionMode.Decompress) : new DeflateStream(input, CompressionMode.Decompress);
                    continue;
                }
                stream = encoding.ToLowerInvariant() switch
                {
                    "gzip" => new GZipStream(stream, CompressionMode.Decompress),
                    "br" => new BrotliStream(stream, CompressionMode.Decompress),
                    "identity" => stream,
                    _ => throw new InvalidDataException("Unsupported download content encoding.")
                };
            }
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private sealed class PrefixReadStream(ReadOnlyMemory<byte> prefix, Stream remainder) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (_position >= prefix.Length) return remainder.Read(buffer);
            int count = Math.Min(buffer.Length, prefix.Length - _position);
            prefix.Span.Slice(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _position < prefix.Length ? ValueTask.FromResult(Read(buffer.Span)) : remainder.ReadAsync(buffer, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) remainder.Dispose();
            base.Dispose(disposing);
        }
    }

    private static readonly Regex Sha256DigestMember = new(
        @"^sha-256\s*=\s*:([A-Za-z0-9+/]+={0,2}):(?:\s*;.*)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static void ApplyResponseDigest(HttpResponseMessage response, DownloadItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.ExpectedChecksum) || HasEncodedContent(response)) return;
        string? digest = ReadSha256Digest(response, "Repr-Digest");
        if (digest == null && response.StatusCode == HttpStatusCode.OK)
            digest = ReadSha256Digest(response, "Content-Digest");
        if (digest == null) return;
        SafeInvoke(() =>
        {
            if (string.IsNullOrWhiteSpace(item.ExpectedChecksum)) item.ExpectedChecksum = digest;
        });
    }

    private static string? ReadSha256Digest(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var values) &&
            !response.Content.Headers.TryGetValues(name, out values)) return null;
        string? result = null;
        foreach (var member in values.SelectMany(value => value.Split(',')))
        {
            var match = Sha256DigestMember.Match(member.Trim());
            if (!match.Success) continue;
            byte[] bytes;
            try { bytes = Convert.FromBase64String(match.Groups[1].Value); }
            catch (FormatException) { continue; }
            if (bytes.Length != 32) continue;
            string candidate = Convert.ToHexString(bytes);
            if (result != null && !string.Equals(result, candidate, StringComparison.Ordinal))
                throw new InvalidDataException("Conflicting SHA-256 response digests.");
            result = candidate;
        }
        return result;
    }

    private static SegmentFileState SnapshotState(SegmentFileState state) => new()
    {
        TotalBytes = state.TotalBytes,
        IsSequential = state.IsSequential,
        EntityTag = state.EntityTag,
        SourceKey = state.SourceKey,
        Segments = state.Segments.Select(segment => new SegmentState
        {
            Start = segment.Start, End = segment.End, Done = segment.Done
        }).ToList()
    };

    private static void SaveSegmentState(string path, SegmentFileState state, bool required = false, FileStream? dataFile = null)
    {
        string stagingPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var snapshot = SnapshotState(state);
            // Commit data before publishing the corresponding progress snapshot.
            dataFile?.Flush(flushToDisk: true);
            using (var staging = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(staging, snapshot);
                staging.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(stagingPath, path, null);
            else File.Move(stagingPath, path);
        }
        catch when (!required) { }
        finally { File.Delete(stagingPath); }
    }
}
