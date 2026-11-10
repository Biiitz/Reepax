using System.ComponentModel;
using System.IO;
using System.Text.Json;
using Reepax.Models;
using Reepax.Services.Storage;
using Reepax.ViewModels;

namespace Reepax.Tests;

[Collection("SharedQueue")]
public sealed class PackageSortingResetTests : IDisposable
{
    private readonly MainViewModel _model = new();
    private readonly DownloadPackage[] _previousPackages;
    private readonly string _directory = Directory.CreateTempSubdirectory("PackageSorting_").FullName;

    public PackageSortingResetTests()
    {
        _previousPackages = _model.Packages.ToArray();
        _model.Packages.Clear();
    }

    [Theory]
    [InlineData(DownloadStatus.Paused)]
    [InlineData(DownloadStatus.Downloading)]
    [InlineData(DownloadStatus.Completed)]
    [InlineData(DownloadStatus.Queued)]
    [InlineData(DownloadStatus.WaitingForBrowser)]
    [InlineData(DownloadStatus.Failed)]
    public void ResetSorting_RestoresAddedOrderWithoutChangingDownloadsOrFiles(DownloadStatus status)
    {
        var package = AddPackage("third.bin", "first.bin", "second.bin");
        var original = package.Items.ToArray();
        var item = original[0];
        item.IsSelected = true;
        bool enabled = status != DownloadStatus.Paused;
        item.IsEnabled = enabled;
        item.Status = status;
        item.DownloadedBytes = 3;
        item.TotalBytes = 8;
        item.ExpectedChecksum = "expected";
        item.CalculatedChecksum = "calculated";
        var paths = new[] { item.SaveFilePath!, item.SaveFilePath + ".part", item.SaveFilePath + ".part.segments" };
        byte[] bytes = { 0, 1, 127, 255 };
        foreach (string path in paths) File.WriteAllBytes(path, bytes);
        _model.MoveItemAfter(item, original[2]);

        Assert.Equal(status, item.Status);
        Assert.True(_model.ResetPackageSortingCommand.CanExecute(package));
        _model.ResetPackageSortingCommand.Execute(package);

        Assert.Equal(original, package.Items);
        Assert.Equal(original, package.ItemsSnapshot);
        Assert.Equal(new[] { 1L, 2L, 3L }, package.Items.Select(entry => entry.AddedOrderIndex));
        Assert.Equal(new[] { 1, 2, 3 }, package.Items.Select(entry => entry.OriginalOrderIndex));
        Assert.Equal(status, item.Status);
        Assert.True(item.IsSelected);
        Assert.Equal(enabled, item.IsEnabled);
        Assert.Equal(3, item.DownloadedBytes);
        Assert.Equal(8, item.TotalBytes);
        Assert.Equal("expected", item.ExpectedChecksum);
        Assert.Equal("calculated", item.CalculatedChecksum);
        Assert.Equal(paths[0], item.SaveFilePath);
        Assert.Equal("third.bin", item.FileName);
        foreach (string path in paths) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.False(_model.ResetPackageSortingCommand.CanExecute(package));
    }

    [Fact]
    public void ResetSorting_ResetsSelectedPackagesIncludingClippedPackagesAndLeavesOthersUntouched()
    {
        var first = AddPackage("first.bin", "second.bin");
        var child = AddPackage("child-first.bin", "child-second.bin");
        var unrelated = AddPackage("other-first.bin", "other-second.bin");
        child.ParentPackageId = first.Id;
        first.ClippedPackages.Add(child);
        var firstOrder = first.Items.ToArray();
        var childOrder = child.Items.ToArray();
        foreach (var package in _model.Packages)
            _model.MoveItemAfter(package.Items[0], package.Items[1]);
        first.IsSelected = true;
        child.IsSelected = true;
        var unrelatedOrder = unrelated.Items.ToArray();
        var unrelatedIndices = unrelated.Items.Select(item => item.OriginalOrderIndex).ToArray();
        _model.RefreshRootPackages();
        var roots = _model.RootPackages.ToArray();

        _model.ResetPackageSortingCommand.Execute(first);

        Assert.Equal(firstOrder, first.Items);
        Assert.Equal(childOrder, child.Items);
        Assert.Equal(unrelatedOrder, unrelated.Items);
        Assert.Equal(unrelatedIndices, unrelated.Items.Select(item => item.OriginalOrderIndex));
        Assert.Equal(roots, _model.RootPackages);
        Assert.Same(child, Assert.Single(first.ClippedPackages));
    }

    [Fact]
    public void ResetSorting_UnselectedContextTargetDoesNotResetSelectedPackages()
    {
        var selected = AddPackage("selected-first.bin", "selected-second.bin");
        var target = AddPackage("target-first.bin", "target-second.bin");
        var targetOrder = target.Items.ToArray();
        _model.MoveItemAfter(selected.Items[0], selected.Items[1]);
        _model.MoveItemAfter(target.Items[0], target.Items[1]);
        selected.IsSelected = true;
        var selectedOrder = selected.Items.ToArray();

        _model.ResetPackageSortingCommand.Execute(target);

        Assert.Equal(targetOrder, target.Items);
        Assert.Equal(selectedOrder, selected.Items);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("removed")]
    [InlineData("empty")]
    [InlineData("single")]
    [InlineData("unchanged")]
    [InlineData("settings")]
    [InlineData("busy")]
    [InlineData("removing")]
    [InlineData("editing")]
    [InlineData("path-change")]
    [InlineData("selected-busy")]
    public void ResetSorting_IsDisabledAndDoesNothingWhenUnavailable(string reason)
    {
        var package = AddPackage("first.bin", "second.bin");
        _model.MoveItemAfter(package.Items[0], package.Items[1]);
        DownloadPackage? target = package;
        switch (reason)
        {
            case "null": target = null; break;
            case "removed": _model.Packages.Remove(package); break;
            case "empty": package.Items.Clear(); break;
            case "single": package.Items.RemoveAt(0); break;
            case "unchanged": package.Items.Move(0, 1); break;
            case "settings": _model.SelectedMainTab = AppMainTab.Settings; break;
            case "busy": _model.IsBusy = true; break;
            case "removing": _model.IsRemovalInProgress = true; break;
            case "editing": package.IsEditing = true; break;
            case "path-change": package.Items[0].SetPathChangePending(true); break;
            case "selected-busy":
                package.IsSelected = true;
                var busy = AddPackage("busy.bin");
                busy.IsSelected = true;
                busy.IsEditing = true;
                break;
        }
        var before = package.Items.ToArray();
        var indices = before.Select(item => item.OriginalOrderIndex).ToArray();

        Assert.False(_model.ResetPackageSortingCommand.CanExecute(target));
        _model.ResetPackageSortingCommand.Execute(target);

        Assert.Equal(before, package.Items);
        Assert.Equal(indices, package.Items.Select(item => item.OriginalOrderIndex));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResetSorting_ClearsColumnSortAndResetsManualBaselineWithoutReorderingOtherPackages(bool visibleOrderMatches)
    {
        var package = visibleOrderMatches
            ? AddPackage("a.bin", "b.bin", "c.bin")
            : AddPackage("c.bin", "a.bin", "b.bin");
        var original = package.Items.ToArray();
        _model.MoveItemAfter(package.Items[0], package.Items[2]);
        var unrelated = AddPackage("f.bin", "d.bin", "e.bin");
        _model.ToggleColumnSort("Name");
        Assert.Equal(ListSortDirection.Ascending, _model.SortDirection);
        var unrelatedOrder = unrelated.Items.ToArray();
        var roots = _model.RootPackages.ToArray();

        Assert.True(_model.ResetPackageSortingCommand.CanExecute(package));
        _model.ResetPackageSortingCommand.Execute(package);

        Assert.Null(_model.SortColumn);
        Assert.Null(_model.SortDirection);
        Assert.Equal(original, package.Items);
        Assert.Equal(unrelatedOrder, unrelated.Items);
        Assert.Equal(roots, _model.RootPackages);
        for (int click = 0; click < 3; click++) _model.ToggleColumnSort("Name");
        Assert.Equal(original, package.Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResetSorting_PreservesAddedOrderAcrossPersistenceAndLaterAdditions(bool history)
    {
        var package = AddPackage("third.bin", "first.bin", "second.bin");
        var originalIds = package.Items.Select(item => item.Id).ToArray();
        _model.MoveItemAfter(package.Items[0], package.Items[2]);
        using var storage = new DownloadPersistenceService(Path.Combine(_directory, "downloads.json"));
        if (history) storage.SaveHistory(new[] { package }, sync: true);
        else storage.SaveDownloads(new[] { package }, sync: true);
        var loaded = Assert.Single(history ? storage.LoadHistory() : storage.LoadDownloads());
        Assert.Equal(package.Items.Select(item => item.Id), loaded.Items.Select(item => item.Id));
        Assert.Equal(new[] { 2L, 3L, 1L }, loaded.Items.Select(item => item.AddedOrderIndex));
        var added = new DownloadItem { FileName = "later.bin", Status = DownloadStatus.Paused };
        loaded.Items.Insert(0, added);
        Assert.Equal(4, added.AddedOrderIndex);
        _model.Packages.Clear();
        _model.Packages.Add(loaded);

        _model.ResetPackageSortingCommand.Execute(loaded);

        Assert.Equal(originalIds.Append(added.Id), loaded.Items.Select(item => item.Id));
    }

    [Fact]
    public void LegacyDownloads_UseSavedOrderAsBaselineAndRetainItAfterSaving()
    {
        string path = Path.Combine(_directory, "downloads.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new[]
        {
            new { Id = Guid.NewGuid(), Name = "Legacy", SaveDirectory = _directory, AutoPar2Repair = false,
                Items = new[] { new { FileName = "second.bin" }, new { FileName = "first.bin" } } }
        }));
        using var storage = new DownloadPersistenceService(path);
        var loaded = Assert.Single(storage.LoadDownloads());
        Assert.Equal(new[] { 1L, 2L }, loaded.Items.Select(item => item.AddedOrderIndex));
        _model.Packages.Add(loaded);
        var original = loaded.Items.Select(item => item.FileName).ToArray();
        _model.MoveItemAfter(loaded.Items[0], loaded.Items[1]);
        storage.SaveDownloads(new[] { loaded }, sync: true);
        var restored = Assert.Single(storage.LoadDownloads());
        _model.Packages.Clear();
        _model.Packages.Add(restored);

        _model.ResetPackageSortingCommand.Execute(restored);

        Assert.Equal(original, restored.Items.Select(item => item.FileName));
    }

    private DownloadPackage AddPackage(params string[] names)
    {
        var package = new DownloadPackage { IsCustomName = true, Name = "Example", SaveDirectory = _directory,
            AutoPar2Repair = false, AutoExtractArchives = false };
        package.AddItems(names.Select(name => new DownloadItem { FileName = name, Status = DownloadStatus.Paused,
            SaveFilePath = Path.Combine(_directory, name), CreatedAt = new DateTime(2026, 1, 1) }));
        _model.Packages.Add(package);
        return package;
    }

    public void Dispose()
    {
        _model.IsBusy = false;
        _model.IsRemovalInProgress = false;
        _model.SelectedMainTab = AppMainTab.Downloads;
        _model.Packages.Clear();
        foreach (var package in _previousPackages) _model.Packages.Add(package);
        Directory.Delete(_directory, recursive: true);
    }
}
