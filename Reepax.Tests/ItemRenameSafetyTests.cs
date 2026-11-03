using System.IO;
using Reepax.Models;
using Reepax.Services.Localization;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

public sealed class ItemRenameSafetyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ItemRename_").FullName;

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
        Assert.False(item.Rename("second.bin"));
        AssertOriginal(item, original);
        if (!directory) Assert.Equal("unrelated data", File.ReadAllText(target + suffix));
    }

    [Fact]
    public void Rename_LockedSourcePreservesModelAndCanBeRetried()
    {
        var item = CreateItem();
        string original = item.SaveFilePath!;
        using (File.Open(original, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(item.Rename("second.bin"));
            AssertOriginal(item, original);
        }
        Assert.True(item.Rename("second.bin"));
        Assert.Equal("second.bin", item.FileName);
        Assert.Equal("original data", File.ReadAllText(item.SaveFilePath!));
    }

    [Theory]
    [InlineData(DownloadStatus.Completed, "second.bin")]
    [InlineData(DownloadStatus.Paused, "second.bin")]
    [InlineData(DownloadStatus.Paused, "FIRST.bin")]
    public void Rename_SuccessMovesSidecarsAndPublishesConsistentIdentity(DownloadStatus status, string name)
    {
        var item = CreateItem();
        item.Status = status;
        string original = item.SaveFilePath!;
        string target = Path.Combine(_root, name);
        item.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName != nameof(item.FileName)) return;
            Assert.Equal(target, item.SaveFilePath);
            Assert.Equal("original data", File.ReadAllText(target));
            Assert.Equal("partial data", File.ReadAllText(target + ".part"));
            Assert.Equal("checkpoint data", File.ReadAllText(target + ".part.segments"));
        };
        Assert.True(item.Rename(name));
        Assert.Equal(name, item.FileName);
        Assert.True(item.IsCustomName);
        Assert.Equal(status, item.Status);
        Assert.Equal("verified fingerprint", item.CalculatedChecksum);
        Assert.Equal(13, item.DownloadedBytes);
        if (name == "second.bin") Assert.False(File.Exists(original));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Rename_LaterMoveFailureRestoresAllEarlierMoves(int failure)
    {
        var item = CreateItem();
        string original = item.SaveFilePath!;
        int calls = 0;
        item.RenameFile = (source, target) =>
        {
            if (++calls == failure) throw new IOException("Injected move failure.");
            File.Move(source, target);
        };
        Assert.False(item.Rename("second.bin"));
        AssertOriginal(item, original);
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(_root), path => Path.GetFileName(path).StartsWith("second"));
        Assert.True(item.Rename("second.bin"));
        Assert.Equal("original data", File.ReadAllText(item.SaveFilePath!));
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
            Assert.Equal("original data", File.ReadAllText(original));
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
        Assert.False(item.Rename("second.bin"));
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
            CalculatedChecksum = "verified fingerprint", DownloadedBytes = 13
        };
    }

    private static void AssertOriginal(DownloadItem item, string original)
    {
        Assert.Equal("first.bin", item.FileName);
        Assert.Equal(original, item.SaveFilePath);
        Assert.False(item.IsCustomName);
        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal("verified fingerprint", item.CalculatedChecksum);
        Assert.Equal(13, item.DownloadedBytes);
        Assert.Equal("original data", File.ReadAllText(original));
        Assert.Equal("partial data", File.ReadAllText(original + ".part"));
        Assert.Equal("checkpoint data", File.ReadAllText(original + ".part.segments"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
