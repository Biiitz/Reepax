using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Storage;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

public class PackageExportImportTests : IDisposable
{
    private readonly string _tempTestDir;

    public PackageExportImportTests()
    {
        DownloadPersistenceService.IsTestEnvironment = true;
        _tempTestDir = Path.Combine(Path.GetTempPath(), "Reepax_ExportImportTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempTestDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempTestDir))
            {
                Directory.Delete(_tempTestDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public void Export_OnlyContainsMetadata_AndExplicitlyExcludesRuntimeProgressAndState()
    {
        // 1. Create a package with active progress, runtime errors, and speeds
        var package = new DownloadPackage
        {
            Name = "My_Test_Game",
            SaveDirectory = @"C:\Downloads\My_Test_Game",
            AutoExtractArchives = true,
            LowResourceExtraction = true,
            DeleteArchiveAfterExtraction = true,
            MoveArchiveToRecycleBin = false,
            AutoResolveHostLinks = true,
            Status = DownloadStatus.Downloading,
            DownloadedBytes = 524288000,
            ProgressPercentage = 50.0,
            SpeedBytesPerSecond = 10485760,
            RemainingSeconds = 50,
            CompletedItemsCount = 1,
            TotalItemsCount = 2,
            StartedAt = DateTime.UtcNow.AddMinutes(-10),
            StatusMessage = "Downloading 10 MB/s"
        };

        var item1 = new DownloadItem
        {
            PackageId = package.Id,
            FileName = "game.part1.rar",
            OriginalUrl = "https://rapidgator.net/file/123/game.part1.rar.html",
            DirectDownloadUrl = "https://rg.download.direct/token123/game.part1.rar",
            TotalBytes = 1048576000,
            DownloadedBytes = 524288000,
            ProgressPercentage = 50.0,
            SpeedBytesPerSecond = 10485760,
            RemainingSeconds = 50,
            Status = DownloadStatus.Downloading,
            StatusMessage = "Downloading",
            ErrorMessage = "Temporary chunk timeout",
            Cookies = "session=secret_token",
            UserAgent = "CustomAgent/1.0",
            Referer = "https://rapidgator.net/",
            SaveFilePath = @"C:\Downloads\My_Test_Game\game.part1.rar",
            HosterName = "Rapidgator",
            IsEnabled = true
        };

        var item2 = new DownloadItem
        {
            PackageId = package.Id,
            FileName = "game.part2.rar",
            OriginalUrl = "https://rapidgator.net/file/456/game.part2.rar.html",
            TotalBytes = 1048576000,
            DownloadedBytes = 0,
            ProgressPercentage = 0,
            Status = DownloadStatus.Queued,
            HosterName = "Rapidgator",
            IsEnabled = false
        };

        package.Items.Add(item1);
        package.Items.Add(item2);

        // 2. Export to JSON
        var json = PackageExportImportService.ExportToJson(package);

        // 3. Verify that metadata IS present
        Assert.Contains("\"format\": \"repx\"", json);
        Assert.Contains("\"name\": \"My_Test_Game\"", json);
        Assert.Contains("\"subDirectory\": \"My_Test_Game\"", json);
        Assert.Contains("\"autoExtractArchives\": true", json);
        Assert.Contains("\"lowResourceExtraction\": true", json);
        Assert.Contains("\"deleteArchiveAfterExtraction\": true", json);
        Assert.Contains("\"fileName\": \"game.part1.rar\"", json);
        Assert.Contains("\"originalUrl\": \"https://rapidgator.net/file/123/game.part1.rar.html\"", json);
        Assert.Contains("\"totalBytes\": 1048576000", json);
        Assert.Contains("\"hosterName\": \"Rapidgator\"", json);

        // 4. Verify that NO runtime state or download progress is saved
        Assert.DoesNotContain("\"downloadedBytes\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"progressPercentage\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"speedBytesPerSecond\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"remainingSeconds\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"errorMessage\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"cookies\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"directDownloadUrl\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"session=secret_token\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"Temporary chunk timeout\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"status\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"statusMessage\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Import_RestoresNewUnstartedPackage_WithZeroProgress()
    {
        var original = new DownloadPackage
        {
            Name = "Upgraded_Software",
            AutoExtractArchives = true,
            Status = DownloadStatus.Downloading,
            DownloadedBytes = 999999,
            ProgressPercentage = 99.0
        };

        var item = new DownloadItem
        {
            FileName = "setup.iso",
            OriginalUrl = "https://ddownload.com/12345/setup.iso",
            TotalBytes = 500000000,
            DownloadedBytes = 250000000,
            ProgressPercentage = 50.0,
            Status = DownloadStatus.Downloading,
            HosterName = "DDownload"
        };
        original.Items.Add(item);

        var filePath = Path.Combine(_tempTestDir, "test_pkg.repx");
        PackageExportImportService.ExportToFile(original, filePath);
        Assert.True(File.Exists(filePath));

        // Import the file
        var imported = PackageExportImportService.ImportFromFile(filePath);

        // Package must be completely fresh
        Assert.NotNull(imported);
        Assert.NotEqual(original.Id, imported.Id);
        Assert.Equal("Upgraded_Software", imported.Name);
        Assert.True(imported.AutoExtractArchives);

        // Progress & Status MUST be strictly 0% / Queued
        Assert.Equal(DownloadStatus.Queued, imported.Status);
        Assert.Equal(0, imported.DownloadedBytes);
        Assert.Equal(0.0, imported.ProgressPercentage);
        Assert.Equal(0, imported.SpeedBytesPerSecond);
        Assert.Equal(0, imported.RemainingSeconds);
        Assert.Null(imported.StartedAt);
        Assert.Null(imported.CompletedAt);

        // Verify items
        Assert.Single(imported.Items);
        var importedItem = imported.Items[0];
        Assert.Equal("setup.iso", importedItem.FileName);
        Assert.Equal("https://ddownload.com/12345/setup.iso", importedItem.OriginalUrl);
        Assert.Equal(500000000, importedItem.TotalBytes);
        Assert.Equal("DDownload", importedItem.HosterName);

        // Item progress MUST be reset
        Assert.Equal(0, importedItem.DownloadedBytes);
        Assert.Equal(0.0, importedItem.ProgressPercentage);
        Assert.Equal(DownloadStatus.Queued, importedItem.Status);
        Assert.Null(importedItem.DirectDownloadUrl);
        Assert.Null(importedItem.ErrorMessage);
        Assert.Null(importedItem.Cookies);
    }

    [Fact]
    public void Import_SupportsLegacySdlrFormat()
    {
        var legacyJson = @"{
            ""format"": ""sdlr"",
            ""version"": 1,
            ""name"": ""Legacy_Package"",
            ""items"": [
                {
                    ""fileName"": ""legacy_game.rar"",
                    ""originalUrl"": ""https://rapidgator.net/file/abc/legacy_game.rar"",
                    ""totalBytes"": 2048,
                    ""hosterName"": ""Rapidgator""
                }
            ]
        }";

        var imported = PackageExportImportService.ImportFromJson(legacyJson);
        Assert.NotNull(imported);
        Assert.Equal("Legacy_Package", imported.Name);
        Assert.Single(imported.Items);
        Assert.Equal("legacy_game.rar", imported.Items[0].FileName);
    }

    [Fact]
    public async Task ImportAndAddAsync_ValidFile_AddsPackageToViewModel()
    {
        var package = new DownloadPackage
        {
            Name = "Import_ViewModel_Test"
        };
        package.Items.Add(new DownloadItem
        {
            FileName = "archive.zip",
            OriginalUrl = "https://rapidgator.net/file/111/archive.zip.html",
            TotalBytes = 1000000
        });

        var filePath = Path.Combine(_tempTestDir, "vm_test.repx");
        PackageExportImportService.ExportToFile(package, filePath);

        var vm = new MainViewModel();
        var initialCount = vm.Packages.Count;

        var imported = await PackageExportImportService.ImportAndAddAsync(filePath, vm);

        Assert.NotNull(imported);
        Assert.Equal(initialCount + 1, vm.Packages.Count);
        Assert.Contains(imported, vm.Packages);
        Assert.Equal(0, imported.DownloadedBytes);
        Assert.Equal(DownloadStatus.Queued, imported.Status);
        Assert.Contains("Import_ViewModel_Test", vm.StatusSummary);
    }

    [Fact]
    public void ImportFromFile_MissingFile_ThrowsFileNotFoundException()
    {
        var nonExistentPath = Path.Combine(_tempTestDir, "does_not_exist.repx");
        Assert.Throws<FileNotFoundException>(() => PackageExportImportService.ImportFromFile(nonExistentPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ImportFromJson_EmptyOrWhitespace_ThrowsInvalidDataException(string emptyJson)
    {
        Assert.Throws<InvalidDataException>(() => PackageExportImportService.ImportFromJson(emptyJson));
    }

    [Fact]
    public void ExportWithDialog_WhenPackageIsCompleted_DoesNotExport()
    {
        var completedPackage = new DownloadPackage
        {
            Name = "Completed_Package",
            Status = DownloadStatus.Completed,
            DownloadedBytes = 1000,
            TotalBytes = 1000,
            ProgressPercentage = 100.0
        };

        // ExportWithDialog returns false for completed packages
        var exported = PackageExportImportService.ExportWithDialog(completedPackage);
        Assert.False(exported);
    }
}
