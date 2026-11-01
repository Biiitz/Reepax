using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Reepax.Models;

internal enum ArchivePasswordOutcome { NotTried, NotConfirmed, Accepted, Verified }

internal sealed class ArchivePasswordTrial
{
    internal ArchivePasswordOutcome Outcome { get; set; }
    internal ArchivePasswordEntry? Counter { get; set; }
}

internal sealed class ArchivePasswordCheck
{
    internal string SourceStamp { get; set; } = string.Empty;
    internal string DisplayName { get; set; } = string.Empty;
    internal bool IsEncrypted { get; set; }
    internal bool Inspected { get; set; }
    internal string? Password { get; set; }
    internal Exception? Failure { get; set; }
    internal Dictionary<string, ArchivePasswordTrial> Trials { get; } = new(StringComparer.Ordinal);
}

internal sealed class PackageArchivePasswordState
{
    internal object Sync { get; } = new();
    internal Dictionary<string, ArchivePasswordCheck> Archives { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, Dictionary<string, bool>> ManualCandidates { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal long Revision { get; set; }
    internal long CheckedRevision { get; set; } = -1;
    internal long SettingsRevision { get; set; } = -1;
    internal bool ManualRequest { get; set; }
    internal bool Cancelled { get; set; }
    internal bool Ready { get; set; }
    internal string? LastInputArchive { get; set; }
    internal string? LastInputPassword { get; set; }
}

public partial class DownloadPackage
{
    internal PackageArchivePasswordState ArchivePasswordState { get; } = new();
    private bool _requiresArchivePassword;
    private long _archivePasswordStateRevision;

    [JsonIgnore]
    public bool RequiresArchivePassword
    {
        get => _requiresArchivePassword;
        internal set
        {
            if (SetProperty(ref _requiresArchivePassword, value))
                OnPropertyChanged(nameof(IsFullyCompleted));
        }
    }

    [JsonIgnore]
    public long ArchivePasswordStateRevision => _archivePasswordStateRevision;

    internal void NotifyArchivePasswordStateChanged()
    {
        _archivePasswordStateRevision++;
        OnPropertyChanged(nameof(ArchivePasswordStateRevision));
    }

    partial void OnAutoExtractArchivesChanged(bool value)
    {
        MarkDirty();
        if (value)
        {
            lock (ArchivePasswordState.Sync)
            {
                ArchivePasswordState.Cancelled = false;
                ArchivePasswordState.Revision++;
            }
            return;
        }
        lock (ArchivePasswordState.Sync)
        {
            if (ArchivePasswordState.ManualRequest) return;
        }
        RequiresArchivePassword = false;
    }
}
