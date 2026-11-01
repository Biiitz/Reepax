using System;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using Reepax.Services.Localization;

namespace Reepax.Models;

public sealed record ArchiveCleanupFile(string Path, Guid? ItemId, string Suffix, string? RelativePath,
    long Length, long LastWriteTicks, bool Recycle);

public sealed record ArchiveCleanupState(bool RecycleArchives, ArchiveCleanupFile[] Files);

public partial class DownloadPackage
{
    private ArchiveCleanupState? _pendingArchiveCleanup;

    [JsonIgnore]
    public ArchiveCleanupState? PendingArchiveCleanup
    {
        get => _pendingArchiveCleanup;
        internal set
        {
            if (!SetProperty(ref _pendingArchiveCleanup, value)) return;
            MarkDirty();
            OnPropertyChanged(nameof(HasPendingArchiveCleanup));
            OnPropertyChanged(nameof(IsFullyCompleted));
        }
    }

    private NextTaskStep CreatePendingCleanupStep(Dictionary<string, NextTaskStepState> states) => new()
    {
        Key = "Cleanup",
        Name = Loc.Get(PendingArchiveCleanup!.RecycleArchives ? "NextTask_MoveToRecycleBin" : "NextTask_DeleteArchive"),
        State = states.TryGetValue("Cleanup", out var state) ? state : NextTaskStepState.Failed
    };

    [JsonIgnore]
    public bool HasPendingArchiveCleanup => PendingArchiveCleanup?.Files is { Length: > 0 };
}
