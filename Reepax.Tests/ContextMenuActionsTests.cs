using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Linq;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

public sealed class ContextMenuActionsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ContextActions_").FullName;
    private readonly MainViewModel _vm = new();
    private readonly DownloadPackage[] _previousPackages;

    public ContextMenuActionsTests()
    {
        _previousPackages = _vm.Packages.ToArray();
        _vm.Packages.Clear();
        _vm.RenameFailureNotification = _ => { };
    }

    [Fact]
    public void OpenBrowser_UsesSelectedFilesAcrossPackagesAndContinuesAfterLaunchFailure()
    {
        var first = AddPackage("First");
        var second = AddPackage("Second");
        first.Items[0].OriginalUrl = "https://example.com/original";
        first.Items[0].DirectDownloadUrl = "https://example.com/direct";
        first.Items[0].IsSelected = true;
        second.Items[0].OriginalUrl = "file:///invalid";
        second.Items[0].DirectDownloadUrl = "https://example.com/fallback";
        second.Items[0].IsSelected = true;
        var invalid = new DownloadItem { OriginalUrl = "javascript:void(0)", IsSelected = true };
        second.Items.Add(invalid);
        var opened = new List<string>();
        _vm.DefaultBrowserLauncher = uri =>
        {
            opened.Add(uri.AbsoluteUri);
            if (opened.Count == 1) throw new InvalidOperationException();
        };
        _vm.OpenItemsInDefaultBrowser(first.Items[0]);
        Assert.Equal(new[] { "https://example.com/original", "https://example.com/fallback" }, opened);
        Assert.Equal(Loc.Format("Status_BrowserLinksOpened", 1, 2), _vm.StatusSummary);
    }

    [Fact]
    public void ContextTargets_UnselectedEntryIsIsolatedAndRemovedEntriesAreIgnored()
    {
        var first = AddPackage("First");
        var second = AddPackage("Second");
        first.IsSelected = true;
        first.Items[0].IsSelected = true;
        Assert.Equal(new[] { second }, _vm.GetPackagesForContextAction(second));
        Assert.Equal(new[] { second.Items[0] }, _vm.GetItemsForContextAction(second.Items[0]));
        second.IsSelected = true;
        Assert.Equal(new[] { first, second }, _vm.GetPackagesForContextAction(first));
        _vm.Packages.Remove(second);
        Assert.Empty(_vm.GetPackagesForContextAction(second));
        Assert.Empty(_vm.GetItemsForContextAction(second.Items[0]));
    }

    [Fact]
    public async Task ChangePaths_MovesSelectedPackagesWithPartialDownloadsAndKeepsUnselectedPackage()
    {
        var first = AddPackage("First");
        var second = AddPackage("Second");
        var untouched = AddPackage("Third");
        first.IsSelected = second.IsSelected = true;
        var oldDirectories = new[] { first.SaveDirectory, second.SaveDirectory };
        var target = Path.Combine(_root, "Target");
        Assert.True(await _vm.ChangeDownloadPathsAsync(_vm.GetPackagesForContextAction(first), target));
        foreach (var package in new[] { first, second })
        {
            Assert.Equal(Path.Combine(target, package.Name), package.SaveDirectory);
            Assert.Equal(Path.Combine(package.SaveDirectory, "data.bin"), package.Items[0].SaveFilePath);
            Assert.Equal("complete", File.ReadAllText(package.Items[0].SaveFilePath!));
            Assert.Equal("partial", File.ReadAllText(package.Items[0].SaveFilePath + ".part"));
            Assert.Equal("checkpoint", File.ReadAllText(package.Items[0].SaveFilePath + ".part.segments"));
            Assert.False(package.Items[0].IsUnavailableForProcessing);
        }
        Assert.All(oldDirectories, directory => Assert.False(Directory.Exists(directory)));
        Assert.Equal("complete", File.ReadAllText(untouched.Items[0].SaveFilePath!));
        Assert.False(_vm.IsBusy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangePaths_WithoutFilesCreatesDirectoryOnlyWhenDownloadStarts(bool emptySourceExists)
    {
        var original = Path.Combine(_root, "Source", "Pending");
        if (emptySourceExists) Directory.CreateDirectory(original);
        var package = new DownloadPackage { Name = "Pending", SaveDirectory = original, IsCustomName = true, AutoPar2Repair = false };
        var item = new DownloadItem { FileName = "data.bin", SaveFilePath = Path.Combine(original, "data.bin"), Status = DownloadStatus.Paused };
        package.Items.Add(item);
        _vm.Packages.Add(package);
        var target = Path.Combine(_root, "Target");
        Assert.True(await _vm.ChangeDownloadPathsAsync(new[] { package }, target));
        Assert.Equal(Path.Combine(target, "Pending"), package.SaveDirectory);
        Assert.Equal(Path.Combine(package.SaveDirectory, "data.bin"), item.SaveFilePath);
        Assert.False(Directory.Exists(target));
        var saved = Assert.Single(DownloadPersistenceService.SnapshotDtos(new[] { package }));
        Assert.Equal(package.SaveDirectory, saved.SaveDirectory);
        Assert.Equal(item.SaveFilePath, Assert.Single(saved.Items).SaveFilePath);
        using var handler = new TestMockHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 })
        }));
        using var client = new HttpClient(handler);
        var engine = new DownloadEngine(client) { MaxConnectionsPerDownload = 1 };
        var download = engine.StartDownloadAsync(item, "https://example.invalid/data.bin", null, null, null, null);
        try
        {
            await download.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(DownloadStatus.Completed, item.Status);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(item.SaveFilePath!));
        }
        finally
        {
            await engine.PauseAllAsync();
            await download.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task ChangePaths_MixedSelectionCreatesFoldersOnlyForExistingData()
    {
        var existing = AddPackage("Existing");
        var pending = new DownloadPackage { Name = "Pending", SaveDirectory = Path.Combine(_root, "Source", "Pending"), IsCustomName = true };
        pending.Items.Add(new DownloadItem { FileName = "pending.bin", Status = DownloadStatus.Paused });
        _vm.Packages.Add(pending);
        var target = Path.Combine(_root, "Target");
        Assert.True(await _vm.ChangeDownloadPathsAsync(new[] { pending, existing }, target));
        Assert.False(Directory.Exists(pending.SaveDirectory));
        Assert.True(Directory.Exists(existing.SaveDirectory));
        Assert.Equal(Path.Combine(target, "Pending", "pending.bin"), pending.Items[0].SaveFilePath);
        Assert.Equal("partial", File.ReadAllText(existing.Items[0].SaveFilePath + ".part"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangePaths_PreflightsEntireSelectionBeforeMoving(bool busy)
    {
        var first = AddPackage("First");
        var second = AddPackage("Second");
        var original = first.SaveDirectory;
        var target = Path.Combine(_root, "Target");
        if (busy) second.Items[0].IsVerifyingIntegrity = true;
        else
        {
            Directory.CreateDirectory(Path.Combine(target, second.Name));
            File.WriteAllText(Path.Combine(target, second.Name, "sentinel.bin"), "keep");
        }
        try
        {
            Assert.False(await _vm.ChangeDownloadPathsAsync(new[] { first, second }, target));
            Assert.Equal(original, first.SaveDirectory);
            Assert.Equal("complete", File.ReadAllText(first.Items[0].SaveFilePath!));
            Assert.False(Directory.Exists(Path.Combine(target, first.Name)));
            Assert.All(new[] { first, second }, package => Assert.False(package.Items[0].IsUnavailableForProcessing));
            if (!busy) Assert.Equal("keep", File.ReadAllText(Path.Combine(target, second.Name, "sentinel.bin")));
        }
        finally { second.Items[0].IsVerifyingIntegrity = false; }
    }

    [Fact]
    public async Task SharedDirectory_OnlyMovesOwnedFilesAndKeepsOtherPackageData()
    {
        var first = AddPackage("First");
        var second = new DownloadPackage { Name = "Second", SaveDirectory = first.SaveDirectory, AutoExtractArchives = false, AutoPar2Repair = false };
        second.Items.Add(new DownloadItem { FileName = "other.bin", SaveFilePath = Path.Combine(first.SaveDirectory, "other.bin") });
        _vm.Packages.Add(second);
        File.WriteAllText(second.Items[0].SaveFilePath!, "keep");
        File.WriteAllText(Path.Combine(first.SaveDirectory, "unrelated.txt"), "keep");
        var oldDirectory = first.SaveDirectory;
        Assert.True(await _vm.ChangeDownloadPathsAsync(new[] { first }, Path.Combine(_root, "Target")));
        Assert.Equal("keep", File.ReadAllText(second.Items[0].SaveFilePath!));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(oldDirectory, "unrelated.txt")));
        Assert.False(File.Exists(Path.Combine(oldDirectory, "data.bin")));
        Assert.Equal("checkpoint", File.ReadAllText(first.Items[0].SaveFilePath + ".part.segments"));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "Target"), ".download-move-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyFallback_PreservesContentsAndCleansStagingOnFailure(bool fail)
    {
        var package = AddPackage("First");
        var empty = Path.Combine(package.SaveDirectory, "Empty");
        Directory.CreateDirectory(empty);
        package.ExtractionDirectory = empty;
        var original = package.SaveDirectory;
        var target = Path.Combine(_root, "Target");
        var plan = Assert.Single(PackageDirectoryMoveService.Prepare(new[] { package }, _vm.Packages.ToArray(), target));
        Assert.True(plan.WholeDirectory);
        using var locked = fail ? new FileStream(plan.Files.Last().Source, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        var service = new PackageDirectoryMoveService(allowDirectoryRename: false);
        if (fail)
        {
            await Assert.ThrowsAsync<IOException>(() => service.ExecuteAsync(plan));
            Assert.True(Directory.Exists(original));
            Assert.All(plan.Files, file => Assert.True(File.Exists(file.Source)));
            Assert.False(Directory.Exists(plan.Target));
        }
        else
        {
            Assert.True(await service.ExecuteAsync(plan));
            Assert.False(Directory.Exists(original));
            Assert.True(Directory.Exists(Path.Combine(plan.Target, "Empty")));
            Assert.Equal(Path.Combine(plan.Target, "Empty"), plan.ExtractionDirectory);
            Assert.Equal("complete", File.ReadAllText(Path.Combine(plan.Target, "data.bin")));
            Assert.Equal("checkpoint", File.ReadAllText(Path.Combine(plan.Target, "data.bin.part.segments")));
        }
        Assert.Empty(Directory.GetDirectories(target, ".download-move-*"));
    }

    [Fact]
    public void PathReservation_BlocksProcessingWithoutChangingRemovalState()
    {
        var item = new DownloadItem();
        item.SetPathChangePending(true);
        Assert.True(item.IsUnavailableForProcessing);
        Assert.False(item.IsRemovalPending);
        item.SetPathChangePending(false);
        Assert.False(item.IsUnavailableForProcessing);
        item.ReserveForRemoval();
        item.SetPathChangePending(true);
        item.SetPathChangePending(false);
        Assert.True(item.IsUnavailableForProcessing);
        item.ReleaseFromRemoval(removed: false);
    }

    [Fact]
    public async Task ChangePaths_WaitsForOtherPathOperationsAndUsesCurrentItems()
    {
        var package = AddPackage("First");
        var lease = await PackagePathOperationGate.Instance.EnterAsync();
        Task<bool>? operation = null;
        try
        {
            operation = _vm.ChangeDownloadPathsAsync(new[] { package }, Path.Combine(_root, "Target"));
            Assert.False(operation.IsCompleted);
            var added = new DownloadItem { FileName = "added.bin", SaveFilePath = Path.Combine(package.SaveDirectory, "added.bin") };
            File.WriteAllText(added.SaveFilePath, "added");
            package.Items.Add(added);
            lease.Dispose();
            Assert.True(await operation.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(Path.Combine(package.SaveDirectory, "added.bin"), added.SaveFilePath);
            Assert.Equal("added", File.ReadAllText(added.SaveFilePath!));
            Assert.False(added.IsUnavailableForProcessing);
        }
        finally
        {
            lease.Dispose();
            if (operation != null) await operation.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveredPaths_RequireConfirmationBeforeMoving(bool confirm)
    {
        var package = AddPackage("First");
        var original = package.SaveDirectory;
        package.RequiresRecoveredPathConfirmation = true;
        _vm.RecoveredPathConfirmationOverride = paths =>
        {
            Assert.Equal(new[] { original }, paths);
            return confirm;
        };
        Assert.Equal(confirm, await _vm.ChangeDownloadPathsAsync(new[] { package }, Path.Combine(_root, "Target")));
        Assert.Equal(!confirm, package.RequiresRecoveredPathConfirmation);
        Assert.Equal(!confirm, Directory.Exists(original));
        Assert.Equal("complete", File.ReadAllText(package.Items[0].SaveFilePath!));
        Assert.False(package.Items[0].IsUnavailableForProcessing);
    }

    [Fact]
    public async Task DestinationCreatedAfterPlanning_IsNotOverwrittenAndOriginalDataRemains()
    {
        var package = AddPackage("First");
        var plan = Assert.Single(PackageDirectoryMoveService.Prepare(new[] { package }, _vm.Packages.ToArray(), Path.Combine(_root, "Target")));
        Directory.CreateDirectory(plan.Target);
        File.WriteAllText(Path.Combine(plan.Target, "data.bin"), "keep");
        await Assert.ThrowsAsync<IOException>(() => new PackageDirectoryMoveService(allowDirectoryRename: false).ExecuteAsync(plan));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(plan.Target, "data.bin")));
        Assert.Equal("complete", File.ReadAllText(package.Items[0].SaveFilePath!));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(plan.Target)!, ".download-move-*"));
    }

    [Theory]
    [InlineData("en", "Copy URL", "Open in browser")]
    [InlineData("de", "URL kopieren", "Im Browser öffnen")]
    public void ContextMenus_ExposeActionsOnlyForTheirTargetTypeAndTranslateLabels(string language, string copy, string browser)
    {
        var localization = LocalizationService.Instance;
        var previous = localization.CurrentLanguage;
        try
        {
            localization.CurrentLanguage = language;
            Assert.Equal(copy, Loc.Get("Menu_CopyUrl"));
            Assert.Equal(browser, Loc.Get("Menu_OpenInBrowser"));
            Assert.NotEqual("Menu_ChangeDownloadPath", Loc.Get("Menu_ChangeDownloadPath"));
            var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Reepax"));
            var xaml = XDocument.Load(Path.Combine(project, "MainWindow.xaml"));
            XNamespace key = "http://schemas.microsoft.com/winfx/2006/xaml";
            var menus = xaml.Descendants().Where(element => element.Name.LocalName == "ContextMenu");
            var package = menus.Single(element => (string?)element.Attribute(key + "Key") == "PackageContextMenu");
            var item = menus.Single(element => (string?)element.Attribute(key + "Key") == "ItemContextMenu");
            Assert.Contains("ChangeDownloadPath_MenuItem_Click", package.ToString());
            Assert.DoesNotContain("OpenItemInBrowser_MenuItem_Click", package.ToString());
            Assert.Contains("OpenItemInBrowser_MenuItem_Click", item.ToString());
            Assert.DoesNotContain("ChangeDownloadPath_MenuItem_Click", item.ToString());
            Assert.DoesNotContain("Menu_CopyOriginalUrl", item.ToString());
        }
        finally { localization.CurrentLanguage = previous; }
    }

    private DownloadPackage AddPackage(string name)
    {
        var directory = Path.Combine(_root, "Source", name);
        Directory.CreateDirectory(directory);
        var package = new DownloadPackage { Name = name, SaveDirectory = directory, IsCustomName = true, AutoExtractArchives = false, AutoPar2Repair = false };
        var item = new DownloadItem { FileName = "data.bin", SaveFilePath = Path.Combine(directory, "data.bin"), Status = DownloadStatus.Paused };
        package.Items.Add(item);
        _vm.Packages.Add(package);
        File.WriteAllText(item.SaveFilePath, "complete");
        File.WriteAllText(item.SaveFilePath + ".part", "partial");
        File.WriteAllText(item.SaveFilePath + ".part.segments", "checkpoint");
        return package;
    }

    public void Dispose()
    {
        _vm.Packages.Clear();
        foreach (var package in _previousPackages) _vm.Packages.Add(package);
        var resolved = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(resolved) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
            !Path.GetFileName(resolved).StartsWith("ContextActions_", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected test directory.");
        Directory.Delete(resolved, recursive: true);
    }
}
