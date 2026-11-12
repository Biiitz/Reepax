using System.IO;
using System.Runtime.InteropServices;
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

        internal static RepairBackupScope Capture(string par2FilePath, IReadOnlyCollection<string>? authorizedTargets = null)
        {
            var scope = new RepairBackupScope();
            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(par2FilePath))!;
                foreach (var original in authorizedTargets ?? ReadRepairTargets(par2FilePath, directory))
                {
                    if (!File.Exists(original) || HasReparsePoint(original))
                        continue;
                    string backup = GetBackupPath(original);
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
                AppLogger.Warn($"[Par2RepairService] Backup tracking unavailable", ex);
            }
            return scope;
        }

        internal static string GetBackupPath(string original)
        {
            var index = 1;
            string backup;
            do
            {
                backup = original + "." + (index++).ToString(System.Globalization.CultureInfo.InvariantCulture);
            } while (Path.Exists(backup));
            return backup;
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
                    AppLogger.Warn($"[Par2RepairService] Backup handling failed", ex);
                }
            }
        }

        private static string[] ReadRepairTargets(string par2FilePath, string directory)
        {
            using var stream = File.OpenRead(par2FilePath);
            var metadata = new RepairMetadata();
            metadata.Read(stream, CancellationToken.None);
            return metadata.IsComplete ? metadata.GetTargets(directory) : [];
        }
    }
}
