using System.IO;
using System.IO.Compression;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Extractor;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

public sealed class ArchiveCleanupRetryTests : IDisposable
{
    private readonly ArchiveRecycleTestScope _recycle = new();
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("ArchiveCleanupRetry_");
    private readonly DownloadPackage[] _previousPackages = QueueManager.Instance.Packages.ToArray();
    private readonly bool _deleteArchives = SettingsService.Instance.Settings.DeleteArchiveAfterExtraction;
    private readonly bool _recycleArchives = SettingsService.Instance.Settings.MoveArchiveToRecycleBin;
    private readonly bool _deletePar2 = SettingsService.Instance.Settings.DeletePar2AfterExtraction;
    private readonly string _language = LocalizationService.Instance.CurrentLanguage;

    public ArchiveCleanupRetryTests()
    {
        DownloadPersistenceService.IsTestEnvironment = true;
        QueueManager.Instance.Packages.Clear();
        var settings = SettingsService.Instance.Settings;
        settings.DeleteArchiveAfterExtraction = false;
        settings.MoveArchiveToRecycleBin = false;
        settings.DeletePar2AfterExtraction = true;
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("part")]
    [InlineData("checkpoint")]
    [InlineData("par2")]
    public async Task FailedDeletion_RemainsVisibleAndRetriesWithoutExtractingAgain(string lockedKind)
    {
        var package = CreatePackage();
        string archive = package.Items[0].SaveFilePath!;
        string lockedPath = lockedKind switch
        {
            "part" => archive + ".part",
            "checkpoint" => archive + ".part.segments",
            "par2" => package.Items[1].SaveFilePath!,
            _ => archive
        };
        byte[] original = File.ReadAllBytes(lockedPath);
        using (var handle = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package));
            Assert.True(package.IsExtracted);
            Assert.Equal(NextTaskStepState.Done, package.NextTaskSteps.Single(step => step.Key == "Extract").State);
            Assert.Equal(NextTaskStepState.Failed, package.NextTaskSteps.Single(step => step.Key == "Cleanup").State);
            Assert.False(package.CheckIsFullyCompleted());
            Assert.Equal(original, File.ReadAllBytes(lockedPath));
        }
        string output = Path.Combine(package.SaveDirectory, "payload.txt");
        File.WriteAllText(output, "user changed extracted content");
        Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package, force: true));
        Assert.False(File.Exists(lockedPath));
        Assert.Equal("user changed extracted content", File.ReadAllText(output));
        Assert.Equal(NextTaskStepState.Done, package.NextTaskSteps.Single(step => step.Key == "Cleanup").State);
        Assert.True(package.CheckIsFullyCompleted());
    }

    [Theory]
    [InlineData("de", false)]
    [InlineData("en", true)]
    public async Task PendingCleanup_SurvivesReloadAndPreservesExtraction(string language, bool history)
    {
        LocalizationService.Instance.CurrentLanguage = language;
        var package = CreatePackage();
        string archive = package.Items[0].SaveFilePath!;
        using (var locked = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package));
        using var storage = new DownloadPersistenceService(Path.Combine(_root.FullName, "downloads.json"));
        if (history) storage.SaveHistory(new[] { package }, sync: true);
        else storage.SaveDownloads(new[] { package }, sync: true);
        QueueManager.Instance.Packages.Clear();
        var restored = Assert.Single(history ? storage.LoadHistory() : storage.LoadDownloads());
        QueueManager.Instance.Packages.Add(restored);
        Assert.True(restored.IsExtracted);
        Assert.True(restored.HasPendingArchiveCleanup);
        Assert.Equal(NextTaskStepState.Failed, restored.NextTaskSteps.Single(step => step.Key == "Cleanup").State);
        Assert.Equal(Loc.Format("Status_ArchiveCleanupFailed", restored.PendingArchiveCleanup!.Files.Length), restored.StatusMessage);
        string output = Path.Combine(restored.SaveDirectory, "payload.txt");
        File.WriteAllText(output, "preserved after reload");
        await new MainViewModel().RetryArchiveCleanupAsync(restored);
        Assert.False(File.Exists(archive));
        Assert.Equal("preserved after reload", File.ReadAllText(output));
        Assert.False(restored.HasPendingArchiveCleanup);
        Assert.Equal(NextTaskStepState.Done, restored.NextTaskSteps.Single(step => step.Key == "Cleanup").State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingCleanup_FollowsVerifiedPackageMoves(bool copy)
    {
        var package = CreatePackage();
        string archive = package.Items[0].SaveFilePath!;
        using (var locked = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package));
        var plan = Assert.Single(PackageDirectoryMoveService.Prepare(new[] { package }, new[] { package }, Path.Combine(_root.FullName, "Moved")));
        Assert.True(await new PackageDirectoryMoveService(allowDirectoryRename: !copy).ExecuteAsync(plan));
        package.SaveDirectory = plan.Target;
        foreach (var pair in plan.ItemPaths) pair.Key.SaveFilePath = pair.Value;
        package.ExtractionDirectory = plan.ExtractionDirectory;
        string movedOutput = Path.Combine(package.SaveDirectory, "payload.txt");
        File.WriteAllText(movedOutput, "preserved after move");
        Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package, force: true));
        Assert.False(File.Exists(package.Items[0].SaveFilePath));
        Assert.False(File.Exists(archive));
        Assert.False(package.HasPendingArchiveCleanup);
        Assert.Equal("preserved after move", File.ReadAllText(movedOutput));
    }

    [Fact]
    public async Task ChangedPendingFile_IsRetainedUntilConflictIsResolved()
    {
        var package = CreatePackage();
        string archive = package.Items[0].SaveFilePath!;
        using (var locked = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package));
        File.WriteAllText(archive, "replacement user data");
        File.SetLastWriteTimeUtc(archive, DateTime.UtcNow.AddMinutes(1));
        Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package, force: true));
        Assert.Equal("replacement user data", File.ReadAllText(archive));
        Assert.True(package.HasPendingArchiveCleanup);
        Assert.Equal(NextTaskStepState.Failed, package.NextTaskSteps.Single(step => step.Key == "Cleanup").State);
        File.Move(archive, Path.Combine(_root.FullName, "preserved.bin"));
        Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package, force: true));
        Assert.False(package.HasPendingArchiveCleanup);
        Assert.Equal("replacement user data", File.ReadAllText(Path.Combine(_root.FullName, "preserved.bin")));
    }

    [Fact]
    public async Task RecycleRetry_DoesNotAdoptAChangedPermanentDeletionSetting()
    {
        var package = CreatePackage();
        package.DeleteArchiveAfterExtraction = false;
        package.MoveArchiveToRecycleBin = true;
        var backend = ArchiveExtractionService.Instance.RecyclePaths;
        ArchiveExtractionService.Instance.RecyclePaths = _ => throw new IOException("Recycle unavailable.");
        Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package));
        string archive = package.Items[0].SaveFilePath!;
        byte[] original = File.ReadAllBytes(archive);
        package.DeleteArchiveAfterExtraction = true;
        SettingsService.Instance.Settings.DeleteArchiveAfterExtraction = true;
        Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package, force: true));
        Assert.Equal(original, File.ReadAllBytes(archive));
        Assert.True(package.HasPendingArchiveCleanup);
        ArchiveExtractionService.Instance.RecyclePaths = backend;
        Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package, force: true));
        Assert.False(File.Exists(archive));
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(_recycle.RecycledPaths)));
        Assert.False(package.HasPendingArchiveCleanup);
    }

    [Fact]
    public async Task NewOwnerOfPendingPath_IsProtectedDuringRetry()
    {
        var package = CreatePackage();
        string archive = package.Items[0].SaveFilePath!;
        using (var locked = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package));
        byte[] original = File.ReadAllBytes(archive);
        var other = new DownloadPackage { Name = "Other", IsCustomName = true, AutoPar2Repair = false };
        other.Items.Add(new DownloadItem { FileName = "archive.zip", SaveFilePath = archive, Status = DownloadStatus.Paused });
        QueueManager.Instance.Packages.Add(other);
        Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package, force: true));
        Assert.Equal(original, File.ReadAllBytes(archive));
        Assert.True(package.HasPendingArchiveCleanup);
        QueueManager.Instance.Packages.Remove(other);
        Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package, force: true));
        Assert.False(File.Exists(archive));
    }

    [Fact]
    public async Task CancelledCleanup_RemainsRetryableWithoutUndoingExtraction()
    {
        var package = CreatePackage();
        package.DeleteArchiveAfterExtraction = false;
        package.MoveArchiveToRecycleBin = true;
        using var cancellation = new CancellationTokenSource();
        var backend = ArchiveExtractionService.Instance.RecyclePaths;
        ArchiveExtractionService.Instance.RecyclePaths = paths =>
        {
            backend(paths);
            cancellation.Cancel();
        };
        Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package, cancellation.Token));
        Assert.True(package.IsExtracted);
        Assert.True(package.HasPendingArchiveCleanup);
        Assert.Equal(NextTaskStepState.Done, package.NextTaskSteps.Single(step => step.Key == "Extract").State);
        Assert.Equal(NextTaskStepState.Failed, package.NextTaskSteps.Single(step => step.Key == "Cleanup").State);
        string output = Path.Combine(package.SaveDirectory, "payload.txt");
        File.WriteAllText(output, "preserved after cancellation");
        ArchiveExtractionService.Instance.RecyclePaths = backend;
        Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package, force: true));
        Assert.False(package.HasPendingArchiveCleanup);
        Assert.Equal("preserved after cancellation", File.ReadAllText(output));
    }

    [Theory]
    [InlineData("de", false)]
    [InlineData("en", true)]
    public async Task ManualOrGlobalCleanupFailure_UsesLocalizedStatusAndRemainsVisible(string language, bool global)
    {
        LocalizationService.Instance.CurrentLanguage = language;
        var package = CreatePackage();
        package.AutoExtractArchives = false;
        package.DeleteArchiveAfterExtraction = !global;
        SettingsService.Instance.Settings.DeleteArchiveAfterExtraction = global;
        string archive = package.Items[0].SaveFilePath!;
        using (var locked = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package, force: true));
        Assert.True(package.IsExtracted);
        Assert.True(package.HasPendingArchiveCleanup);
        Assert.False(package.CheckIsFullyCompleted());
        Assert.Equal(Loc.Format("Status_ArchiveCleanupFailed", package.PendingArchiveCleanup!.Files.Length), package.StatusMessage);
        package.RecalculateAggregates(force: true);
        Assert.Equal(NextTaskStepState.Failed, package.NextTaskSteps.Single(step => step.Key == "Cleanup").State);
        Assert.Equal(Loc.Format("Status_ArchiveCleanupFailed", package.PendingArchiveCleanup.Files.Length), package.StatusMessage);
        await new MainViewModel().RetryArchiveCleanupAsync(package);
        Assert.False(package.HasPendingArchiveCleanup);
        Assert.Equal(Loc.Get("Status_CompletedAndExtracted"), package.StatusMessage);
    }

    private DownloadPackage CreatePackage()
    {
        string directory = Path.Combine(_root.FullName, "Archive");
        Directory.CreateDirectory(directory);
        string archive = Path.Combine(directory, "archive.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("payload.txt").Open())) writer.Write("archive payload");
        string par2 = Path.Combine(directory, "archive.par2");
        File.WriteAllText(par2, "recovery input");
        var package = new DownloadPackage
        {
            Name = "Archive", IsCustomName = true, SaveDirectory = directory, AutoExtractArchives = true,
            AutoPar2Repair = false, DeleteArchiveAfterExtraction = true, HasCompletedNotified = true
        };
        foreach (string path in new[] { archive, par2 }) package.Items.Add(new DownloadItem
        {
            FileName = Path.GetFileName(path), SaveFilePath = path, Status = DownloadStatus.Completed,
            TotalBytes = new FileInfo(path).Length, DownloadedBytes = new FileInfo(path).Length
        });
        ArchiveCleanupSafetyTests.WriteDownloadSidecars(package.Items[0]);
        QueueManager.Instance.Packages.Add(package);
        return package;
    }

    public void Dispose()
    {
        _recycle.Dispose();
        QueueManager.Instance.Packages.Clear();
        foreach (var package in _previousPackages) QueueManager.Instance.Packages.Add(package);
        var settings = SettingsService.Instance.Settings;
        settings.DeleteArchiveAfterExtraction = _deleteArchives;
        settings.MoveArchiveToRecycleBin = _recycleArchives;
        settings.DeletePar2AfterExtraction = _deletePar2;
        LocalizationService.Instance.CurrentLanguage = _language;
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(_root.Parent?.FullName, temporaryRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected temporary directory.");
        _root.Delete(recursive: true);
    }
}
