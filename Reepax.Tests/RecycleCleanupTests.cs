using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Localization;
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
    public async Task Package_RecyclesDirectoriesWithoutDiscardingPartialOrEmptyFiles(bool nestedExtraction)
    {
        var package = AddPackage("TargetFolder");
        var item = AddFile(package, "data.bin");
        File.WriteAllText(item.SaveFilePath + ".part", "partial");
        File.WriteAllText(item.SaveFilePath + ".part.segments", "segments");
        File.WriteAllText(Path.Combine(package.SaveDirectory, "empty.bin"), "");
        Directory.CreateDirectory(Path.Combine(package.SaveDirectory, "nested"));
        File.WriteAllText(Path.Combine(package.SaveDirectory, "nested", "extracted.bin"), "nested");
        package.ExtractionDirectory = nestedExtraction
            ? Path.Combine(package.SaveDirectory, "TargetFolder")
            : Path.Combine(_root, "extracted", "TargetFolder");
        Directory.CreateDirectory(package.ExtractionDirectory);
        File.WriteAllText(Path.Combine(package.ExtractionDirectory, "result.bin"), "result");

        await _model.RemovePackage(package);

        Assert.DoesNotContain(package, _model.Packages);
        Assert.Equal(nestedExtraction ? 1 : 2, _moved.Count);
        var recycled = _moved.Single(move => move.Source == package.SaveDirectory).Destination;
        Assert.Equal("content", File.ReadAllText(Path.Combine(recycled, "data.bin")));
        Assert.Equal("partial", File.ReadAllText(Path.Combine(recycled, "data.bin.part")));
        Assert.Equal("segments", File.ReadAllText(Path.Combine(recycled, "data.bin.part.segments")));
        Assert.True(File.Exists(Path.Combine(recycled, "empty.bin")));
        Assert.Equal("nested", File.ReadAllText(Path.Combine(recycled, "nested", "extracted.bin")));
        var extracted = nestedExtraction ? Path.Combine(recycled, "TargetFolder")
            : _moved.Single(move => move.Source == package.ExtractionDirectory).Destination;
        Assert.Equal("result", File.ReadAllText(Path.Combine(extracted, "result.bin")));
    }

    [Fact]
    public async Task Package_EmptyDedicatedDirectoryIsRecycled()
    {
        var package = AddPackage("TargetFolder");

        await _model.RemovePackage(package);

        Assert.DoesNotContain(package, _model.Packages);
        Assert.False(Directory.Exists(package.SaveDirectory));
        var moved = Assert.Single(_moved);
        Assert.True(Directory.Exists(moved.Destination));
        Assert.Empty(Directory.EnumerateFileSystemEntries(moved.Destination));
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
    public async Task Item_RecyclesAllSidecarsAndPreservesOtherItems()
    {
        var package = AddPackage("TargetFolder");
        var item = AddFile(package, "data.bin");
        var other = AddFile(package, "other.bin", "other");
        var suffixes = new[] { ".part", ".part.segments", ".segments", ".tmp", ".temp", ".reepax_tmp" };
        foreach (var suffix in suffixes)
            File.WriteAllText(item.SaveFilePath + suffix, suffix);

        await _model.RemoveItem(item);

        Assert.Contains(package, _model.Packages);
        Assert.DoesNotContain(item, package.Items);
        Assert.Equal("other", File.ReadAllText(other.SaveFilePath!));
        Assert.Equal(7, _moved.Count);
        Assert.All(_moved, move => Assert.True(File.Exists(move.Destination)));
        foreach (var suffix in suffixes)
            Assert.Equal(suffix, File.ReadAllText(_moved.Single(move => move.Source == item.SaveFilePath + suffix).Destination));
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
        AddFile(first, "first.bin", "first");
        first.IsSelected = true;
        var second = AddPackage("OtherFolder", "OtherFolder");
        var remaining = AddFile(second, "second.bin", "second");
        second.IsSelected = true;
        _model.RecyclePaths = paths =>
        {
            MoveToTestBin(paths.Where(path => path == first.SaveDirectory).ToArray());
            throw new IOException("Recycling interrupted.");
        };

        await _model.DeleteSelected();

        Assert.Contains(first, _model.Packages);
        Assert.Contains(second, _model.Packages);
        Assert.Equal("first", File.ReadAllText(Path.Combine(Assert.Single(_moved).Destination, "first.bin")));
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

    private void MoveToTestBin(IReadOnlyCollection<string> paths)
    {
        var bin = Path.Combine(_root, "Recycled");
        Directory.CreateDirectory(bin);
        foreach (var path in paths)
        {
            Assert.StartsWith(_root + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
            var destination = Path.Combine(bin, _moved.Count.ToString());
            if (Directory.Exists(path))
                Directory.Move(path, destination);
            else
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
