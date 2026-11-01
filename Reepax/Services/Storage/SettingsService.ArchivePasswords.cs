using System;
using System.Collections.Generic;
using System.Linq;
using Reepax.Models;

namespace Reepax.Services.Storage;

public partial class SettingsService
{
    internal event EventHandler? ArchivePasswordsChanged;
    private long _archivePasswordListRevision;
    internal long ArchivePasswordListRevision { get { lock (_fileLock) return _archivePasswordListRevision; } }

    internal (ArchivePasswordEntry[] Entries, long Revision) GetArchivePasswordSnapshot()
    {
        lock (_fileLock)
        {
            ArchivePasswordEntry.NormalizeSettings(Settings);
            return (Settings.ArchivePasswordStatistics.Select(entry => entry.Snapshot()).ToArray(), _archivePasswordListRevision);
        }
    }

    internal ArchivePasswordEntry[] GetArchivePasswordEntries()
    {
        lock (_fileLock)
        {
            ArchivePasswordEntry.NormalizeSettings(Settings);
            return Settings.ArchivePasswordStatistics.Select(entry => entry.Snapshot()).ToArray();
        }
    }

    internal bool ContainsArchivePassword(string password)
    {
        lock (_fileLock)
            return Settings.ExtractionPasswords?.Contains(password, StringComparer.Ordinal) == true;
    }

    internal void ReplaceArchivePasswords(IEnumerable<string> passwords)
    {
        lock (_fileLock)
        {
            ArchivePasswordEntry.NormalizeSettings(Settings);
            var replacement = AppSettings.SanitizeExtractionPasswords(passwords);
            if (Settings.ExtractionPasswords.SequenceEqual(replacement, StringComparer.Ordinal)) return;
            Settings.ExtractionPasswords = replacement;
            ArchivePasswordEntry.NormalizeSettings(Settings);
            _archivePasswordListRevision++;
        }
        NotifyArchivePasswordsChanged();
    }

    internal bool RememberArchivePassword(string password)
    {
        if (string.IsNullOrEmpty(password)) return false;
        lock (_fileLock)
        {
            ArchivePasswordEntry.NormalizeSettings(Settings);
            if (Settings.ExtractionPasswords.Contains(password, StringComparer.Ordinal)) return false;
            Settings.ExtractionPasswords.Add(password);
            Settings.ArchivePasswordStatistics.Add(new ArchivePasswordEntry { Password = password, AddedAt = DateTimeOffset.UtcNow });
            _archivePasswordListRevision++;
        }
        NotifyArchivePasswordsChanged();
        return true;
    }

    internal void RemoveArchivePassword(string password)
    {
        lock (_fileLock)
        {
            ArchivePasswordEntry.NormalizeSettings(Settings);
            if (!Settings.ExtractionPasswords.Remove(password)) return;
            ArchivePasswordEntry.NormalizeSettings(Settings);
            _archivePasswordListRevision++;
        }
        NotifyArchivePasswordsChanged();
    }

    internal ArchivePasswordEntry? RecordArchivePasswordAttempt(string password)
    {
        ArchivePasswordEntry? entry;
        lock (_fileLock)
        {
            ArchivePasswordEntry.NormalizeSettings(Settings);
            entry = Settings.ArchivePasswordStatistics.FirstOrDefault(candidate => candidate.Password == password);
            if (entry == null) return null;
            if (entry.UsageCount < long.MaxValue) entry.UsageCount++;
        }
        NotifyArchivePasswordsChanged();
        return entry;
    }

    internal void RecordArchivePasswordSuccess(ArchivePasswordEntry entry)
    {
        lock (_fileLock)
        {
            ArchivePasswordEntry.NormalizeSettings(Settings);
            if (!Settings.ArchivePasswordStatistics.Contains(entry) || entry.SuccessCount >= entry.UsageCount) return;
            entry.SuccessCount++;
            entry.LastSuccessAt = DateTimeOffset.UtcNow;
        }
        NotifyArchivePasswordsChanged();
    }

    private void NotifyArchivePasswordsChanged()
    {
        RequestSaveSettings();
        ArchivePasswordsChanged?.Invoke(this, EventArgs.Empty);
    }
}
