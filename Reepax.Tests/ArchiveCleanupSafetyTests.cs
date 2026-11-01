using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Extractor;
using Reepax.Services.Storage;
using Xunit;

namespace Reepax.Tests;

public sealed class ArchiveCleanupSafetyTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("ArchiveCleanup_");
    private readonly DownloadPackage[] _previousPackages = QueueManager.Instance.Packages.ToArray();
    private readonly bool _deleteArchives = SettingsService.Instance.Settings.DeleteArchiveAfterExtraction;
    private readonly bool _recycleArchives = SettingsService.Instance.Settings.MoveArchiveToRecycleBin;
    private readonly bool _deletePar2 = SettingsService.Instance.Settings.DeletePar2AfterExtraction;

    public ArchiveCleanupSafetyTests()
    {
        DownloadPersistenceService.IsTestEnvironment = true;
        QueueManager.Instance.Packages.Clear();
        SettingsService.Instance.Settings.DeleteArchiveAfterExtraction = false;
        SettingsService.Instance.Settings.MoveArchiveToRecycleBin = false;
        SettingsService.Instance.Settings.DeletePar2AfterExtraction = true;
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task PackageCleanup_PreservesPayloadAndExistingFiles(bool manual, bool recycle, bool shared)
    {
        string directory = Path.Combine(_root.FullName, "Archive");
        Directory.CreateDirectory(directory);
        string archivePath = Path.Combine(directory, "archive.zip");
        var payload = new[]
        {
            "payload.bak", "data.cache", "notes.log", "payload.tmp", "state.segments", "payload.par2",
            "archive.zip.part", "archive.zip.part.segments", "archive.zip.tmp", "nested/payload.cache", "keep.txt"
        }.ToDictionary(name => name, name => System.Text.Encoding.UTF8.GetBytes("Archive content: " + name));
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            foreach (var pair in payload)
            {
                using var output = archive.CreateEntry(pair.Key).Open();
                output.Write(pair.Value);
            }
            archive.CreateEntry("empty.cache");
        }
        var existing = new[] { "existing.bak", "existing.log", "orphan.tmp", "archive.zip.temp", "nested/existing.part" }
            .ToDictionary(name => Path.Combine(directory, name), name => System.Text.Encoding.UTF8.GetBytes("Existing content: " + name));
        foreach (var pair in existing)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pair.Key)!);
            File.WriteAllBytes(pair.Key, pair.Value);
        }
        File.WriteAllText(Path.Combine(directory, "payload.par2"), "previous recovery file");
        string dataPath = Path.Combine(directory, "downloaded.log");
        byte[] downloaded = { 1, 2, 3, 4 };
        File.WriteAllBytes(dataPath, downloaded);
        string otherPart = Path.Combine(directory, "other.bin.part");
        File.WriteAllText(otherPart, "another download");
        string repairPath = Path.Combine(directory, "recovery.par2");
        File.WriteAllText(repairPath, "downloaded recovery input");
        string discoveredRepair = Path.Combine(directory, "discovered.par2");
        File.WriteAllText(discoveredRepair, "recovery input");
        var package = new DownloadPackage
        {
            Name = "Archive", IsCustomName = true, SaveDirectory = directory, AutoPar2Repair = false,
            AutoExtractArchives = !manual, DeleteArchiveAfterExtraction = !recycle,
            MoveArchiveToRecycleBin = recycle, HasCompletedNotified = true
        };
        var archiveItem = Completed(archivePath);
        var dataItem = Completed(dataPath);
        WriteDownloadSidecars(archiveItem);
        WriteDownloadSidecars(dataItem);
        package.Items.Add(archiveItem);
        package.Items.Add(dataItem);
        package.Items.Add(Completed(repairPath));
        QueueManager.Instance.Packages.Add(package);
        if (shared)
        {
            var other = new DownloadPackage { Name = "Other", IsCustomName = true, SaveDirectory = directory, AutoPar2Repair = false };
            other.Items.Add(new DownloadItem { FileName = "other.bin", SaveFilePath = Path.Combine(directory, "other.bin"), Status = DownloadStatus.Paused });
            QueueManager.Instance.Packages.Add(other);
        }
        bool success = await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package, force: manual);
        Assert.True(success);
        Assert.True(package.IsExtracted);
        Assert.False(File.Exists(archivePath));
        foreach (var pair in payload) Assert.Equal(pair.Value, File.ReadAllBytes(Path.Combine(directory, pair.Key)));
        foreach (var pair in existing) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
        Assert.Equal(0, new FileInfo(Path.Combine(directory, "empty.cache")).Length);
        Assert.Equal(downloaded, File.ReadAllBytes(dataPath));
        Assert.False(File.Exists(dataPath + ".part"));
        Assert.False(File.Exists(dataPath + ".part.segments"));
        Assert.False(File.Exists(repairPath));
        Assert.Equal(shared, File.Exists(discoveredRepair));
        Assert.Equal("another download", File.ReadAllText(otherPart));
        Assert.Empty(Directory.GetDirectories(directory, ".archive-work-*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackageCleanup_PreservesFinalTargetsThatResembleSidecars(bool otherPackage)
    {
        string directory = Path.Combine(_root.FullName, "Archive");
        Directory.CreateDirectory(directory);
        string archivePath = Path.Combine(directory, "archive.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("payload.txt").Open())) writer.Write("payload");
        var package = new DownloadPackage
        {
            Name = "Archive", IsCustomName = true, SaveDirectory = directory,
            AutoExtractArchives = true, AutoPar2Repair = false,
            DeleteArchiveAfterExtraction = true, HasCompletedNotified = true
        };
        var archiveItem = Completed(archivePath);
        WriteDownloadSidecars(archiveItem);
        package.Items.Add(archiveItem);
        var owner = otherPackage ? new DownloadPackage
        {
            Name = "Other", IsCustomName = true, SaveDirectory = directory, AutoPar2Repair = false
        } : package;
        foreach (string suffix in new[] { ".part", ".part.segments" })
        {
            File.WriteAllText(archivePath + suffix, "completed file" + suffix);
            owner.Items.Add(Completed(archivePath + suffix));
        }
        QueueManager.Instance.Packages.Add(package);
        if (otherPackage) QueueManager.Instance.Packages.Add(owner);
        Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package));
        foreach (string suffix in new[] { ".part", ".part.segments" })
            Assert.Equal("completed file" + suffix, File.ReadAllText(archivePath + suffix));
        Assert.False(File.Exists(archivePath));
    }

    private static DownloadItem Completed(string path) => new()
    {
        FileName = Path.GetFileName(path), SaveFilePath = path, Status = DownloadStatus.Completed,
        TotalBytes = new FileInfo(path).Length, DownloadedBytes = new FileInfo(path).Length
    };

    [Theory]
    [InlineData("legacy")]
    [InlineData("invalid")]
    [InlineData("other_source")]
    [InlineData("missing_url")]
    public async Task PackageCleanup_PreservesSidecarsWithoutMatchingOwnership(string scenario)
    {
        var package = CreateSimplePackage();
        var item = package.Items[0];
        WriteDownloadSidecars(item);
        string statusPath = item.SaveFilePath + ".part.segments";
        if (scenario == "legacy")
            File.WriteAllText(statusPath, JsonSerializer.Serialize(new
            {
                TotalBytes = item.TotalBytes, Segments = new[] { new { Start = 0, End = item.TotalBytes - 1, Done = item.TotalBytes } }
            }));
        else if (scenario == "invalid") File.WriteAllText(statusPath, "unrelated content");
        else if (scenario == "other_source") item.DirectDownloadUrl = "https://example.invalid/other";
        else item.DirectDownloadUrl = null;
        byte[] part = File.ReadAllBytes(item.SaveFilePath + ".part");
        byte[] status = File.ReadAllBytes(statusPath);
        Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package));
        Assert.Equal(part, File.ReadAllBytes(item.SaveFilePath + ".part"));
        Assert.Equal(status, File.ReadAllBytes(statusPath));
        Assert.False(File.Exists(item.SaveFilePath));
    }

    [Fact]
    public async Task PackageCleanup_PreservesDisabledDownloadsAndTheirOwnedSidecars()
    {
        var package = CreateSimplePackage();
        string path = Path.Combine(package.SaveDirectory, "pending.bin");
        File.WriteAllText(path, "disabled download");
        var item = Completed(path);
        WriteDownloadSidecars(item);
        item.IsEnabled = false;
        item.Status = DownloadStatus.Paused;
        package.Items.Add(item);
        byte[] part = File.ReadAllBytes(path + ".part");
        byte[] status = File.ReadAllBytes(path + ".part.segments");
        Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package));
        Assert.Equal("disabled download", File.ReadAllText(path));
        Assert.Equal(part, File.ReadAllBytes(path + ".part"));
        Assert.Equal(status, File.ReadAllBytes(path + ".part.segments"));
    }

    private DownloadPackage CreateSimplePackage()
    {
        string directory = Path.Combine(_root.FullName, "Archive");
        Directory.CreateDirectory(directory);
        string archivePath = Path.Combine(directory, "archive.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("payload.txt").Open())) writer.Write("payload");
        var package = new DownloadPackage
        {
            Name = "Archive", IsCustomName = true, SaveDirectory = directory, AutoExtractArchives = true,
            AutoPar2Repair = false, DeleteArchiveAfterExtraction = true, HasCompletedNotified = true
        };
        package.Items.Add(Completed(archivePath));
        QueueManager.Instance.Packages.Add(package);
        return package;
    }

    internal static void WriteDownloadSidecars(DownloadItem item)
    {
        item.DirectDownloadUrl = "https://example.invalid/" + Uri.EscapeDataString(item.FileName);
        byte[] data = File.ReadAllBytes(item.SaveFilePath!);
        File.WriteAllBytes(item.SaveFilePath + ".part", data);
        File.WriteAllText(item.SaveFilePath + ".part.segments", JsonSerializer.Serialize(new
        {
            TotalBytes = data.Length,
            SourceKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                new Uri(item.DirectDownloadUrl).GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped)))),
            Segments = new[] { new { Start = 0, End = data.Length - 1, Done = data.Length } }
        }));
    }

    public void Dispose()
    {
        QueueManager.Instance.Packages.Clear();
        foreach (var package in _previousPackages) QueueManager.Instance.Packages.Add(package);
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
