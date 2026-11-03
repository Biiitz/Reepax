using System.IO;
using System.Security.Cryptography;
using System.Text;
using Reepax.Models;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

public sealed class ItemRenameSafetyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ItemRename_").FullName;
    private static readonly string Checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("original data")));

    [Theory]
    [InlineData("", false)]
    [InlineData(".part", false)]
    [InlineData(".part.segments", false)]
    [InlineData("", true)]
    public void Rename_OccupiedTargetPreservesModelAndAllOriginalBytes(string suffix, bool directory)
    {
        var item = CreateItem();
        string original = item.SaveFilePath!;
        string target = Path.Combine(_root, "second.bin");
        if (directory) Directory.CreateDirectory(target + suffix);
        else File.WriteAllText(target + suffix, "unrelated data");
        RenameThroughViewModel(item, "second.bin", Loc.Get("Status_ItemRenameFailed"));
        AssertOriginal(item, original);
        if (suffix != "" || directory) Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".part") && suffix != ".part");
        Assert.False(File.Exists(target + ".part.segments") && suffix != ".part.segments");
        if (directory) Assert.Empty(Directory.EnumerateFileSystemEntries(target + suffix));
        if (!directory) Assert.Equal("unrelated data", File.ReadAllText(target + suffix));
    }

    [Theory]
    [InlineData(DownloadStatus.Completed, "")]
    [InlineData(DownloadStatus.Paused, ".part")]
    [InlineData(DownloadStatus.Paused, ".part.segments")]
    public void Rename_LockedSourcePreservesModelAndCanBeRetried(DownloadStatus status, string suffix)
    {
        var item = CreateItem();
        item.Status = status;
        string original = item.SaveFilePath!;
        using (File.Open(original + suffix, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            RenameThroughViewModel(item, "second.bin", Loc.Get("Status_ItemRenameFailed"));
            AssertOriginal(item, original, status);
            Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(_root), path => Path.GetFileName(path).StartsWith("second"));
        }
        RenameThroughViewModel(item, "second.bin");
        AssertRenamed(item, original, "second.bin", status);
    }

    [Theory]
    [InlineData(DownloadStatus.Completed, "second.bin")]
    [InlineData(DownloadStatus.Completed, "FIRST.bin")]
    [InlineData(DownloadStatus.Paused, "second.bin")]
    [InlineData(DownloadStatus.Paused, "FIRST.bin")]
    public void Rename_SuccessMovesSidecarsAndPublishesConsistentIdentity(DownloadStatus status, string name)
    {
        var item = CreateItem();
        item.Status = status;
        string original = item.SaveFilePath!;
        string target = Path.Combine(_root, name);
        int identityChanges = 0;
        item.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName != nameof(item.FileName)) return;
            identityChanges++;
            Assert.Equal(target, item.SaveFilePath);
            AssertFileBytes(target);
        };
        RenameThroughViewModel(item, name);
        Assert.Equal(1, identityChanges);
        AssertRenamed(item, original, name, status);
    }

    [Theory]
    [InlineData(2, DownloadStatus.Paused)]
    [InlineData(3, DownloadStatus.Completed)]
    public void Rename_LaterMoveFailureRestoresAllEarlierMoves(int failure, DownloadStatus status)
    {
        var item = CreateItem();
        item.Status = status;
        string original = item.SaveFilePath!;
        int identityChanges = 0;
        item.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName is nameof(item.FileName) or nameof(item.SaveFilePath)) identityChanges++;
        };
        int calls = 0;
        item.RenameFile = (source, target) =>
        {
            if (++calls == failure) throw new IOException("Injected move failure.");
            File.Move(source, target);
        };
        RenameThroughViewModel(item, "second.bin", Loc.Get("Status_ItemRenameFailed"));
        AssertOriginal(item, original, status);
        Assert.Equal(0, identityChanges);
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(_root), path => Path.GetFileName(path).StartsWith("second"));
        RenameThroughViewModel(item, "second.bin");
        AssertRenamed(item, original, "second.bin", status);
    }

    [Fact]
    public void Rename_IncompleteRollbackPreservesBytesAndCannotRemainVerified()
    {
        var item = CreateItem();
        string original = item.SaveFilePath!;
        int calls = 0;
        item.RenameFile = (source, target) =>
        {
            if (++calls >= 2) throw new IOException("Injected move failure.");
            File.Move(source, target);
        };
        Assert.False(item.Rename("second.bin"));
        Assert.Equal(original, item.SaveFilePath);
        Assert.Equal("first.bin", item.FileName);
        Assert.Equal(DownloadStatus.Failed, item.Status);
        Assert.Null(item.CalculatedChecksum);
        Assert.Equal(Checksum, item.ExpectedChecksum);
        Assert.Equal(Loc.Get("Status_ItemRenameRecoveryRequired"), item.ErrorMessage);
        Assert.Equal("original data", File.ReadAllText(original));
        Assert.Equal("checkpoint data", File.ReadAllText(original + ".part.segments"));
        Assert.Equal("partial data", File.ReadAllText(Path.Combine(_root, "second.bin.part")));
    }

    [Theory]
    [InlineData("active")]
    [InlineData("verification")]
    [InlineData("extraction")]
    [InlineData("collision")]
    public void UserRename_ReportsFailureAndPreservesTheOriginalEntry(string scenario)
    {
        var item = CreateItem();
        string original = item.SaveFilePath!;
        var vm = new MainViewModel();
        vm.Packages.Clear();
        var package = new DownloadPackage { Name = "Sample", SaveDirectory = _root, IsCustomName = true, AutoPar2Repair = false };
        package.Items.Add(item);
        vm.Packages.Add(package);
        if (scenario == "active") item.Status = DownloadStatus.Downloading;
        if (scenario == "verification") item.IsVerifyingIntegrity = true;
        if (scenario == "extraction") package.IsExtracting = true;
        if (scenario == "collision") File.WriteAllText(Path.Combine(_root, "second.bin"), "unrelated data");
        string? notification = null;
        vm.RenameFailureNotification = message => notification = message;
        try
        {
            vm.RenameItemWhenAvailable(item, "second.bin");
            Assert.Equal(Loc.Get(scenario == "collision" ? "Status_ItemRenameFailed" : "Status_ItemRenameBusy"), notification);
            Assert.Equal(notification, vm.StatusSummary);
            Assert.Equal(original, item.SaveFilePath);
            Assert.Equal("first.bin", item.FileName);
            Assert.False(item.IsCustomName);
            Assert.Equal(scenario == "active" ? DownloadStatus.Downloading : DownloadStatus.Completed, item.Status);
            Assert.Equal(Checksum, item.ExpectedChecksum);
            Assert.Equal(scenario == "active" ? null : Checksum, item.CalculatedChecksum);
            AssertFileBytes(original);
            AssertSavedIdentity(item, package);
        }
        finally
        {
            item.IsVerifyingIntegrity = false;
            package.IsExtracting = false;
            vm.Packages.Clear();
        }
    }

    [Fact]
    public void UserRename_UnrelatedActiveSiblingDoesNotBlockCompletedFile()
    {
        var item = CreateItem();
        var vm = new MainViewModel();
        vm.Packages.Clear();
        var package = new DownloadPackage { Name = "Sample", SaveDirectory = _root, IsCustomName = true, AutoPar2Repair = false };
        package.Items.Add(item);
        package.Items.Add(new DownloadItem { FileName = "other.bin", Status = DownloadStatus.Downloading });
        vm.Packages.Add(package);
        vm.RenameFailureNotification = _ => Assert.True(false, "Unexpected rename failure.");
        try
        {
            vm.RenameItemWhenAvailable(item, "second.bin");
            Assert.Equal("second.bin", item.FileName);
            Assert.Equal("original data", File.ReadAllText(item.SaveFilePath!));
        }
        finally { vm.Packages.Clear(); }
    }

    [Theory]
    [InlineData("../outside.bin")]
    [InlineData("..")]
    [InlineData("invalid:name")]
    [InlineData("trailing.")]
    public void Rename_InvalidNameDoesNotChangeIdentity(string name)
    {
        var item = CreateItem();
        string original = item.SaveFilePath!;
        Assert.False(item.Rename(name));
        AssertOriginal(item, original);
    }

    [Fact]
    public void Rename_TargetAppearingAfterValidationIsPreservedAndEarlierMovesAreRestored()
    {
        var item = CreateItem();
        string original = item.SaveFilePath!;
        string occupied = Path.Combine(_root, "second.bin");
        int calls = 0;
        item.RenameFile = (source, target) =>
        {
            if (++calls == 1) File.WriteAllText(occupied, "unrelated data");
            File.Move(source, target);
        };
        RenameThroughViewModel(item, "second.bin", Loc.Get("Status_ItemRenameFailed"));
        AssertOriginal(item, original);
        Assert.Equal("unrelated data", File.ReadAllText(occupied));
    }

    [Fact]
    public void Rename_UnstartedDownloadUpdatesThePlannedPathWithoutCreatingDirectories()
    {
        string directory = Path.Combine(_root, "planned");
        var item = new DownloadItem { FileName = "first.bin", SaveFilePath = Path.Combine(directory, "first.bin") };
        Assert.True(item.Rename("second.bin"));
        Assert.Equal(Path.Combine(directory, "second.bin"), item.SaveFilePath);
        Assert.False(Directory.Exists(directory));
    }

    private DownloadItem CreateItem()
    {
        string path = Path.Combine(_root, "first.bin");
        File.WriteAllText(path, "original data");
        File.WriteAllText(path + ".part", "partial data");
        File.WriteAllText(path + ".part.segments", "checkpoint data");
        return new DownloadItem
        {
            FileName = "first.bin", SaveFilePath = path, Status = DownloadStatus.Completed,
            ExpectedChecksum = Checksum, CalculatedChecksum = Checksum, TotalBytes = 13, DownloadedBytes = 13
        };
    }

    private static void AssertOriginal(DownloadItem item, string original, DownloadStatus status = DownloadStatus.Completed)
    {
        Assert.Equal("first.bin", item.FileName);
        Assert.Equal(original, item.SaveFilePath);
        Assert.False(item.IsCustomName);
        Assert.Equal(status, item.Status);
        Assert.Equal(Checksum, item.ExpectedChecksum);
        Assert.Equal(Checksum, item.CalculatedChecksum);
        Assert.Equal(13, item.DownloadedBytes);
        AssertFileBytes(original);
    }

    private static void AssertFileBytes(string path)
    {
        Assert.Equal(Encoding.UTF8.GetBytes("original data"), File.ReadAllBytes(path));
        Assert.Equal(Encoding.UTF8.GetBytes("partial data"), File.ReadAllBytes(path + ".part"));
        Assert.Equal(Encoding.UTF8.GetBytes("checkpoint data"), File.ReadAllBytes(path + ".part.segments"));
    }

    private void AssertRenamed(DownloadItem item, string original, string name, DownloadStatus status)
    {
        string target = Path.Combine(_root, name);
        Assert.Equal(target, item.SaveFilePath);
        Assert.Equal(name, item.FileName);
        Assert.True(item.IsCustomName);
        Assert.Equal(status, item.Status);
        Assert.Equal(Checksum, item.ExpectedChecksum);
        Assert.Equal(Checksum, item.CalculatedChecksum);
        Assert.Equal(13, item.DownloadedBytes);
        AssertFileBytes(target);
        Assert.Equal(item.CalculatedChecksum, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))));
        foreach (string suffix in new[] { "", ".part", ".part.segments" })
        {
            Assert.Contains(Directory.EnumerateFiles(_root), path => string.Equals(path, target + suffix, StringComparison.Ordinal));
            Assert.DoesNotContain(Directory.EnumerateFiles(_root), path => string.Equals(path, original + suffix, StringComparison.Ordinal));
        }
    }

    internal static void RenameThroughViewModel(DownloadItem item, string name, string? expectedFailure = null)
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();
        var package = new DownloadPackage { Name = "Sample", SaveDirectory = Path.GetDirectoryName(item.SaveFilePath)!, IsCustomName = true, AutoPar2Repair = false };
        package.Items.Add(item);
        vm.Packages.Add(package);
        string? notification = null;
        vm.RenameFailureNotification = message => notification = message;
        try
        {
            vm.RenameItemWhenAvailable(item, name);
            Assert.Equal(expectedFailure, notification);
            if (expectedFailure != null) Assert.Equal(expectedFailure, vm.StatusSummary);
            AssertSavedIdentity(item, package);
        }
        finally { vm.Packages.Clear(); }
    }

    private static void AssertSavedIdentity(DownloadItem item, DownloadPackage package)
    {
        var saved = Assert.Single(Assert.Single(DownloadPersistenceService.SnapshotDtos(new[] { package })).Items);
        Assert.Equal(item.Id, saved.Id);
        Assert.Equal(item.FileName, saved.FileName);
        Assert.Equal(item.SaveFilePath, saved.SaveFilePath);
        Assert.Equal(item.IsCustomName, saved.IsCustomName);
        Assert.Equal(item.IsReadyForPostProcessing ? DownloadStatus.Completed : DownloadStatus.Paused, saved.Status);
        Assert.Equal(item.ExpectedChecksum, saved.ExpectedChecksum);
        Assert.Equal(item.CalculatedChecksum, saved.CalculatedChecksum);

        var directory = Directory.CreateTempSubdirectory("ItemRenameState_");
        var persistence = new DownloadPersistenceService(Path.Combine(directory.FullName, "downloads.json"));
        try
        {
            persistence.SaveDownloads(new[] { package }, sync: true);
            Assert.True(File.Exists(Path.Combine(directory.FullName, "downloads.json")));
            var restored = Assert.Single(Assert.Single(persistence.LoadDownloads()).Items);
            Assert.False(persistence.HasDownloadsLoadFailed);
            Assert.Equal(saved.Id, restored.Id);
            Assert.Equal(saved.FileName, restored.FileName);
            Assert.Equal(saved.SaveFilePath, restored.SaveFilePath);
            Assert.Equal(saved.IsCustomName, restored.IsCustomName);
            Assert.Equal(saved.Status, restored.Status);
            Assert.Equal(saved.DownloadedBytes, restored.DownloadedBytes);
            Assert.Equal(saved.ExpectedChecksum, restored.ExpectedChecksum);
            Assert.Equal(saved.CalculatedChecksum, restored.CalculatedChecksum);
        }
        finally
        {
            persistence.Dispose();
            string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(directory.Parent?.FullName, parent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected temporary directory.");
            directory.Delete(recursive: true);
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
