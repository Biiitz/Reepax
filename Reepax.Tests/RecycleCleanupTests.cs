using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Reepax.Converters;
using Reepax.Models;
using Reepax.Services.Extractor;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

[Collection("SharedQueue")]
public sealed class RecycleCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RecycleCase_" + Guid.NewGuid().ToString("N"));
    private readonly MainViewModel _model;
    private readonly List<(string Source, string Destination)> _moved = new();

    public RecycleCleanupTests()
    {
        Directory.CreateDirectory(_root);
        _model = new MainViewModel();
        _model.Packages.Clear();
        _model.RecyclePaths = MoveToTestBin;
        _model.CleanupConfirmationOverride = _ => (true, true, true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Package_RecyclesTrackedFilesWithoutDiscardingPartialOrEmptyFiles(bool nestedExtraction)
    {
        var package = AddPackage("TargetFolder");
        var item = AddFile(package, "data.bin");
        File.WriteAllText(item.SaveFilePath + ".part", "partial");
        File.WriteAllText(item.SaveFilePath + ".part.segments", "segments");
        var empty = AddFile(package, "empty.bin", "");
        Directory.CreateDirectory(Path.Combine(package.SaveDirectory, "nested"));
        var nested = Path.Combine(package.SaveDirectory, "nested", "extracted.bin");
        File.WriteAllText(nested, "nested");
        package.ExtractionDirectory = nestedExtraction
            ? Path.Combine(package.SaveDirectory, "TargetFolder")
            : Path.Combine(_root, "extracted", "TargetFolder");
        Directory.CreateDirectory(package.ExtractionDirectory);
        var result = Path.Combine(package.ExtractionDirectory, "result.bin");
        File.WriteAllText(result, "result");
        package.RecordExtractedFiles([ExtractedPackageFile.Capture(nested), ExtractedPackageFile.Capture(result)]);

        await _model.RemovePackage(package);

        Assert.DoesNotContain(package, _model.Packages);
        Assert.Equal(6, _moved.Count);
        Assert.Equal("content", File.ReadAllText(RecycledFile(item.SaveFilePath!)));
        Assert.Equal("partial", File.ReadAllText(RecycledFile(item.SaveFilePath + ".part")));
        Assert.Equal("segments", File.ReadAllText(RecycledFile(item.SaveFilePath + ".part.segments")));
        Assert.Empty(File.ReadAllBytes(RecycledFile(empty.SaveFilePath!)));
        Assert.Equal("nested", File.ReadAllText(RecycledFile(nested)));
        Assert.Equal("result", File.ReadAllText(RecycledFile(result)));
        Assert.False(Directory.Exists(package.SaveDirectory));
        Assert.False(Directory.Exists(package.ExtractionDirectory));
    }

    [Fact]
    public async Task Package_EmptyDirectoryIsRemovedWithoutSendingItToTheRecycleBackend()
    {
        var package = AddPackage("TargetFolder");
        string? preview = null;
        _model.CleanupConfirmationOverride = message =>
        {
            preview = message;
            return (true, true, true);
        };

        await _model.RemovePackage(package);

        Assert.Equal(Loc.Get("Dialog_DeleteEmptyPackageDirectory"), preview);
        Assert.DoesNotContain(package, _model.Packages);
        Assert.False(Directory.Exists(package.SaveDirectory));
        Assert.Empty(_moved);
    }

    [Fact]
    public async Task Package_ConfirmationForEmptyDirectoryCannotAuthorizeNewFiles()
    {
        var package = AddPackage("TargetFolder");
        var path = Path.Combine(package.SaveDirectory, "data.bin");
        var item = new DownloadItem { FileName = "data.bin", SaveFilePath = path, Status = DownloadStatus.Paused };
        package.Items.Add(item);
        string? preview = null;
        _model.CleanupConfirmationOverride = message =>
        {
            preview = message;
            File.WriteAllText(path, "finished during confirmation");
            return (true, true, false);
        };

        await _model.RemovePackage(package);

        Assert.Equal(Loc.Get("Dialog_DeleteEmptyPackageDirectory"), preview);
        Assert.Equal(Loc.Get("Status_CleanupChanged"), _model.StatusSummary);
        Assert.Contains(package, _model.Packages);
        Assert.False(item.IsRemovalPending);
        Assert.Equal("finished during confirmation", File.ReadAllText(path));
        Assert.Empty(_moved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Package_InExistingMatchingDirectory_RemovesOnlyItsOwnFiles(bool recycle)
    {
        byte[] notes = [0x42, 0x17, 0x00];
        byte[] content = [1, 2, 3, 4];
        var directory = Path.Combine(_root, "Collection");
        Directory.CreateDirectory(directory);
        var notesPath = Path.Combine(directory, "notes.txt");
        File.WriteAllBytes(notesPath, notes);
        var tempPath = Path.Combine(directory, "data.bin.tmp");
        File.WriteAllBytes(tempPath, notes);
        var package = PackageGrouper.CreatePackage("Collection",
            [new ExtractedLink { RawFileName = "data.bin", Url = "https://files.example.invalid/data.bin" }], _root);
        package.IsCustomName = true;
        _model.Packages.Add(package);
        var item = Assert.Single(package.Items);
        Assert.Equal(directory, package.SaveDirectory);
        File.WriteAllBytes(item.SaveFilePath!, content);
        var other = AddFile(AddPackage("Other", "Other"), "data.bin", "other");

        string? preview = null;
        _model.CleanupConfirmationOverride = message =>
        {
            preview = message;
            return (true, true, recycle);
        };

        await _model.RemovePackage(package);

        Assert.True(File.Exists(notesPath));
        Assert.Equal(notes, File.ReadAllBytes(notesPath));
        Assert.Equal(notes, File.ReadAllBytes(tempPath));
        Assert.Equal("other", File.ReadAllText(other.SaveFilePath!));
        Assert.Equal(Loc.Format("Dialog_DeletePackageOptionSingleFile", BytesToHumanReadableConverter.FormatBytes(content.Length)), preview);
        Assert.DoesNotContain(package, _model.Packages);
        Assert.False(File.Exists(item.SaveFilePath));
        if (recycle)
        {
            var moved = Assert.Single(_moved);
            Assert.Equal(item.SaveFilePath, moved.Source);
            Assert.Equal(content, File.ReadAllBytes(moved.Destination));
        }
        else Assert.Empty(_moved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Package_DeletesPublishedOutputsAfterReloadAndRelocation(bool recycle)
    {
        byte[] result = [1, 2, 3, 4];
        byte[] replacement = [4, 3, 2, 1];
        var package = AddPackage("TargetFolder");
        package.IsCustomName = true;
        var archive = AddFile(package, "source.zip");
        File.Delete(archive.SaveFilePath!);
        using (var zip = ZipFile.Open(archive.SaveFilePath!, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "payload/result.bin", "edit.bin" })
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(result);
            }
        }
        archive.TotalBytes = archive.DownloadedBytes = new FileInfo(archive.SaveFilePath!).Length;
        archive.Status = DownloadStatus.Completed;

        var settings = SettingsService.Instance.Settings;
        var previousCleanup = (settings.DeleteArchiveAfterExtraction, settings.MoveArchiveToRecycleBin);
        try
        {
            settings.DeleteArchiveAfterExtraction = true;
            settings.MoveArchiveToRecycleBin = false;
            Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package, force: true));
        }
        finally
        {
            (settings.DeleteArchiveAfterExtraction, settings.MoveArchiveToRecycleBin) = previousCleanup;
        }
        Assert.False(File.Exists(archive.SaveFilePath));
        Assert.Equal(result, File.ReadAllBytes(Path.Combine(package.SaveDirectory, "payload", "result.bin")));

        using var persistence = new DownloadPersistenceService(Path.Combine(_root, "state", "downloads.json"));
        if (recycle) persistence.SaveHistory([package], sync: true);
        else persistence.SaveDownloads([package], sync: true);
        var restored = Assert.Single(recycle ? persistence.LoadHistory() : persistence.LoadDownloads());
        _model.Packages.Remove(package);
        _model.Packages.Add(restored);
        var originalDirectory = restored.SaveDirectory;
        if (recycle)
            Assert.True(await _model.ChangeDownloadPathsAsync([restored], Path.Combine(_root, "Moved")));
        else
            Assert.True(restored.Rename("Renamed"));
        Assert.NotEqual(originalDirectory, restored.SaveDirectory);
        Assert.False(Directory.Exists(originalDirectory));

        var ownedPath = Path.Combine(restored.SaveDirectory, "payload", "result.bin");
        var editedPath = Path.Combine(restored.SaveDirectory, "edit.bin");
        var legacyPath = Path.Combine(restored.SaveDirectory, "legacy.bin");
        var oldWrite = File.GetLastWriteTimeUtc(editedPath);
        File.WriteAllBytes(editedPath, replacement);
        File.SetLastWriteTimeUtc(editedPath, oldWrite.AddMinutes(1));
        File.WriteAllBytes(legacyPath, replacement);
        _model.CleanupConfirmationOverride = _ => (true, true, recycle);

        await _model.RemovePackage(restored);

        Assert.DoesNotContain(restored, _model.Packages);
        Assert.False(File.Exists(ownedPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(ownedPath)));
        Assert.Equal(replacement, File.ReadAllBytes(editedPath));
        Assert.Equal(replacement, File.ReadAllBytes(legacyPath));
        if (recycle)
        {
            Assert.Equal(ownedPath, Assert.Single(_moved).Source);
            Assert.Equal(result, File.ReadAllBytes(RecycledFile(ownedPath)));
        }
        else Assert.Empty(_moved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Package_PreservesFilesAddedAfterTheCleanupScan(bool recycle)
    {
        var package = AddPackage("TargetFolder");
        var item = AddFile(package, "data.bin");
        var notesPath = Path.Combine(package.SaveDirectory, "notes.txt");
        int diskOperations = 0;
        _model.DiskCleanupRunner = action =>
        {
            if (++diskOperations == 3) File.WriteAllText(notesPath, "added during cleanup");
            action();
            return Task.CompletedTask;
        };
        _model.CleanupConfirmationOverride = _ => (true, true, recycle);

        await _model.RemovePackage(package);

        Assert.Equal(3, diskOperations);
        Assert.DoesNotContain(package, _model.Packages);
        Assert.False(File.Exists(item.SaveFilePath));
        Assert.Equal("added during cleanup", File.ReadAllText(notesPath));
        if (recycle) Assert.Equal("content", File.ReadAllText(RecycledFile(item.SaveFilePath!)));
        else Assert.Empty(_moved);
    }

    [Fact]
    public async Task Package_PreservesOtherPackagesAndSharedFileReferences()
    {
        var package = AddPackage("TargetFolder");
        var owned = AddFile(package, "owned.bin");
        File.WriteAllText(owned.SaveFilePath + ".part", "partial");
        var shared = AddFile(package, "shared.bin", "shared");
        File.WriteAllText(shared.SaveFilePath + ".part", "shared partial");
        var other = AddPackage(Path.Combine("TargetFolder", "OtherFolder"), "OtherFolder");
        var otherItem = AddFile(other, "other.bin", "other");
        other.Items.Add(new DownloadItem { FileName = "shared.bin", SaveFilePath = shared.SaveFilePath });

        await _model.RemovePackage(package);

        Assert.DoesNotContain(package, _model.Packages);
        Assert.Contains(other, _model.Packages);
        Assert.Equal("other", File.ReadAllText(otherItem.SaveFilePath!));
        Assert.Equal("shared", File.ReadAllText(shared.SaveFilePath!));
        Assert.Equal("shared partial", File.ReadAllText(shared.SaveFilePath + ".part"));
        Assert.Equal(2, _moved.Count);
        Assert.All(_moved, move => Assert.StartsWith(owned.SaveFilePath!, move.Source));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Package_UnsuccessfulRecyclingKeepsEntryAndNeverDeletesPermanently(bool throws)
    {
        var package = AddPackage("TargetFolder");
        var item = AddFile(package, "data.bin");
        File.WriteAllText(item.SaveFilePath + ".part", "partial");
        _model.RecyclePaths = _ => { if (throws) throw new IOException("Recycling unavailable."); };

        await _model.RemovePackage(package);

        Assert.Contains(package, _model.Packages);
        Assert.Equal("content", File.ReadAllText(item.SaveFilePath!));
        Assert.Equal("partial", File.ReadAllText(item.SaveFilePath + ".part"));
        Assert.Equal(Loc.Get("Status_RecycleFailed"), _model.StatusSummary);
        Assert.False(item.IsRemovalPending);
        Assert.False(_model.IsRemovalInProgress);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Package_ListOnlyOrCancelledRemovalDoesNotTouchFiles(bool confirmed, bool deleteFiles)
    {
        var package = AddPackage("TargetFolder");
        var item = AddFile(package, "data.bin");
        _model.CleanupConfirmationOverride = _ => (confirmed, deleteFiles, true);
        _model.RecyclePaths = _ => throw new InvalidOperationException("File operation was not expected.");

        await _model.RemovePackage(package);

        Assert.Equal(!confirmed, _model.Packages.Contains(package));
        Assert.Equal("content", File.ReadAllText(item.SaveFilePath!));
        Assert.Empty(_moved);
    }

    [Fact]
    public async Task Item_RecyclesReservedSidecarsAndPreservesOtherFiles()
    {
        var package = AddPackage("TargetFolder");
        var item = AddFile(package, "data.bin");
        var other = AddFile(package, "other.bin", "other");
        var ownedSuffixes = new[] { ".part", ".part.segments" };
        var unknownSuffixes = new[] { ".segments", ".tmp", ".temp", ".reepax_tmp" };
        foreach (var suffix in ownedSuffixes.Concat(unknownSuffixes))
            File.WriteAllText(item.SaveFilePath + suffix, suffix);

        await _model.RemoveItem(item);

        Assert.Contains(package, _model.Packages);
        Assert.DoesNotContain(item, package.Items);
        Assert.Equal("other", File.ReadAllText(other.SaveFilePath!));
        Assert.Equal(3, _moved.Count);
        Assert.Equal("content", File.ReadAllText(RecycledFile(item.SaveFilePath!)));
        foreach (var suffix in ownedSuffixes)
            Assert.Equal(suffix, File.ReadAllText(RecycledFile(item.SaveFilePath + suffix)));
        foreach (var suffix in unknownSuffixes)
            Assert.Equal(suffix, File.ReadAllText(item.SaveFilePath + suffix));
    }

    [Fact]
    public async Task MultipleSelection_UsesTheSameRecycleChoiceForPackagesAndItems()
    {
        var package = AddPackage("TargetFolder");
        AddFile(package, "data.bin");
        package.IsSelected = true;
        var other = AddPackage("OtherFolder", "OtherFolder");
        var selected = AddFile(other, "selected.bin");
        selected.IsSelected = true;
        var kept = AddFile(other, "kept.bin", "kept");

        await _model.DeleteSelected();

        Assert.DoesNotContain(package, _model.Packages);
        Assert.Contains(other, _model.Packages);
        Assert.DoesNotContain(selected, other.Items);
        Assert.Equal("kept", File.ReadAllText(kept.SaveFilePath!));
        Assert.Equal(2, _moved.Count);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    public async Task FilteredSelection_ProtectsPreviouslySelectedHiddenPackagesAndFiles(
        bool confirmed, bool deleteFiles, bool recycleFiles)
    {
        var shown = AddPackage("Shown group", "Shown group");
        var shownFile = AddFile(shown, "sample.bin", "shown");
        var attached = AddPackage("Attached group", "Attached group");
        var attachedFile = AddFile(attached, "sample.bin", "attached");
        _model.ClipPackage(attached, shown);
        shown.IsExpanded = false;
        var hidden = AddPackage("Hidden group", "Hidden group");
        var hiddenFile = AddFile(hidden, "sample.bin", "hidden");
        var hiddenAttached = AddPackage("Hidden attachment", "Hidden attachment");
        var hiddenAttachedFile = AddFile(hiddenAttached, "sample.bin", "hidden attachment");
        _model.ClipPackage(hiddenAttached, hidden);

        _model.SelectPackageFromPointer(hidden, controlPressed: false, wasSelected: false);
        _model.SelectItemFromPointer(hiddenAttachedFile, controlPressed: true, wasSelected: false);
        _model.SearchFilterText = "Shown";
        Assert.Same(shown, Assert.Single(_model.RootPackages));
        _model.SelectAll();
        Assert.True(shown.IsSelected);
        Assert.False(shownFile.IsSelected);
        Assert.True(attached.IsSelected);
        Assert.True(attachedFile.IsSelected);
        Assert.True(hidden.IsSelected);
        Assert.True(hiddenAttachedFile.IsSelected);
        Assert.False(hiddenFile.IsSelected);
        Assert.False(hiddenAttached.IsSelected);

        int confirmations = 0;
        string? offeredOption = null;
        _model.CleanupConfirmationOverride = option =>
        {
            confirmations++;
            offeredOption = option;
            return (confirmed, deleteFiles, recycleFiles);
        };
        await _model.DeleteSelected();

        Assert.Equal(1, confirmations);
        Assert.Equal(Loc.Format("Dialog_DeleteMultipleOptionFiles",
            Reepax.Converters.BytesToHumanReadableConverter.FormatBytes(13)), offeredOption);
        Assert.Equal(!confirmed, _model.Packages.Contains(shown));
        Assert.Equal(!confirmed, _model.Packages.Contains(attached));
        if (confirmed && deleteFiles)
        {
            Assert.False(File.Exists(shownFile.SaveFilePath));
            Assert.False(File.Exists(attachedFile.SaveFilePath));
        }
        else
        {
            Assert.Equal("shown", File.ReadAllText(shownFile.SaveFilePath!));
            Assert.Equal("attached", File.ReadAllText(attachedFile.SaveFilePath!));
        }
        if (confirmed && deleteFiles && recycleFiles)
        {
            Assert.Equal(2, _moved.Count);
            Assert.Equal("shown", File.ReadAllText(RecycledFile(shownFile.SaveFilePath!)));
            Assert.Equal("attached", File.ReadAllText(RecycledFile(attachedFile.SaveFilePath!)));
        }
        else
        {
            Assert.Empty(_moved);
        }
        Assert.Contains(hidden, _model.Packages);
        Assert.Contains(hiddenAttached, _model.Packages);
        Assert.Equal("hidden", File.ReadAllText(hiddenFile.SaveFilePath!));
        Assert.Equal("hidden attachment", File.ReadAllText(hiddenAttachedFile.SaveFilePath!));
    }

    [Fact]
    public async Task DeleteSelected_RechecksVisibilityAfterConfirmation()
    {
        var package = AddPackage("TargetFolder");
        var item = AddFile(package, "data.bin");
        item.Status = DownloadStatus.Paused;
        _model.SelectItemFromPointer(item, controlPressed: false, wasSelected: false);
        Assert.True(_model.CanDeleteSelected);
        var previousFilter = _model.SelectedStatusFilter;
        int confirmations = 0;
        _model.CleanupConfirmationOverride = _ =>
        {
            confirmations++;
            _model.SelectedStatusFilter = DownloadStatusFilter.Completed;
            return (true, true, false);
        };

        try
        {
            await _model.DeleteSelected();

            Assert.Equal(1, confirmations);
            Assert.False(_model.CanDeleteSelected);
            Assert.Contains(package, _model.Packages);
            Assert.Contains(item, package.Items);
            Assert.Equal("content", File.ReadAllText(item.SaveFilePath!));
        }
        finally { _model.SelectedStatusFilter = previousFilter; }
    }

    [Fact]
    public async Task Package_PermanentChoiceDoesNotInvokeRecycling()
    {
        var package = AddPackage("TargetFolder");
        var item = AddFile(package, "data.bin");
        _model.CleanupConfirmationOverride = _ => (true, true, false);
        _model.RecyclePaths = _ => throw new InvalidOperationException("Recycling was not expected.");

        await _model.RemovePackage(package);

        Assert.DoesNotContain(package, _model.Packages);
        Assert.False(File.Exists(item.SaveFilePath));
        Assert.Empty(_moved);
    }

    [Fact]
    public async Task MultipleSelection_PartialFailureKeepsEntriesAndPreservesBothResults()
    {
        var first = AddPackage("TargetFolder");
        var firstItem = AddFile(first, "first.bin", "first");
        first.IsSelected = true;
        var second = AddPackage("OtherFolder", "OtherFolder");
        var remaining = AddFile(second, "second.bin", "second");
        second.IsSelected = true;
        _model.RecyclePaths = paths =>
        {
            MoveToTestBin(paths.Where(path => path == firstItem.SaveFilePath).ToArray());
            throw new IOException("Recycling interrupted.");
        };

        await _model.DeleteSelected();

        Assert.Contains(first, _model.Packages);
        Assert.Contains(second, _model.Packages);
        Assert.Equal(firstItem.SaveFilePath, Assert.Single(_moved).Source);
        Assert.Equal("first", File.ReadAllText(RecycledFile(firstItem.SaveFilePath!)));
        Assert.Equal("second", File.ReadAllText(remaining.SaveFilePath!));
        Assert.Equal(Loc.Get("Status_RecycleFailed"), _model.StatusSummary);
    }

    [Fact]
    public async Task NativeBackend_LockedFileFailsWithoutRemovingItsContents()
    {
        var file = Path.Combine(_root, "locked.bin");
        File.WriteAllText(file, "locked content");
        using (var handle = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var failure = await Record.ExceptionAsync(() => Task.Run(() => RecycleBinService.Move(new[] { file })));
            Assert.True(failure is IOException or COMException, $"Unexpected failure: {failure?.GetType().Name ?? "none"}");
            Assert.True(File.Exists(file));
        }
        Assert.Equal("locked content", File.ReadAllText(file));
    }

    [Theory]
    [InlineData(0u, false)]
    [InlineData(0x40u, false)]
    [InlineData(0x80u, true)]
    [InlineData(0xC0u, true)]
    public void NativeCallback_RejectsOperationsWithoutRecycling(uint flags, bool allowed)
    {
        var sink = new RecycleBinService.RecycleOnlySink();
        Assert.Equal(allowed, sink.PreDeleteItem(flags, 0) == 0);
        Assert.Equal(!allowed, sink.Failure < 0);
    }

    private DownloadPackage AddPackage(string relativeDirectory, string name = "TargetFolder")
    {
        var directory = Path.Combine(_root, relativeDirectory);
        Directory.CreateDirectory(directory);
        var package = new DownloadPackage { Name = name, SaveDirectory = directory, AutoPar2Repair = false };
        _model.Packages.Add(package);
        return package;
    }

    private static DownloadItem AddFile(DownloadPackage package, string name, string content = "content")
    {
        var path = Path.Combine(package.SaveDirectory, name);
        File.WriteAllText(path, content);
        var item = new DownloadItem { FileName = name, SaveFilePath = path };
        package.Items.Add(item);
        return item;
    }

    private string RecycledFile(string path) => Assert.Single(_moved, move => move.Source == path).Destination;

    private void MoveToTestBin(IReadOnlyCollection<string> paths)
    {
        var bin = Path.Combine(_root, "Recycled");
        Directory.CreateDirectory(bin);
        foreach (var path in paths)
        {
            Assert.StartsWith(_root + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
            var destination = Path.Combine(bin, _moved.Count.ToString());
            File.Move(path, destination);
            _moved.Add((path, destination));
        }
    }

    public void Dispose()
    {
        _model.Packages.Clear();
        Directory.Delete(_root, true);
    }
}
