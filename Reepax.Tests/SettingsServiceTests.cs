using System;
using System.IO;
using System.Text.Json;
using Reepax.Models;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Xunit;

namespace Reepax.Tests;

public class SettingsServiceTests : IDisposable
{
    private readonly string _tempDirectory;

    public SettingsServiceTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "Reepax_SettingsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, true);
            }
        }
        catch { }
    }

    [Fact]
    public void SettingsService_WhenFileIsCorrupted_LoadFails_FlagIsSet_AndSaveIsBlocked()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings.json");
        const string corruptContent = "INVALID_CORRUPT_ENCRYPTED_OR_JSON_CONTENT_12345";
        File.WriteAllText(settingsPath, corruptContent);

        var service = new SettingsService(settingsPath);

        // 1. Verify flag is set to true indicating load failure
        Assert.True(service.HasSettingsLoadFailed);

        // 2. Modify in-memory settings
        service.Settings.DefaultDownloadDirectory = "C:\\CompromisedPath";

        // 3. Attempt regular save - must be blocked to prevent overwriting existing data with factory defaults
        service.SaveSettings();

        // 4. File on disk must remain completely untouched
        Assert.True(File.Exists(settingsPath));
        var actualOnDisk = File.ReadAllText(settingsPath);
        Assert.Equal(corruptContent, actualOnDisk);

        // 5. Backup file must NOT be created with default settings
        var backupPath = settingsPath + ".bak";
        Assert.False(File.Exists(backupPath));
    }

    [Fact]
    public void SettingsService_WhenPrimaryIsCorruptedButBackupIsValid_RestoresFromBackup()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings.json");
        var backupPath = settingsPath + ".bak";

        // Write corrupt content to primary
        File.WriteAllText(settingsPath, "CORRUPT_PRIMARY");

        // Write valid encrypted settings to backup
        var validSettings = new AppSettings
        {
            SelectedCompletionSound = "5.mp3",
            CompletionSoundVolume = 95
        };
        var validJson = JsonSerializer.Serialize(validSettings);
        var encryptedBackup = SecureAppDataStorage.EncryptString(validJson);
        File.WriteAllText(backupPath, encryptedBackup);

        var service = new SettingsService(settingsPath);

        // Must successfully restore from backup
        Assert.False(service.HasSettingsLoadFailed);
        Assert.Equal("5.mp3", service.Settings.SelectedCompletionSound);
        Assert.Equal(95, service.Settings.CompletionSoundVolume);
    }

    [Fact]
    public void SettingsService_WhenLoadFailed_ForceSave_CreatesEmergencyBackupAndSaves()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings.json");
        const string corruptContent = "ORIGINAL_CORRUPT_DATA_TO_PRESERVE";
        File.WriteAllText(settingsPath, corruptContent);

        var service = new SettingsService(settingsPath);
        Assert.True(service.HasSettingsLoadFailed);

        service.Settings.SelectedErrorSound = "3.mp3";

        // Call SaveSettings with force: true
        service.SaveSettings(force: true);

        // Flag should now be reset to false
        Assert.False(service.HasSettingsLoadFailed);

        // An emergency backup file must exist and contain the original corrupt data
        var emergencyFiles = Directory.GetFiles(_tempDirectory, "settings.json.emergency_*.bak");
        Assert.Single(emergencyFiles);
        Assert.Equal(corruptContent, File.ReadAllText(emergencyFiles[0]));

        // Primary settings file should now be valid encrypted json with the new setting
        var reloadedService = new SettingsService(settingsPath);
        Assert.False(reloadedService.HasSettingsLoadFailed);
        Assert.Equal("3.mp3", reloadedService.Settings.SelectedErrorSound);
    }

    [Fact]
    public void SettingsService_CleanInstall_SavesSuccessfullyWithoutLoadFailure()
    {
        var settingsPath = Path.Combine(_tempDirectory, "clean_settings.json");
        Assert.False(File.Exists(settingsPath));

        var service = new SettingsService(settingsPath);
        Assert.False(service.HasSettingsLoadFailed);

        service.Settings.SelectedCompletionSound = "4.mp3";
        service.SaveSettings();

        Assert.True(File.Exists(settingsPath));
        var reloadedService = new SettingsService(settingsPath);
        Assert.False(reloadedService.HasSettingsLoadFailed);
        Assert.Equal("4.mp3", reloadedService.Settings.SelectedCompletionSound);
    }

    [Theory]
    [InlineData("Dialog_SettingsLoadFailedTitle")]
    [InlineData("Dialog_SettingsLoadFailedMessage")]
    public void Localization_SettingsLoadFailedKeys_ExistInBothLanguages(string key)
    {
        Assert.True(Strings_de.Map.ContainsKey(key), $"Key '{key}' is missing in Strings_de");
        Assert.False(string.IsNullOrWhiteSpace(Strings_de.Map[key]), $"Value for '{key}' is empty in Strings_de");

        Assert.True(Strings_en.Map.ContainsKey(key), $"Key '{key}' is missing in Strings_en");
        Assert.False(string.IsNullOrWhiteSpace(Strings_en.Map[key]), $"Value for '{key}' is empty in Strings_en");
    }

    [Fact]
    public void MigrateFromRoamingInternal_SuccessfulCopy_MigratesFilesAndRemovesSource()
    {
        var fakeRoaming = Path.Combine(_tempDirectory, "FakeRoaming");
        var fakeLocal = Path.Combine(_tempDirectory, "FakeLocal");
        Directory.CreateDirectory(fakeRoaming);
        Directory.CreateDirectory(fakeLocal);

        File.WriteAllText(Path.Combine(fakeRoaming, "settings.json"), "{\"Theme\":\"Dark\"}");
        File.WriteAllText(Path.Combine(fakeRoaming, "downloads.json"), "[]");

        var subDir = Path.Combine(fakeRoaming, "Extensions");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "ext1.crx"), "dummy");

        bool result = SettingsService.MigrateFromRoamingInternal(fakeRoaming, fakeLocal);

        Assert.True(result);
        Assert.True(File.Exists(Path.Combine(fakeLocal, "settings.json")));
        Assert.True(File.Exists(Path.Combine(fakeLocal, "downloads.json")));
        Assert.True(File.Exists(Path.Combine(fakeLocal, "Extensions", "ext1.crx")));
        Assert.False(Directory.Exists(fakeRoaming), "Roaming directory should be removed after 100% successful migration.");
    }

    [Fact]
    public void MigrateFromRoamingInternal_WhenFileLockedOrCopyFails_PreservesSourceDirectory()
    {
        var fakeRoaming = Path.Combine(_tempDirectory, "FakeRoamingLocked");
        var fakeLocal = Path.Combine(_tempDirectory, "FakeLocalLocked");
        Directory.CreateDirectory(fakeRoaming);
        Directory.CreateDirectory(fakeLocal);

        var lockedFile = Path.Combine(fakeRoaming, "settings.json");
        File.WriteAllText(lockedFile, "{\"Important\":\"UserValue\"}");

        // Lock file with FileShare.None to force copy failure
        using var lockStream = new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        bool result = SettingsService.MigrateFromRoamingInternal(fakeRoaming, fakeLocal);

        Assert.False(result);
        Assert.True(Directory.Exists(fakeRoaming), "Roaming directory must NEVER be deleted if any file failed to copy!");
        Assert.True(File.Exists(lockedFile), "Source file must be preserved intact!");
    }
}
