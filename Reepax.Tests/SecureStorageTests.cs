using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Reepax.Models;
using Reepax.Services.Storage;
using Xunit;

namespace Reepax.Tests;

public class SecureStorageTests
{
    [Fact]
    public void SecureAppDataStorage_EncryptAndDecrypt_Roundtrip()
    {
        var originalText = "{\"TestKey\":\"SecretValue123\",\"Path\":\"C:\\\\TestStorage\\\\Downloads\"}";

        var encrypted = SecureAppDataStorage.EncryptString(originalText);

        Assert.NotNull(encrypted);
        Assert.StartsWith(SecureAppDataStorage.HeaderPrefix, encrypted);
        Assert.NotEqual(originalText, encrypted);
        Assert.DoesNotContain("SecretValue123", encrypted);
        Assert.DoesNotContain("TestKey", encrypted);

        var decrypted = SecureAppDataStorage.DecryptString(encrypted);
        Assert.Equal(originalText, decrypted);
    }

    [Fact]
    public void SecureAppDataStorage_AlreadyEncrypted_DoesNotDoubleEncrypt()
    {
        var text = "{\"Key\":\"Value\"}";
        var encrypted1 = SecureAppDataStorage.EncryptString(text);
        var encrypted2 = SecureAppDataStorage.EncryptString(encrypted1);

        Assert.Equal(encrypted1, encrypted2);
        Assert.Equal(text, SecureAppDataStorage.DecryptString(encrypted2));
    }

    [Fact]
    public void SecureAppDataStorage_LegacyPlainText_DecryptedAsIs()
    {
        var plainJson = "{\"MaxConcurrentBackgroundDownloads\":5,\"Language\":\"de\"}";

        var decrypted = SecureAppDataStorage.DecryptString(plainJson);

        Assert.Equal(plainJson, decrypted);
    }

    [Fact]
    public void SecureAppDataStorage_EmptyOrNull_HandledGracefully()
    {
        Assert.Equal("", SecureAppDataStorage.EncryptString(""));
        Assert.Null(SecureAppDataStorage.EncryptString(null!));
        Assert.Equal("", SecureAppDataStorage.DecryptString(""));
        Assert.Null(SecureAppDataStorage.DecryptString(null!));
    }

    [Fact]
    public void SecureAppDataStorage_TamperedCiphertext_ThrowsException()
    {
        var tampered = SecureAppDataStorage.HeaderPrefix + "YWJjZGVmZ2hpamtsbW5vcHFyc3R1dnd4eXo="; // valid base64, but invalid DPAPI blob

        Assert.ThrowsAny<CryptographicException>(() => SecureAppDataStorage.DecryptString(tampered));
    }

    [Fact]
    public void SettingsService_SavesEncrypted_And_LoadsEncrypted()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_SecureSettingsTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var settingsFile = Path.Combine(testDir, "settings.json");

        try
        {
            var service = new SettingsService(settingsFile);
            service.Settings.MaxConcurrentBackgroundDownloads = 7;
            service.Settings.DefaultDownloadDirectory = "D:\\MySecretDownloadFolder";
            service.SaveSettings();

            Assert.True(File.Exists(settingsFile));

            var rawContent = File.ReadAllText(settingsFile);
            Assert.StartsWith(SecureAppDataStorage.HeaderPrefix, rawContent);
            Assert.DoesNotContain("MySecretDownloadFolder", rawContent);
            Assert.DoesNotContain("MaxConcurrentBackgroundDownloads", rawContent);

            var reloadedService = new SettingsService(settingsFile);
            Assert.Equal(7, reloadedService.Settings.MaxConcurrentBackgroundDownloads);
            Assert.Equal("D:\\MySecretDownloadFolder", reloadedService.Settings.DefaultDownloadDirectory);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void SettingsService_BackwardCompatibility_LoadsPlainJsonAndReSavesEncrypted()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_LegacySettingsTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var settingsFile = Path.Combine(testDir, "settings.json");

        try
        {
            // Write legacy plain JSON settings
            var legacyJson = JsonSerializer.Serialize(new AppSettings
            {
                MaxConcurrentBackgroundDownloads = 9,
                SpeedLimitMBps = 42
            }, new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(settingsFile, legacyJson);

            // Load with SettingsService
            var service = new SettingsService(settingsFile);
            Assert.Equal(9, service.Settings.MaxConcurrentBackgroundDownloads);
            Assert.Equal(42, service.Settings.SpeedLimitMBps);

            // Resave and verify it is now encrypted
            service.SaveSettings();

            var rawContent = File.ReadAllText(settingsFile);
            Assert.StartsWith(SecureAppDataStorage.HeaderPrefix, rawContent);
            Assert.DoesNotContain("MaxConcurrentBackgroundDownloads", rawContent);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void DownloadPersistenceService_SavesEncrypted_And_LoadsEncrypted()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_SecureDownloadsTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var downloadsFile = Path.Combine(testDir, "downloads.json");

        try
        {
            var persistence = new DownloadPersistenceService(downloadsFile);
            var package = new DownloadPackage
            {
                Id = Guid.NewGuid(),
                Name = "TopSecretGameArchive",
                SaveDirectory = "C:\\Games\\Secret"
            };
            package.Items.Add(new DownloadItem
            {
                Id = Guid.NewGuid(),
                FileName = "game.part01.rar",
                OriginalUrl = "https://example.com/secret_url_never_plaintext.rar",
                Status = DownloadStatus.Queued
            });

            var packages = new System.Collections.ObjectModel.ObservableCollection<DownloadPackage> { package };
            persistence.SaveDownloads(packages);

            Assert.True(File.Exists(downloadsFile));

            var rawContent = File.ReadAllText(downloadsFile);
            Assert.StartsWith(SecureAppDataStorage.HeaderPrefix, rawContent);
            Assert.DoesNotContain("TopSecretGameArchive", rawContent);
            Assert.DoesNotContain("secret_url_never_plaintext", rawContent);

            var reloaded = persistence.LoadDownloads();
            Assert.Single(reloaded);
            Assert.Equal("TopSecretGameArchive", reloaded[0].Name);
            Assert.Single(reloaded[0].Items);
            Assert.Equal("https://example.com/secret_url_never_plaintext.rar", reloaded[0].Items[0].OriginalUrl);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void DownloadPersistenceService_CorruptedEncryptedFile_RecoversFromBackup()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_CorruptEncryptedRecov_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var downloadsFile = Path.Combine(testDir, "downloads.json");

        try
        {
            var persistence = new DownloadPersistenceService(downloadsFile);
            var pkg1 = new DownloadPackage { Id = Guid.NewGuid(), Name = "ValidPackageOne" };
            var list1 = new System.Collections.ObjectModel.ObservableCollection<DownloadPackage> { pkg1 };
            persistence.SaveDownloads(list1);

            // Second save to generate .bak
            var pkg2 = new DownloadPackage { Id = Guid.NewGuid(), Name = "ValidPackageTwo" };
            var list2 = new System.Collections.ObjectModel.ObservableCollection<DownloadPackage> { pkg1, pkg2 };
            persistence.SaveDownloads(list2);

            Assert.True(File.Exists(downloadsFile));
            Assert.True(File.Exists(downloadsFile + ".bak"));

            // Corrupt the primary downloads.json with invalid encrypted header data
            File.WriteAllText(downloadsFile, SecureAppDataStorage.HeaderPrefix + "THIS_IS_CORRUPT_NOT_DPAPI_DATA==");

            var recovered = persistence.LoadDownloads();
            Assert.NotEmpty(recovered);

            var corruptFiles = Directory.GetFiles(testDir, "downloads.json.corrupt_*.bak");
            Assert.NotEmpty(corruptFiles);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }
}
