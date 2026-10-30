using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Reepax.Services.Storage;

namespace Reepax.Services.Verification;

public partial class Par2RepairService
{
    private readonly Dictionary<string, RepairDirectoryLease> _repairDirectories = new(StringComparer.OrdinalIgnoreCase);

    private async Task<IDisposable> EnterRepairDirectoryAsync(string directory, CancellationToken ct)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        RepairDirectoryLease entry;
        lock (_repairDirectories)
        {
            if (!_repairDirectories.TryGetValue(directory, out entry!))
                _repairDirectories.Add(directory, entry = new RepairDirectoryLease(this, directory));
            entry.Users++;
        }
        try
        {
            await entry.Gate.WaitAsync(ct);
            return entry;
        }
        catch
        {
            entry.ReleaseUser();
            throw;
        }
    }

    private sealed class RepairDirectoryLease(Par2RepairService owner, string directory) : IDisposable
    {
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal int Users { get; set; }

        public void Dispose()
        {
            Gate.Release();
            ReleaseUser();
        }

        internal void ReleaseUser()
        {
            lock (owner._repairDirectories)
            {
                if (--Users == 0)
                {
                    owner._repairDirectories.Remove(directory);
                    Gate.Dispose();
                }
            }
        }
    }

    internal sealed class RepairBackupScope : IDisposable
    {
        private readonly List<(string Original, string Backup, SafeFileHandle Handle, FileIdentity Identity)> _candidates = new();

        [StructLayout(LayoutKind.Sequential)]
        private struct FileIdentity
        {
            public ulong Volume;
            public ulong Low;
            public ulong High;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
            out FileIdentity identity, uint bufferSize);

        private static bool TryGetIdentity(SafeFileHandle file, out FileIdentity identity)
            => GetFileInformationByHandleEx(file, 18, out identity, (uint)Marshal.SizeOf<FileIdentity>());

        internal static RepairBackupScope Capture(string par2FilePath)
        {
            var scope = new RepairBackupScope();
            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(par2FilePath))!;
                foreach (var original in ReadRepairTargets(par2FilePath, directory))
                {
                    if (!File.Exists(original) || HasReparsePoint(original))
                        continue;
                    var index = 1;
                    string backup;
                    do
                    {
                        backup = original + "." + (index++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    } while (Path.Exists(backup));
                    var handle = File.OpenHandle(original, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (TryGetIdentity(handle, out var identity))
                        scope._candidates.Add((original, backup, handle, identity));
                    else
                        handle.Dispose();
                }
            }
            catch (Exception ex)
            {
                scope.Dispose();
                AppLogger.Warn($"[Par2RepairService] Backup tracking unavailable: {ex.Message}");
            }
            return scope;
        }

        internal void Restore() => Process(restore: true);

        internal void Clean() => Process(restore: false);

        public void Dispose()
        {
            foreach (var candidate in _candidates)
                candidate.Handle.Dispose();
            _candidates.Clear();
        }

        private void Process(bool restore)
        {
            foreach (var (original, backup, _, identity) in _candidates)
            {
                try
                {
                    if (!File.Exists(backup) || HasReparsePoint(backup) || HasReparsePoint(original))
                        continue;
                    using var handle = File.OpenHandle(backup, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (!TryGetIdentity(handle, out var backupIdentity) || !identity.Equals(backupIdentity))
                        continue;
                    if (restore)
                        File.Move(backup, original, overwrite: true);
                    else if (File.Exists(original))
                        Extractor.ArchiveExtractionService.DeleteOrMoveToTemp(backup);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"[Par2RepairService] Backup handling failed: {ex.Message}");
                }
            }
        }

        private static bool HasReparsePoint(string path)
        {
            for (string? current = path; current != null; current = Path.GetDirectoryName(current))
            {
                if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return true;
            }
            return false;
        }

        private static IEnumerable<string> ReadRepairTargets(string par2FilePath, string directory)
        {
            using var stream = File.OpenRead(par2FilePath);
            var header = new byte[64];
            var descriptions = new Dictionary<(string Set, string Id), string>();
            var recoverable = new HashSet<(string Set, string Id)>();
            var sets = new HashSet<string>();
            while (stream.Position < stream.Length)
            {
                stream.ReadExactly(header);
                if (!header.AsSpan(0, 8).SequenceEqual("PAR2\0PKT"u8))
                    return Array.Empty<string>();
                var length = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8, 8));
                if (length < 64 || length % 4 != 0 || length > (ulong)(stream.Length - stream.Position) + 64)
                    return Array.Empty<string>();
                var type = header.AsSpan(48, 16);
                var isMain = type.SequenceEqual("PAR 2.0\0Main\0\0\0\0"u8);
                var isDescription = type.SequenceEqual("PAR 2.0\0FileDesc"u8);
                if (!isMain && !isDescription)
                {
                    stream.Seek((long)length - 64, SeekOrigin.Current);
                    continue;
                }
                // Unknown or excessive metadata never authorizes backup removal.
                if (length > 16 * 1024 * 1024)
                    return Array.Empty<string>();
                var body = new byte[(int)length - 64];
                stream.ReadExactly(body);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                hash.AppendData(header, 32, 32);
                hash.AppendData(body);
                if (!hash.GetHashAndReset().AsSpan().SequenceEqual(header.AsSpan(16, 16)))
                    continue;
                var set = Convert.ToHexString(header.AsSpan(32, 16));
                if (isMain)
                {
                    if (body.Length < 12 || (body.Length - 12) % 16 != 0)
                        return Array.Empty<string>();
                    var count = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8, 4));
                    if (count > (body.Length - 12) / 16)
                        return Array.Empty<string>();
                    sets.Add(set);
                    for (var i = 0; i < count; i++)
                        recoverable.Add((set, Convert.ToHexString(body.AsSpan(12 + i * 16, 16))));
                }
                else if (body.Length > 56)
                {
                    var name = new System.Text.UTF8Encoding(false, true).GetString(body, 56, body.Length - 56).TrimEnd('\0');
                    var id = (set, Convert.ToHexString(body.AsSpan(0, 16)));
                    if (descriptions.TryGetValue(id, out var previous) && previous != name)
                        return Array.Empty<string>();
                    descriptions[id] = name;
                }
            }
            if (sets.Count != 1)
                return Array.Empty<string>();

            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in recoverable)
            {
                if (!descriptions.TryGetValue(id, out var name))
                    continue;
                var parts = name.Replace('\\', '/').Split('/');
                if (Path.IsPathRooted(name) || parts.Any(part => string.IsNullOrWhiteSpace(part) ||
                    part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                    part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                    continue;
                var path = Path.GetFullPath(Path.Combine(directory, name));
                var prefix = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    targets.Add(path);
            }
            return targets;
        }
    }
}
