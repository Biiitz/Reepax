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

    internal sealed partial class RepairBackupScope : IDisposable
    {
        private readonly List<(Entry Entry, SafeFileHandle Handle)> _candidates = new();

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
                    if (HasReparsePoint(original))
                        throw new IOException("A linked PAR2 target cannot be tracked.");
                    if (IsAbsent(original))
                        continue;
                    string backup = GetBackupPath(original);
                    var handle = File.OpenHandle(original, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    try
                    {
                        var identity = ReadIdentity(handle) ?? throw new IOException("A PAR2 target identity could not be read.");
                        scope._candidates.Add((new(original, backup, identity), handle));
                    }
                    catch
                    {
                        handle.Dispose();
                        throw;
                    }
                }
            }
            catch
            {
                scope.Dispose();
                throw;
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

        public void Dispose() => CloseTargetHandles();

        private void CloseTargetHandles()
        {
            foreach (var candidate in _candidates)
                candidate.Handle.Dispose();
            _candidates.Clear();
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
