using System;
using System.Collections.Generic;
using System.Linq;
using Reepax.Models;

namespace Reepax.Services.Storage;

public partial class SettingsService
{
    internal event EventHandler? ArchivePasswordsChanged;
    private long _archivePasswordListRevision;
    private List<string>? _indexedArchivePasswords;
    private List<ArchivePasswordEntry>? _indexedArchiveStatistics;
    private readonly Dictionary<string, ArchivePasswordSlot> _archivePasswordIndex = new(StringComparer.Ordinal);
    private readonly record struct ArchivePasswordSlot(int Index, ArchivePasswordEntry Entry);
    internal long ArchivePasswordListRevision { get { lock (_fileLock) return _archivePasswordListRevision; } }

    internal (ArchivePasswordEntry[] Entries, long Revision) GetArchivePasswordSnapshot()
    {
        lock (_fileLock)
        {
            EnsureArchivePasswordIndex(validateEntries: true);
            return (Settings.ArchivePasswordStatistics.Select(entry => entry.Snapshot()).ToArray(), _archivePasswordListRevision);
        }
    }

    internal ArchivePasswordEntry[] GetArchivePasswordEntries()
    {
        lock (_fileLock)
        {
            EnsureArchivePasswordIndex(validateEntries: true);
            return Settings.ArchivePasswordStatistics.Select(entry => entry.Snapshot()).ToArray();
        }
    }

    internal bool ContainsArchivePassword(string password)
    {
        lock (_fileLock)
            return FindArchivePasswordEntry(password) != null;
    }

    internal void ReplaceArchivePasswords(IEnumerable<string> passwords)
    {
        lock (_fileLock)
        {
            EnsureArchivePasswordIndex(validateEntries: true);
            var replacement = AppSettings.SanitizeExtractionPasswords(passwords);
            if (Settings.ExtractionPasswords.SequenceEqual(replacement, StringComparer.Ordinal)) return;
            Settings.ExtractionPasswords = replacement;
            EnsureArchivePasswordIndex(validateEntries: true);
            _archivePasswordListRevision++;
        }
        NotifyArchivePasswordsChanged();
    }

    internal bool RememberArchivePassword(string password)
    {
        if (string.IsNullOrEmpty(password)) return false;
        lock (_fileLock)
        {
            EnsureArchivePasswordIndex(validateEntries: true);
            if (_archivePasswordIndex.ContainsKey(password)) return false;
            var entry = new ArchivePasswordEntry { Password = password, AddedAt = DateTimeOffset.UtcNow };
            int index = Settings.ExtractionPasswords.Count;
            Settings.ExtractionPasswords.Add(password);
            Settings.ArchivePasswordStatistics.Add(entry);
            _archivePasswordIndex.Add(password, new ArchivePasswordSlot(index, entry));
            _archivePasswordListRevision++;
        }
        NotifyArchivePasswordsChanged();
        return true;
    }

    internal void RemoveArchivePassword(string password)
    {
        lock (_fileLock)
        {
            EnsureArchivePasswordIndex(validateEntries: true);
            if (!Settings.ExtractionPasswords.Remove(password)) return;
            EnsureArchivePasswordIndex(validateEntries: true);
            _archivePasswordListRevision++;
        }
        NotifyArchivePasswordsChanged();
    }

    internal ArchivePasswordEntry? RecordArchivePasswordAttempt(string password)
    {
        ArchivePasswordEntry? entry;
        lock (_fileLock)
        {
            entry = FindArchivePasswordEntry(password);
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
            if (!ReferenceEquals(FindArchivePasswordEntry(entry.Password), entry) || entry.SuccessCount >= entry.UsageCount) return;
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

    private ArchivePasswordEntry? FindArchivePasswordEntry(string password)
    {
        if (string.IsNullOrEmpty(password)) return null;
        EnsureArchivePasswordIndex();
        bool indexed = _archivePasswordIndex.TryGetValue(password, out var slot);
        if (indexed && IsCurrentArchivePasswordSlot(password, slot)) return slot.Entry;
        if (!indexed && !Settings.ExtractionPasswords.Contains(password, StringComparer.Ordinal)) return null;
        EnsureArchivePasswordIndex(validateEntries: true);
        return _archivePasswordIndex.TryGetValue(password, out slot) ? slot.Entry : null;
    }

    private bool IsCurrentArchivePasswordSlot(string password, ArchivePasswordSlot slot) =>
        slot.Index < Settings.ExtractionPasswords.Count && slot.Index < Settings.ArchivePasswordStatistics.Count &&
        string.Equals(Settings.ExtractionPasswords[slot.Index], password, StringComparison.Ordinal) &&
        ReferenceEquals(Settings.ArchivePasswordStatistics[slot.Index], slot.Entry) &&
        string.Equals(slot.Entry.Password, password, StringComparison.Ordinal) &&
        slot.Entry.UsageCount >= 0 && slot.Entry.SuccessCount >= 0 && slot.Entry.SuccessCount <= slot.Entry.UsageCount;

    private void EnsureArchivePasswordIndex(bool validateEntries = false)
    {
        bool current = _indexedArchivePasswords != null && _indexedArchiveStatistics != null &&
            ReferenceEquals(_indexedArchivePasswords, Settings.ExtractionPasswords) &&
            ReferenceEquals(_indexedArchiveStatistics, Settings.ArchivePasswordStatistics) &&
            _indexedArchivePasswords.Count == _archivePasswordIndex.Count && _indexedArchiveStatistics.Count == _archivePasswordIndex.Count;
        if (current && validateEntries)
        {
            for (int index = 0; index < Settings.ExtractionPasswords.Count; index++)
            {
                string password = Settings.ExtractionPasswords[index];
                if (string.IsNullOrEmpty(password) || !_archivePasswordIndex.TryGetValue(password, out var slot) ||
                    slot.Index != index || !IsCurrentArchivePasswordSlot(password, slot))
                {
                    current = false;
                    break;
                }
            }
        }
        if (current) return;
        // Recheck cached slots after in-place edits without scanning unchanged lists for each attempt.
        ArchivePasswordEntry.NormalizeSettings(Settings);
        _archivePasswordIndex.Clear();
        for (int index = 0; index < Settings.ExtractionPasswords.Count; index++)
            _archivePasswordIndex.Add(Settings.ExtractionPasswords[index], new ArchivePasswordSlot(index, Settings.ArchivePasswordStatistics[index]));
        _indexedArchivePasswords = Settings.ExtractionPasswords;
        _indexedArchiveStatistics = Settings.ArchivePasswordStatistics;
    }
}
