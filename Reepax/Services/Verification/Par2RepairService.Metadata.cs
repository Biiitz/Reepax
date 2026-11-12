using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Reepax.Services.Storage;

namespace Reepax.Services.Verification;

public partial class Par2RepairService
{
    private sealed class PackageRepairInput(FileStream parity, string path, string[] targets) : IDisposable
    {
        private static readonly Regex VolumeSuffix = new(@"\.(?:v(?:o(?:l[0-9]*(?:[+-][0-9]*)?)?)?)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal string IndexPath { get; } = path;
        internal string[] Targets { get; } = targets;
        internal string[] RecoveryVolumes { get; private init; } = [];
        private DirectoryInfo? ScratchDirectory { get; init; }

        internal static PackageRepairInput? Open(string primary, string directory, CancellationToken ct)
        {
            var streams = new List<FileStream>();
            DirectoryInfo? scratch = null;
            try
            {
                streams.Add(File.OpenRead(primary));
                var metadata = new RepairMetadata();
                metadata.Read(streams[0], ct);
                if (metadata.IsComplete)
                {
                    var input = new PackageRepairInput(streams[0], primary, metadata.GetTargets(directory));
                    streams.Clear();
                    return input;
                }

                // The primary file can omit metadata carried by a matching recovery volume.
                var volumes = FindRecoveryVolumes(primary, directory).ToArray();
                foreach (string volume in volumes.Skip(1))
                {
                    ct.ThrowIfCancellationRequested();
                    try { streams.Add(File.OpenRead(volume)); }
                    catch (FileNotFoundException) { continue; }
                    metadata.Read(streams[^1], ct);
                    if (metadata.IsComplete) break;
                }

                if (!metadata.IsComplete) return null;
                string[] targets = metadata.GetTargets(directory);

                // The native reader loads these validated packets first, then uses the volumes for recovery data.
                scratch = Directory.CreateTempSubdirectory("ReepaxPar2_");
                string indexPath = Path.Combine(scratch.FullName, "index.par2");
                using (var index = File.Open(indexPath, FileMode.CreateNew, FileAccess.Write))
                    metadata.WriteIndex(index, ct);
                var prepared = new PackageRepairInput(File.OpenRead(indexPath), indexPath, targets)
                {
                    RecoveryVolumes = volumes,
                    ScratchDirectory = scratch
                };
                scratch = null;
                return prepared;
            }
            finally
            {
                foreach (var stream in streams) stream.Dispose();
                if (scratch != null) DeleteIndex(scratch);
            }
        }

        private static IEnumerable<string> FindRecoveryVolumes(string primary, string directory)
        {
            yield return AsRecoveryArgument(primary);
            string stem = VolumeSuffix.Replace(Path.GetFileNameWithoutExtension(primary), string.Empty);
            var options = new EnumerationOptions
            {
                MatchType = MatchType.Win32, AttributesToSkip = 0, IgnoreInaccessible = false
            };
            string prefix = stem.Length == 0 ? "*" : stem + ".*";
            var found = Directory.EnumerateFiles(directory, prefix + ".par2", options)
                .Concat(Directory.EnumerateFiles(directory, prefix + ".PAR2", options));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { primary };
            foreach (string volume in found)
            {
                if (seen.Add(volume))
                    yield return AsRecoveryArgument(volume);
            }
        }

        private static string AsRecoveryArgument(string path)
        {
            if (path.Contains(".par2", StringComparison.Ordinal) || path.Contains(".PAR2", StringComparison.Ordinal))
                return path;

            // The native reader identifies explicitly supplied volumes by their extension.
            string lowerCase = path[..^4] + "par2";
            if (!File.Exists(lowerCase)) throw new InvalidDataException();
            return lowerCase;
        }

        public void Dispose()
        {
            parity.Dispose();
            if (ScratchDirectory != null) DeleteIndex(ScratchDirectory);
        }

        private static void DeleteIndex(DirectoryInfo scratch)
        {
            try
            {
                File.Delete(Path.Combine(scratch.FullName, "index.par2"));
                scratch.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLogger.Warn("[Par2RepairService] Could not remove temporary index", ex);
            }
        }
    }

    private sealed class RepairMetadata
    {
        private static readonly UTF8Encoding FileNameEncoding = new(false, true);
        private readonly byte[] _buffer = new byte[64 * 1024];
        private readonly Dictionary<string, (string Name, PacketLocation Packet)> _descriptions = [];
        private string? _set;
        private PacketLocation _main;
        private string[]? _fileIds;
        private HashSet<string>? _missingIds;

        internal bool IsComplete => _missingIds is { Count: 0 };

        internal void Read(FileStream stream, CancellationToken ct)
        {
            byte[] header = new byte[64];
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            while (!IsComplete && stream.Length - stream.Position >= header.Length)
            {
                ct.ThrowIfCancellationRequested();
                long start = stream.Position;
                stream.ReadExactly(header);
                ulong length = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8, 8));
                if (!header.AsSpan(0, 8).SequenceEqual("PAR2\0PKT"u8) || length < 64 || length % 4 != 0 ||
                    length > (ulong)(stream.Length - start))
                {
                    if (!FindNextPacket(stream, start + 1, ct)) return;
                    continue;
                }

                string set = Convert.ToHexString(header.AsSpan(32, 16));
                long bodyLength = (long)length - header.Length;
                bool main = header.AsSpan(48, 16).SequenceEqual("PAR 2.0\0Main\0\0\0\0"u8);
                bool description = header.AsSpan(48, 16).SequenceEqual("PAR 2.0\0FileDesc"u8);
                bool keepBody = (_set == null || _set == set) &&
                    ((main && _fileIds == null && bodyLength is >= 12 and <= 12 + 32768 * 16) ||
                     (description && bodyLength is > 56 and <= 56 + 100000));
                byte[]? body = keepBody ? new byte[(int)bodyLength] : null;
                hash.AppendData(header, 32, 32);
                if (body != null)
                {
                    stream.ReadExactly(body);
                    hash.AppendData(body);
                }
                else
                {
                    for (long remaining = bodyLength; remaining > 0;)
                    {
                        ct.ThrowIfCancellationRequested();
                        int count = (int)Math.Min(remaining, _buffer.Length);
                        stream.ReadExactly(_buffer.AsSpan(0, count));
                        hash.AppendData(_buffer, 0, count);
                        remaining -= count;
                    }
                }

                if (!hash.GetHashAndReset().AsSpan().SequenceEqual(header.AsSpan(16, 16)))
                {
                    if (!FindNextPacket(stream, start + 1, ct)) return;
                    continue;
                }

                // The native reader fixes the set from its first valid packet and keeps the first valid description per ID.
                _set ??= set;
                if (_set != set || body == null) continue;
                var packet = new PacketLocation(stream, start, (int)length);
                if (main) ReadMain(body, packet);
                else ReadDescription(body, packet);
            }
        }

        private bool FindNextPacket(FileStream stream, long offset, CancellationToken ct)
        {
            stream.Position = offset;
            while (stream.Length - stream.Position >= 8)
            {
                ct.ThrowIfCancellationRequested();
                int count = (int)Math.Min(stream.Length - stream.Position, _buffer.Length);
                stream.ReadExactly(_buffer.AsSpan(0, count));
                int index = _buffer.AsSpan(0, count).IndexOf("PAR2\0PKT"u8);
                if (index >= 0)
                {
                    stream.Position += index - count;
                    return true;
                }
                stream.Position -= 7;
            }
            return false;
        }

        private void ReadMain(byte[] body, PacketLocation packet)
        {
            ulong blockSize = BinaryPrimitives.ReadUInt64LittleEndian(body);
            int count = (body.Length - 12) / 16;
            uint recoverable = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8));
            if (blockSize == 0 || blockSize % 4 != 0 || (body.Length - 12) % 16 != 0 || recoverable > count)
                return;
            _main = packet;
            _fileIds = new string[count];
            for (int index = 0; index < count; index++)
                _fileIds[index] = Convert.ToHexString(body.AsSpan(12 + index * 16, 16));
            _missingIds = new HashSet<string>(_fileIds, StringComparer.Ordinal);
            _missingIds.ExceptWith(_descriptions.Keys);
        }

        private void ReadDescription(byte[] body, PacketLocation packet)
        {
            if (BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(48)) <= 16384 &&
                !body.AsSpan(16, 16).SequenceEqual(body.AsSpan(32, 16)))
                return;
            string id = Convert.ToHexString(body.AsSpan(0, 16));
            if (_descriptions.ContainsKey(id) || (_missingIds != null && !_missingIds.Contains(id))) return;
            var name = body.AsSpan(56);
            int terminator = name.IndexOf((byte)0);
            if (terminator >= 0) name = name[..terminator];
            _descriptions.Add(id, (FileNameEncoding.GetString(name), packet));
            _missingIds?.Remove(id);
        }

        internal string[] GetTargets(string directory)
        {
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string prefix = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
            foreach (string id in _fileIds!)
            {
                string name = _descriptions[id].Name;
                string[] parts = name.Replace('\\', '/').Split('/');
                if (parts.Any(part => string.IsNullOrWhiteSpace(part) || part.EndsWith(' ') || part.EndsWith('.') ||
                    part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsDeviceName(part)))
                    throw new InvalidDataException();
                string target = Path.GetFullPath(Path.Combine(directory, name));
                if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !targets.Add(target))
                    throw new InvalidDataException();
            }
            if (targets.Count == 0) throw new InvalidDataException();
            return targets.ToArray();
        }

        private static bool IsDeviceName(string part)
        {
            string name = part.Split('.', 2)[0].TrimEnd(' ').ToUpperInvariant();
            return name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) &&
                 "0123456789¹²³".Contains(name[3]));
        }

        internal void WriteIndex(FileStream index, CancellationToken ct)
        {
            _main.CopyTo(index, _buffer, ct);
            foreach (string id in _fileIds!)
                _descriptions[id].Packet.CopyTo(index, _buffer, ct);
        }

        private readonly record struct PacketLocation(FileStream Stream, long Offset, int Length)
        {
            internal void CopyTo(FileStream destination, byte[] buffer, CancellationToken ct)
            {
                Stream.Position = Offset;
                for (int remaining = Length; remaining > 0;)
                {
                    ct.ThrowIfCancellationRequested();
                    int count = Math.Min(remaining, buffer.Length);
                    Stream.ReadExactly(buffer.AsSpan(0, count));
                    destination.Write(buffer, 0, count);
                    remaining -= count;
                }
            }
        }
    }
}
