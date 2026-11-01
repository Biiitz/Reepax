using System;
using System.IO;
using System.Text.Json;
using Reepax.Models;
using Reepax.Services.Extractor;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;
using Xunit;

namespace Reepax.Tests;

public class GameInstallFolderTests : IDisposable
{
    private readonly string _tempTestDir;
    private readonly string _previousInstallDirectory;

    public GameInstallFolderTests()
    {
        _tempTestDir = Path.Combine(Path.GetTempPath(), "Reepax_GameInstallTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempTestDir);
        _previousInstallDirectory = SettingsService.Instance.Settings.GameInstallDirectory;
        SettingsService.Instance.Settings.GameInstallDirectory = _tempTestDir;
    }

    public void Dispose()
    {
        SettingsService.Instance.Settings.GameInstallDirectory = _previousInstallDirectory;
        try
        {
            if (Directory.Exists(_tempTestDir))
            {
                Directory.Delete(_tempTestDir, recursive: true);
            }
        }
        catch { }
    }

    [Theory]
    [InlineData("Open_Tool_Update_from_v1.0.1_to_v1.0.2.rar", "Open_Tool_Update_from_v1.0.1_to_v1.0.2.rar")]
    [InlineData("Example App - Updates", "Example App - Updates")]
    [InlineData("Example Software 2026", "Example Software 2026")]
    [InlineData("Example.Software.v2.1-Setup", "Example.Software.v2.1-Setup")]
    [InlineData("Example Suite [Deluxe Edition]", "Example Suite [Deluxe Edition]")]
    [InlineData("Example Name: Special Edition / Part 1", "Example Name Special Edition Part 1")]
    [InlineData("Example *Test* <1> | New?", "Example Test 1 New")]
    public void GetGameInstallName_RetainsExactInAppPackageName_AndSanitizesUnsupportedChars(string packageName, string expectedFolderName)
    {
        // Arrange
        var pkg = new DownloadPackage
        {
            Name = packageName
        };

        // Act
        var result = GameInstallFolderService.GetGameInstallName(pkg);

        // Assert
        Assert.Equal(expectedFolderName, result);
    }

    [Fact]
    public void CreateAndCopyGameInstallFolder_CreatesDirectoryOnDisk()
    {
        // Arrange
        var pkg = new DownloadPackage
        {
            Name = "Example App"
        };

        // Act
        var createdPath = GameInstallFolderService.CreateAndCopyGameInstallFolder(pkg, showNotification: false);

        // Assert
        Assert.NotNull(createdPath);
        Assert.True(Directory.Exists(createdPath));
        Assert.Equal(Path.Combine(_tempTestDir, "Example App"), createdPath);
    }

    [Fact]
    public void CreateAndCopyGameInstallFolder_ForUpdatePackage_CreatesExactFolderDirectory()
    {
        // Arrange
        var pkg = new DownloadPackage
        {
            Name = "Example App - Updates"
        };

        // Act
        var createdPath = GameInstallFolderService.CreateAndCopyGameInstallFolder(pkg, showNotification: false);

        // Assert
        Assert.NotNull(createdPath);
        Assert.True(Directory.Exists(createdPath));
        // Must point to the exact package folder name, not stripped!
        Assert.Equal(Path.Combine(_tempTestDir, "Example App - Updates"), createdPath);
    }

    [Fact]
    public void AppSettings_LegacyAutomaticFolderOption_IsIgnoredAndDirectoryIsPreserved()
    {
        var customPath = Path.Combine(_tempTestDir, "CustomApps");
        var legacyJson = JsonSerializer.Serialize(new { CreateGameInstallFolder = true, GameInstallDirectory = customPath });
        var settings = JsonSerializer.Deserialize<AppSettings>(legacyJson);

        Assert.NotNull(settings);
        Assert.Equal(customPath, settings.GameInstallDirectory);
        Assert.DoesNotContain("CreateGameInstallFolder", JsonSerializer.Serialize(settings));
    }

    [Fact]
    public void QueueManager_NotifyPackageCompletion_DoesNotCreateInstallFolder()
    {
        // Arrange
        SettingsService.Instance.Settings.GameInstallDirectory = _tempTestDir;

        var pkg = new DownloadPackage
        {
            Name = "Example Package",
            SaveDirectory = _tempTestDir,
            Status = DownloadStatus.Completed
        };
        var item = new DownloadItem
        {
            FileName = "example_package.zip",
            Status = DownloadStatus.Completed,
            TotalBytes = 1000,
            DownloadedBytes = 1000
        };
        pkg.Items.Add(item);

        // Act
        Services.Download.QueueManager.Instance.NotifyPackageCompletionIfEligible(pkg);

        // Assert
        var expectedFolder = Path.Combine(_tempTestDir, "Example Package");
        Assert.False(Directory.Exists(expectedFolder));
    }

    [Fact]
    public void CreateOrValidateGameInstallFolder_DistinguishesCreatedAndAlreadyExists()
    {
        // Arrange
        var pkg = new DownloadPackage
        {
            Name = "Example Project"
        };

        // Act 1: First call creates folder
        var result1 = GameInstallFolderService.CreateOrValidateGameInstallFolder(pkg);

        // Assert 1
        Assert.Equal(GameInstallFolderStatus.Created, result1.Status);
        Assert.NotNull(result1.Path);
        Assert.True(Directory.Exists(result1.Path));

        // Act 2: Second call detects already exists
        var result2 = GameInstallFolderService.CreateOrValidateGameInstallFolder(pkg);

        // Assert 2
        Assert.Equal(GameInstallFolderStatus.AlreadyExists, result2.Status);
        Assert.Equal(result1.Path, result2.Path);
    }

    [Fact]
    public void CreateOrValidateGameInstallFolder_WithCustomBaseDir_CreatesInCustomDirectory()
    {
        // Arrange
        var customBase = Path.Combine(_tempTestDir, "SelectedFolderAtRuntime");
        var pkg = new DownloadPackage
        {
            Name = "Example Suite"
        };

        // Act
        var result = GameInstallFolderService.CreateOrValidateGameInstallFolder(pkg, customBase);

        // Assert
        Assert.Equal(GameInstallFolderStatus.Created, result.Status);
        var expected = Path.Combine(customBase, "Example Suite");
        Assert.Equal(expected, result.Path);
        Assert.True(Directory.Exists(expected));
    }
}
