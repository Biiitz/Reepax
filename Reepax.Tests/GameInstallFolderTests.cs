using System;
using System.IO;
using Reepax.Models;
using Reepax.Services.Extractor;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;
using Xunit;

namespace Reepax.Tests;

public class GameInstallFolderTests : IDisposable
{
    private readonly string _tempTestDir;

    public GameInstallFolderTests()
    {
        _tempTestDir = Path.Combine(Path.GetTempPath(), "Reepax_GameInstallTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempTestDir);
        SettingsService.Instance.Settings.GameInstallDirectory = _tempTestDir;
    }

    public void Dispose()
    {
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
    [InlineData("Open_Adventure_Update_from_v1.0.1_to_v1.0.2.rar", "Open_Adventure_Update_from_v1.0.1_to_v1.0.2.rar")]
    [InlineData("Space Quest - Updates", "Space Quest - Updates")]
    [InlineData("Solar Explorer 2026", "Solar Explorer 2026")]
    [InlineData("Solar.Explorer.v2.1-Setup", "Solar.Explorer.v2.1-Setup")]
    [InlineData("Fantasy RPG [Deluxe Edition]", "Fantasy RPG [Deluxe Edition]")]
    [InlineData("Game Name: Special Edition / Part 1", "Game Name Special Edition Part 1")]
    [InlineData("Game *Test* <1> | New?", "Game Test 1 New")]
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
            Name = "Space Quest"
        };

        // Act
        var createdPath = GameInstallFolderService.CreateAndCopyGameInstallFolder(pkg, showNotification: false);

        // Assert
        Assert.NotNull(createdPath);
        Assert.True(Directory.Exists(createdPath));
        Assert.Equal(Path.Combine(_tempTestDir, "Space Quest"), createdPath);
    }

    [Fact]
    public void CreateAndCopyGameInstallFolder_ForUpdatePackage_CreatesExactFolderDirectory()
    {
        // Arrange
        var pkg = new DownloadPackage
        {
            Name = "Space Quest - Updates"
        };

        // Act
        var createdPath = GameInstallFolderService.CreateAndCopyGameInstallFolder(pkg, showNotification: false);

        // Assert
        Assert.NotNull(createdPath);
        Assert.True(Directory.Exists(createdPath));
        // Must point to the exact package folder name, not stripped!
        Assert.Equal(Path.Combine(_tempTestDir, "Space Quest - Updates"), createdPath);
    }

    [Fact]
    public void AppSettings_GameInstallDirectory_PersistsCorrectly()
    {
        // Arrange
        var customPath = Path.Combine(_tempTestDir, "CustomGames");
        SettingsService.Instance.Settings.CreateGameInstallFolder = true;
        SettingsService.Instance.Settings.GameInstallDirectory = customPath;

        // Act
        SettingsService.Instance.SaveSettings();

        // Assert
        Assert.True(SettingsService.Instance.Settings.CreateGameInstallFolder);
        Assert.Equal(customPath, SettingsService.Instance.Settings.GameInstallDirectory);
    }

    [Fact]
    public void QueueManager_NotifyPackageCompletion_CreatesFolderWhenEnabled()
    {
        // Arrange
        SettingsService.Instance.Settings.CreateGameInstallFolder = true;
        SettingsService.Instance.Settings.GameInstallDirectory = _tempTestDir;

        var pkg = new DownloadPackage
        {
            Name = "Stardew Valley",
            SaveDirectory = _tempTestDir,
            Status = DownloadStatus.Completed
        };
        var item = new DownloadItem
        {
            FileName = "stardew_valley.zip",
            Status = DownloadStatus.Completed,
            TotalBytes = 1000,
            DownloadedBytes = 1000
        };
        pkg.Items.Add(item);

        // Act
        Services.Download.QueueManager.Instance.NotifyPackageCompletionIfEligible(pkg);

        // Assert
        var expectedFolder = Path.Combine(_tempTestDir, "Stardew Valley");
        Assert.True(Directory.Exists(expectedFolder));
    }

    [Fact]
    public void CreateOrValidateGameInstallFolder_DistinguishesCreatedAndAlreadyExists()
    {
        // Arrange
        var pkg = new DownloadPackage
        {
            Name = "Hollow Knight"
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
            Name = "Celeste"
        };

        // Act
        var result = GameInstallFolderService.CreateOrValidateGameInstallFolder(pkg, customBase);

        // Assert
        Assert.Equal(GameInstallFolderStatus.Created, result.Status);
        var expected = Path.Combine(customBase, "Celeste");
        Assert.Equal(expected, result.Path);
        Assert.True(Directory.Exists(expected));
    }
}
