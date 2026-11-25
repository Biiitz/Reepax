using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Reepax.Services.Storage;

namespace Reepax.Services.Verification;

public partial class Par2RepairService
{
    internal static Action? RepairDecisionSaved { get; set; }

    internal static void RecoverInterruptedRepairs()
    {
        try { RepairBackupScope.RecoverPending(); }
        catch (Exception ex) { AppLogger.Warn("[Par2RepairService] Could not scan for interrupted repairs", ex); }
    }

    internal sealed partial class RepairBackupScope
    {
        private enum RecoveryDecision { Restore, Clean, Keep }
        private sealed record Identity(ulong Volume, ulong Low, ulong High, long Length, long Created, long Written);
        private sealed record Entry(string Original, string Backup, Identity Identity, Identity? Replacement = null);
        private sealed record Journal(Guid Id, string Directory, Entry[] Entries, int ProcessId = 0, long ProcessStart = 0,
            RecoveryDecision Decision = RecoveryDecision.Restore);

        private static string GetJournalDirectory(string? repairDirectory) =>
            DownloadPersistenceService.IsTestEnvironment && SettingsService.AppDataDirectoryOverride == null && repairDirectory != null
                ? Path.Combine(repairDirectory, ".par2-recovery")
                : Path.Combine(SettingsService.AppDataDirectory, "Par2Recovery");
        private Journal? _journal;
        private string? _journalPath;

        internal void PrepareRecovery(string directory)
        {
            if (_candidates.Count == 0) return;
            string journalDirectory = GetJournalDirectory(directory);
            if (HasReparsePoint(journalDirectory)) throw new IOException("The PAR2 recovery directory is linked.");
            Directory.CreateDirectory(journalDirectory);
            var journal = new Journal(Guid.NewGuid(), directory, _candidates.Select(candidate => candidate.Entry).ToArray());
            string path = Path.Combine(journalDirectory, journal.Id.ToString("N") + ".journal");
            Save(path, journal, overwrite: false);
            _journalPath = path;
            _journal = journal;
        }

        internal void SetRecoveryWriter(Process process)
        {
            if (_journal == null) return;
            var journal = _journal with { ProcessId = process.Id, ProcessStart = process.StartTime.ToFileTimeUtc() };
            Save(_journalPath!, journal, overwrite: true);
            _journal = journal;
        }

        internal void FinishRecovery(bool success, bool purgeBackups)
        {
            if (_journal == null) return;
            CloseTargetHandles();
            if (success)
            {
                var journal = _journal with
                {
                    Decision = purgeBackups ? RecoveryDecision.Clean : RecoveryDecision.Keep,
                    Entries = purgeBackups ? _journal.Entries.Select(entry =>
                        entry with { Replacement = ReadReplacement(entry.Original) }).ToArray() : _journal.Entries
                };
                Save(_journalPath!, journal, overwrite: true);
                _journal = journal;
                RepairDecisionSaved?.Invoke();
            }
            try
            {
                Apply(_journal);
                File.Delete(_journalPath!);
                _journal = null;
            }
            catch (Exception ex) when (success && (ex is IOException or UnauthorizedAccessException))
            {
                AppLogger.Warn("[Par2RepairService] Completed repair cleanup is still pending", ex);
            }
        }

        internal static void RecoverPending(string? repairDirectory = null)
        {
            string journalDirectory = GetJournalDirectory(repairDirectory);
            if (!Directory.Exists(journalDirectory) || HasReparsePoint(journalDirectory)) return;
            foreach (string path in Directory.EnumerateFiles(journalDirectory, "*.journal"))
            {
                bool matchesDirectory = false;
                try
                {
                    if (HasReparsePoint(path)) continue;
                    string content = File.ReadAllText(path);
                    if (!SecureAppDataStorage.IsEncrypted(content)) continue;
                    var journal = JsonSerializer.Deserialize<Journal>(SecureAppDataStorage.DecryptString(content));
                    if (journal == null || journal.Id.ToString("N") != Path.GetFileNameWithoutExtension(path))
                        continue;
                    matchesDirectory = repairDirectory != null &&
                        string.Equals(journal.Directory, repairDirectory, StringComparison.OrdinalIgnoreCase);
                    if (repairDirectory != null && !matchesDirectory) continue;
                    WaitForWriter(journal);
                    Apply(journal);
                    File.Delete(path);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("[Par2RepairService] Interrupted PAR2 recovery is still pending", ex);
                    if (matchesDirectory) throw;
                }
            }
        }

        private static void WaitForWriter(Journal journal)
        {
            if (journal.ProcessId == 0) return;
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(journal.ProcessId);
                if (process.StartTime.ToFileTimeUtc() == journal.ProcessStart && !process.WaitForExit(3000))
                    throw new IOException("The interrupted PAR2 writer is still active.");
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }

        private static void Apply(Journal journal)
        {
            if (journal.Decision == RecoveryDecision.Keep) return;
            if (!Directory.Exists(journal.Directory) || HasReparsePoint(journal.Directory))
                throw new IOException("The PAR2 repair directory is unavailable.");
            for (int index = 0; index < journal.Entries.Length; index++)
            {
                var entry = journal.Entries[index];
                if (HasReparsePoint(entry.Original) || HasReparsePoint(entry.Backup))
                    throw new IOException("A PAR2 recovery path is linked.");
                if (IsAbsent(entry.Backup))
                {
                    if (journal.Decision == RecoveryDecision.Clean || Matches(entry.Original, entry.Identity)) continue;
                    throw new IOException("A PAR2 backup could not be located.");
                }

                using var backup = RelocationFileAccess.OpenSource(entry.Backup);
                if (ReadIdentity(backup.Stream.SafeFileHandle) != entry.Identity)
                {
                    if (journal.Decision == RecoveryDecision.Clean || Matches(entry.Original, entry.Identity)) continue;
                    throw new IOException("A PAR2 backup was replaced.");
                }
                if (journal.Decision == RecoveryDecision.Clean)
                {
                    using var replacement = File.OpenHandle(entry.Original, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (entry.Replacement == null || ReadIdentity(replacement) != entry.Replacement || !backup.TryDelete())
                        throw new IOException("A PAR2 backup could not be cleaned.");
                    continue;
                }

                if (!backup.CanDelete) throw new IOException("A PAR2 backup could not be restored.");
                if (Matches(entry.Original, entry.Identity)) continue;
                if (!IsAbsent(entry.Original))
                {
                    // PAR2 may have left a partial result. Preserve any occupant before restoring the verified backup.
                    string suffix = ".interrupted-" + journal.Id.ToString("N");
                    string displaced = Path.GetFileName(entry.Backup).Length + suffix.Length <= 255
                        ? entry.Backup + suffix
                        : Path.Combine(Path.GetDirectoryName(entry.Backup)!, index.ToString(System.Globalization.CultureInfo.InvariantCulture) + suffix);
                    using var replacement = RelocationFileAccess.OpenSource(entry.Original);
                    replacement.MoveTo(displaced);
                }
                backup.MoveTo(entry.Original);
            }
        }

        private static bool IsAbsent(string path)
        {
            try { File.GetAttributes(path); return false; }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
        }

        private static bool Matches(string path, Identity identity)
        {
            if (IsAbsent(path)) return false;
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return ReadIdentity(handle) == identity;
        }

        private static Identity? ReadReplacement(string path)
        {
            try
            {
                if (IsAbsent(path) || HasReparsePoint(path)) return null;
                using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return ReadIdentity(handle);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }

        private static Identity? ReadIdentity(SafeFileHandle handle) => TryGetIdentity(handle, out var id)
            ? new(id.Volume, id.Low, id.High, RandomAccess.GetLength(handle),
                File.GetCreationTimeUtc(handle).ToFileTimeUtc(), File.GetLastWriteTimeUtc(handle).ToFileTimeUtc()) : null;

        private static void Save(string path, Journal journal, bool overwrite)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            bool ownsTemporary = false;
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(SecureAppDataStorage.EncryptString(JsonSerializer.Serialize(journal)));
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    ownsTemporary = true;
                    file.Write(data);
                    file.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite);
                ownsTemporary = false;
            }
            finally
            {
                if (ownsTemporary) File.Delete(temporary);
            }
        }
    }
}
