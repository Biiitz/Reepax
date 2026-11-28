using System.IO;
using System.IO.Compression;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Extractor;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Xunit;

namespace Reepax.Tests;

public sealed class PackageExtractionDestinationTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("Reepax_ExtractionDestinations_");
    private readonly List<DownloadPackage> _registered = [];
    private readonly bool _deleteArchives = SettingsService.Instance.Settings.DeleteArchiveAfterExtraction;
    private readonly bool _recycleArchives = SettingsService.Instance.Settings.MoveArchiveToRecycleBin;

    public PackageExtractionDestinationTests()
    {
        SettingsService.Instance.Settings.DeleteArchiveAfterExtraction = false;
        SettingsService.Instance.Settings.MoveArchiveToRecycleBin = false;
    }

    [Theory]
    [InlineData("", "", false)]
    [InlineData("nested", ".part", false)]
    [InlineData("nested", ".part.segments", true)]
    public async Task ExtractionConflict_PreservesAnotherPackagesDownload(string subdirectory, string suffix, bool registerDuringExtraction)
    {
        string directory = Path.Combine(_root.FullName, subdirectory);
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, "payload.bin");
        string protectedFile = destination + suffix;
        byte[] original = [5, 4, 3, 2, 1];
        File.WriteAllBytes(protectedFile, original);
        var download = new DownloadItem
        {
            FileName = "payload.bin", SaveFilePath = destination,
            Status = suffix.Length == 0 ? DownloadStatus.Completed : DownloadStatus.Paused,
            DownloadedBytes = original.Length
        };
        var other = new DownloadPackage { Name = "Download package", SaveDirectory = directory };
        other.Items.Add(download);

        string entry = Path.GetRelativePath(_root.FullName, protectedFile).Replace('\\', '/');
        var package = CreateArchivePackage("sample.zip", entry, [9, 8, 7]);
        package.DeleteArchiveAfterExtraction = true;
        if (registerDuringExtraction)
        {
            package.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName != nameof(DownloadPackage.ExtractionDirectory)) return;
                Assert.True(PackagePathOperationGate.Instance.TryEnter(out var lease));
                using (lease) Register(other);
            };
        }
        else Register(other);

        string archive = package.Items[0].SaveFilePath!;
        byte[] archiveBytes = File.ReadAllBytes(archive);
        var originalStatus = download.Status;
        bool success = await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package);

        Assert.Contains(other, QueueManager.Instance.Packages);
        Assert.Equal(original, File.ReadAllBytes(protectedFile));
        Assert.Equal(originalStatus, download.Status);
        Assert.Equal(original.Length, download.DownloadedBytes);
        Assert.Equal(archiveBytes, File.ReadAllBytes(archive));
        Assert.False(success);
        Assert.False(package.IsExtracted);
        Assert.Empty(package.ExtractedFiles);
        Assert.Null(package.PendingArchiveCleanup);
        Assert.False(File.Exists(Path.Combine(_root.FullName, "extra.txt")));
        Assert.Equal(Loc.Format("Status_ExtractionFailed", Loc.Get("Extraction_TargetReserved")), package.StatusMessage);
        Assert.Empty(Directory.GetDirectories(_root.FullName, ".archive-work-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ExtractionConflict_PreservesAnotherPackagesOutputAndAllowsItsOwnReextraction()
    {
        byte[] original = [5, 4, 3, 2, 1];
        var owner = CreateArchivePackage("owner.zip", "payload.bin", original);
        var extractor = ArchiveExtractionService.Instance;
        Assert.True(await extractor.CheckAndExtractPackageAsync(owner));
        var ownedFiles = owner.ExtractedFiles;
        string output = Path.Combine(_root.FullName, "payload.bin");
        Assert.Contains(ownedFiles, file => file.Path == output);

        var other = CreateArchivePackage("other.zip", "PAYLOAD.BIN", [9, 8, 7], includeExtraFile: false);
        bool success = await extractor.CheckAndExtractPackageAsync(other, force: true);
        Assert.Equal(original, File.ReadAllBytes(output));
        Assert.False(success);
        Assert.False(other.IsExtracted);
        Assert.Empty(other.ExtractedFiles);
        Assert.Same(ownedFiles, owner.ExtractedFiles);

        File.WriteAllBytes(output, [1, 1]);
        Assert.True(await extractor.CheckAndExtractPackageAsync(owner, force: true, forceReextract: true));
        Assert.Equal(original, File.ReadAllBytes(output));
        Assert.True(owner.IsExtracted);
        Assert.Empty(Directory.GetDirectories(_root.FullName, ".archive-work-*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("reserved.bin", "reserved.bin")]
    [InlineData("reserved.bin", "reserved.bin/content.txt")]
    [InlineData("reserved.bin/content.txt", "reserved.bin")]
    public async Task ExtractionConflict_LeavesUnusedDownloadReservationAvailable(string downloadPath, string entryName)
    {
        string reservedFile = Path.Combine(_root.FullName, downloadPath);
        var other = new DownloadPackage
        {
            Name = "Queued package", SaveDirectory = Path.GetDirectoryName(reservedFile)!
        };
        other.Items.Add(new DownloadItem { FileName = Path.GetFileName(reservedFile), Status = DownloadStatus.Queued });
        Register(other);

        var package = CreateArchivePackage("sample.zip", entryName, [1, 2, 3]);
        Assert.False(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package));
        Assert.False(Path.Exists(Path.Combine(_root.FullName, "reserved.bin")));
        Assert.False(File.Exists(Path.Combine(_root.FullName, "extra.txt")));
        Assert.Empty(package.ExtractedFiles);
        Assert.Empty(Directory.GetDirectories(_root.FullName, ".archive-work-*", SearchOption.AllDirectories));
    }

    private DownloadPackage CreateArchivePackage(string name, string entryName, byte[] bytes, bool includeExtraFile = true)
    {
        string path = Path.Combine(_root.FullName, name);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            if (includeExtraFile)
            {
                using var extra = zip.CreateEntry("extra.txt").Open();
                extra.WriteByte(42);
            }
            using var entry = zip.CreateEntry(entryName).Open();
            entry.Write(bytes);
        }
        var package = new DownloadPackage
        {
            Name = name, SaveDirectory = _root.FullName, AutoExtractArchives = true, AutoPar2Repair = false
        };
        package.Items.Add(new DownloadItem { FileName = name, SaveFilePath = path, Status = DownloadStatus.Completed });
        Register(package);
        return package;
    }

    private void Register(DownloadPackage package)
    {
        QueueManager.Instance.Packages.Add(package);
        _registered.Add(package);
    }

    public void Dispose()
    {
        foreach (var package in _registered) QueueManager.Instance.Packages.Remove(package);
        SettingsService.Instance.Settings.DeleteArchiveAfterExtraction = _deleteArchives;
        SettingsService.Instance.Settings.MoveArchiveToRecycleBin = _recycleArchives;
        _root.Delete(recursive: true);
    }
}
