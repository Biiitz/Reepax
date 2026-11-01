using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Extractor;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Xunit;

namespace Reepax.Tests;

public sealed class ArchiveRecycleSafetyTests : IDisposable
{
    private readonly ArchiveRecycleTestScope _recycle = new();
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("ArchiveRecycleSafety_");
    private readonly DownloadPackage[] _previousPackages = QueueManager.Instance.Packages.ToArray();
    private readonly string _language = LocalizationService.Instance.CurrentLanguage;
    private readonly bool _deleteArchives = SettingsService.Instance.Settings.DeleteArchiveAfterExtraction;
    private readonly bool _recycleArchives = SettingsService.Instance.Settings.MoveArchiveToRecycleBin;
    private readonly bool _deletePar2 = SettingsService.Instance.Settings.DeletePar2AfterExtraction;

    public ArchiveRecycleSafetyTests()
    {
        DownloadPersistenceService.IsTestEnvironment = true;
        QueueManager.Instance.Packages.Clear();
        var settings = SettingsService.Instance.Settings;
        settings.DeleteArchiveAfterExtraction = false;
        settings.MoveArchiveToRecycleBin = false;
        settings.DeletePar2AfterExtraction = false;
    }

    [Theory]
    [InlineData(false, "exception")]
    [InlineData(false, "cancel")]
    [InlineData(false, "no_op")]
    [InlineData(false, "shell_error")]
    [InlineData(true, "exception")]
    [InlineData(true, "cancel")]
    [InlineData(true, "no_op")]
    [InlineData(true, "shell_error")]
    public void RecycleFailure_PreservesFilesAndReportsFailure(bool directory, string outcome)
    {
        string source = Path.Combine(_root.FullName, "Source");
        Directory.CreateDirectory(source);
        string file = Path.Combine(source, "archive.rar");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4 });
        File.WriteAllText(Path.Combine(source, "payload.bak"), "user backup");
        File.WriteAllBytes(Path.Combine(source, "empty.tmp"), Array.Empty<byte>());
        var before = Directory.GetFiles(source).ToDictionary(path => path, File.ReadAllBytes);
        int attempts = 0;
        Exception? failure = outcome switch
        {
            "exception" => new IOException("Recycling unavailable."),
            "cancel" => new OperationCanceledException("Recycling cancelled."),
            "shell_error" => new COMException("Recycling failed.", unchecked((int)0x80070005)),
            _ => null
        };
        ArchiveExtractionService.Instance.RecyclePaths = _ =>
        {
            attempts++;
            if (failure != null) throw failure;
        };
        var reported = Record.Exception(() => ArchiveExtractionService.DeleteToRecycleBin(directory ? source : file));
        Assert.NotNull(reported);
        if (failure != null) Assert.Same(failure, reported);
        else Assert.IsType<IOException>(reported);
        Assert.Equal(1, attempts);
        foreach (var pair in before) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
        Assert.Empty(_recycle.RecycledPaths);
    }

    [Theory]
    [InlineData("archive.rar", false)]
    [InlineData("payload.bak", false)]
    [InlineData("empty.tmp", true)]
    public void SuccessfulRecycle_PreservesContentsInBackend(string name, bool empty)
    {
        string source = Path.Combine(_root.FullName, name);
        byte[] data = empty ? Array.Empty<byte>() : new byte[] { 4, 3, 2, 1 };
        File.WriteAllBytes(source, data);
        ArchiveExtractionService.DeleteToRecycleBin(source);
        Assert.False(File.Exists(source));
        Assert.Equal(data, File.ReadAllBytes(Assert.Single(_recycle.RecycledPaths)));
    }

    [Fact]
    public void RecyclingDirectory_DoesNotPurgeItsTemporaryLookingPayload()
    {
        string source = Path.Combine(_root.FullName, "Source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "payload.bak"), "backup content");
        File.WriteAllText(Path.Combine(source, "payload.part"), "partial content");
        ArchiveExtractionService.DeleteToRecycleBin(source);
        string recycled = Assert.Single(_recycle.RecycledPaths);
        Assert.False(Directory.Exists(source));
        Assert.Equal("backup content", File.ReadAllText(Path.Combine(recycled, "payload.bak")));
        Assert.Equal("partial content", File.ReadAllText(Path.Combine(recycled, "payload.part")));
    }

    [Fact]
    public void MissingPathsAndProtectedRoots_DoNotReachBackend()
    {
        int attempts = 0;
        ArchiveExtractionService.Instance.RecyclePaths = _ => attempts++;
        ArchiveExtractionService.DeleteToRecycleBin(string.Empty);
        ArchiveExtractionService.DeleteToRecycleBin(Path.Combine(_root.FullName, "missing.rar"));
        Assert.Throws<IOException>(() => ArchiveExtractionService.DeleteToRecycleBin(Path.GetPathRoot(_root.FullName)!));
        Assert.Equal(0, attempts);
    }

    [Theory]
    [InlineData("de", false, false)]
    [InlineData("en", false, false)]
    [InlineData("de", true, false)]
    [InlineData("en", true, false)]
    [InlineData("de", false, true)]
    [InlineData("en", false, true)]
    public async Task PackageRecycleFailure_KeepsExtractionSuccessfulAndMarksCleanupFailed(string language, bool global, bool manual)
    {
        LocalizationService.Instance.CurrentLanguage = language;
        var package = CreatePackage();
        package.AutoExtractArchives = !manual;
        package.MoveArchiveToRecycleBin = !global;
        SettingsService.Instance.Settings.MoveArchiveToRecycleBin = global;
        string archivePath = package.Items[0].SaveFilePath!;
        byte[] original = File.ReadAllBytes(archivePath);
        ArchiveExtractionService.Instance.RecyclePaths = _ => throw new IOException("Recycling unavailable.");
        Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package, force: manual));
        Assert.True(package.IsExtracted);
        Assert.Equal(original, File.ReadAllBytes(archivePath));
        Assert.Equal("archive payload", File.ReadAllText(Path.Combine(package.SaveDirectory, "payload.txt")));
        if (!manual) Assert.Equal(NextTaskStepState.Done, package.NextTaskSteps.Single(step => step.Key == "Extract").State);
        Assert.Equal(NextTaskStepState.Failed, package.NextTaskSteps.Single(step => step.Key == "Cleanup").State);
        Assert.Equal(Loc.Get("Status_ArchiveRecycleFailed"), package.StatusMessage);
        if (!manual) Assert.False(package.CheckIsFullyCompleted());
        package.RecalculateAggregates(force: true);
        Assert.Equal(Loc.Get("Status_ArchiveRecycleFailed"), package.StatusMessage);
    }

    [Fact]
    public async Task SuccessfulPackageRecycle_MovesArchiveWithoutLosingItsContents()
    {
        var package = CreatePackage();
        package.MoveArchiveToRecycleBin = true;
        string archivePath = package.Items[0].SaveFilePath!;
        byte[] original = File.ReadAllBytes(archivePath);
        Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package));
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(_recycle.RecycledPaths)));
        Assert.False(File.Exists(archivePath));
        Assert.Equal(NextTaskStepState.Done, package.NextTaskSteps.Single(step => step.Key == "Cleanup").State);
        Assert.True(package.CheckIsFullyCompleted());
    }

    [Fact]
    public async Task ExplicitPermanentDeletion_DoesNotInvokeRecycleBackend()
    {
        var package = CreatePackage();
        package.DeleteArchiveAfterExtraction = true;
        SettingsService.Instance.Settings.MoveArchiveToRecycleBin = true;
        ArchiveExtractionService.Instance.RecyclePaths = _ => throw new InvalidOperationException("Recycling was not requested.");
        Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package));
        Assert.False(File.Exists(package.Items[0].SaveFilePath));
        Assert.Equal("archive payload", File.ReadAllText(Path.Combine(package.SaveDirectory, "payload.txt")));
        Assert.Equal(NextTaskStepState.Done, package.NextTaskSteps.Single(step => step.Key == "Cleanup").State);
    }

    private DownloadPackage CreatePackage()
    {
        string directory = Path.Combine(_root.FullName, "Archive");
        Directory.CreateDirectory(directory);
        string archivePath = Path.Combine(directory, "archive.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("payload.txt").Open())) writer.Write("archive payload");
        var package = new DownloadPackage
        {
            Name = "Archive", IsCustomName = true, SaveDirectory = directory, AutoExtractArchives = true,
            AutoPar2Repair = false, HasCompletedNotified = true
        };
        package.Items.Add(new DownloadItem
        {
            FileName = "archive.zip", SaveFilePath = archivePath, Status = DownloadStatus.Completed,
            TotalBytes = new FileInfo(archivePath).Length, DownloadedBytes = new FileInfo(archivePath).Length
        });
        QueueManager.Instance.Packages.Add(package);
        return package;
    }

    public void Dispose()
    {
        _recycle.Dispose();
        QueueManager.Instance.Packages.Clear();
        foreach (var package in _previousPackages) QueueManager.Instance.Packages.Add(package);
        LocalizationService.Instance.CurrentLanguage = _language;
        var settings = SettingsService.Instance.Settings;
        settings.DeleteArchiveAfterExtraction = _deleteArchives;
        settings.MoveArchiveToRecycleBin = _recycleArchives;
        settings.DeletePar2AfterExtraction = _deletePar2;
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(_root.Parent?.FullName, temporaryRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected temporary directory.");
        _root.Delete(recursive: true);
    }
}
