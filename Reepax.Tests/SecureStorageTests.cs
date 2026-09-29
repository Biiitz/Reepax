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

    [Fact]
    public void SecureAppDataStorage_Portable_EncryptAndDecrypt_Roundtrip()
    {
        var originalOverride = SettingsService.PortableModeOverride;
        try
        {
            SettingsService.PortableModeOverride = true;
            var originalText = "{\"Host\":\"reepax.portable.net\",\"ApiKey\":\"PortableKey_999\"}";

            var encrypted = SecureAppDataStorage.EncryptString(originalText);

            Assert.NotNull(encrypted);
            Assert.StartsWith(SecureAppDataStorage.PortableHeaderPrefix, encrypted);
            Assert.NotEqual(originalText, encrypted);
            Assert.DoesNotContain("PortableKey_999", encrypted);

            var decrypted = SecureAppDataStorage.DecryptString(encrypted);
            Assert.Equal(originalText, decrypted);
        }
        finally
        {
            SettingsService.PortableModeOverride = originalOverride;
        }
    }

    [Fact]
    public void SecureAppDataStorage_Portable_AlreadyEncrypted_DoesNotDoubleEncrypt()
    {
        var originalOverride = SettingsService.PortableModeOverride;
        try
        {
            SettingsService.PortableModeOverride = true;
            var text = "{\"Portable\":\"Value\"}";
            var encrypted1 = SecureAppDataStorage.EncryptString(text);
            var encrypted2 = SecureAppDataStorage.EncryptString(encrypted1);

            Assert.Equal(encrypted1, encrypted2);
            Assert.Equal(text, SecureAppDataStorage.DecryptString(encrypted2));
        }
        finally
        {
            SettingsService.PortableModeOverride = originalOverride;
        }
    }

    [Fact]
    public void SecureAppDataStorage_Portable_DecryptedAcrossModes()
    {
        var originalOverride = SettingsService.PortableModeOverride;
        try
        {
            // Encrypted in portable mode
            SettingsService.PortableModeOverride = true;
            var secret = "{\"SecretCrossMachineData\":42}";
            var encrypted = SecureAppDataStorage.EncryptString(secret);
            Assert.StartsWith(SecureAppDataStorage.PortableHeaderPrefix, encrypted);

            // Decrypted in installed mode (simulating portable data opened anywhere)
            SettingsService.PortableModeOverride = false;
            var decrypted = SecureAppDataStorage.DecryptString(encrypted);
            Assert.Equal(secret, decrypted);
        }
        finally
        {
            SettingsService.PortableModeOverride = originalOverride;
        }
    }

    [Fact]
    public void SecureAppDataStorage_Portable_TamperedCiphertext_ThrowsException()
    {
        // Invalid base64 in portable payload
        var tampered1 = SecureAppDataStorage.PortableHeaderPrefix + "NotValidBase64@@@";
        Assert.ThrowsAny<CryptographicException>(() => SecureAppDataStorage.DecryptString(tampered1));

        // Too short payload (< 16 bytes for IV)
        var tooShort = SecureAppDataStorage.PortableHeaderPrefix + Convert.ToBase64String(new byte[] { 1, 2, 3 });
        Assert.ThrowsAny<CryptographicException>(() => SecureAppDataStorage.DecryptString(tooShort));

        // Corrupted ciphertext bytes with valid IV length
        var corrupted = new byte[32];
        new Random(42).NextBytes(corrupted);
        var tampered2 = SecureAppDataStorage.PortableHeaderPrefix + Convert.ToBase64String(corrupted);
        Assert.ThrowsAny<CryptographicException>(() => SecureAppDataStorage.DecryptString(tampered2));
    }

    [Fact]
    public void SecureAppDataStorage_IsEncrypted_DetectsBothPrefixes()
    {
        Assert.True(SecureAppDataStorage.IsEncrypted(SecureAppDataStorage.HeaderPrefix + "someBase64"));
        Assert.True(SecureAppDataStorage.IsEncrypted(SecureAppDataStorage.PortableHeaderPrefix + "someBase64"));
        Assert.False(SecureAppDataStorage.IsEncrypted("{\"plain\":\"json\"}"));
        Assert.False(SecureAppDataStorage.IsEncrypted(null));
        Assert.False(SecureAppDataStorage.IsEncrypted(""));
        Assert.False(SecureAppDataStorage.IsEncrypted("   "));
    }

    [Fact]
    public void SecureAppDataStorage_CrossMachineDpapiException_IsDerivedCryptographicException()
    {
        var tampered = SecureAppDataStorage.HeaderPrefix + "YWJjZGVmZ2hpamtsbW5vcHFyc3R1dnd4eXo=";

        var ex = Assert.Throws<CrossMachineDpapiException>(() => SecureAppDataStorage.DecryptString(tampered));
        Assert.IsAssignableFrom<CryptographicException>(ex);
    }

    [Fact]
    public void SettingsService_PortableMode_SavesWithPortableHeader()
    {
        var originalOverride = SettingsService.PortableModeOverride;
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_PortableSettingsTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var settingsFile = Path.Combine(testDir, "settings.json");

        try
        {
            SettingsService.PortableModeOverride = true;

            var service = new SettingsService(settingsFile);
            service.Settings.MaxConcurrentBackgroundDownloads = 12;
            service.Settings.DefaultDownloadDirectory = "E:\\PortableDownloads";
            service.SaveSettings();

            Assert.True(File.Exists(settingsFile));
            var rawContent = File.ReadAllText(settingsFile);
            Assert.StartsWith(SecureAppDataStorage.PortableHeaderPrefix, rawContent);
            Assert.DoesNotContain("PortableDownloads", rawContent);

            var reloadedService = new SettingsService(settingsFile);
            Assert.Equal(12, reloadedService.Settings.MaxConcurrentBackgroundDownloads);
            Assert.Equal("E:\\PortableDownloads", reloadedService.Settings.DefaultDownloadDirectory);
        }
        finally
        {
            SettingsService.PortableModeOverride = originalOverride;
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void DownloadPersistenceService_PortableMode_SavesWithPortableHeader()
    {
        var originalOverride = SettingsService.PortableModeOverride;
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_PortableDownloadsTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var downloadsFile = Path.Combine(testDir, "downloads.json");

        try
        {
            SettingsService.PortableModeOverride = true;

            var persistence = new DownloadPersistenceService(downloadsFile);
            var package = new DownloadPackage
            {
                Id = Guid.NewGuid(),
                Name = "PortableUSBPackage",
                SaveDirectory = "E:\\Downloads"
            };
            package.Items.Add(new DownloadItem
            {
                Id = Guid.NewGuid(),
                FileName = "portable_file.zip",
                OriginalUrl = "https://example.com/portable_file.zip",
                Status = DownloadStatus.Queued
            });

            var packages = new System.Collections.ObjectModel.ObservableCollection<DownloadPackage> { package };
            persistence.SaveDownloads(packages);

            Assert.True(File.Exists(downloadsFile));
            var rawContent = File.ReadAllText(downloadsFile);
            Assert.StartsWith(SecureAppDataStorage.PortableHeaderPrefix, rawContent);
            Assert.DoesNotContain("PortableUSBPackage", rawContent);

            var reloaded = persistence.LoadDownloads();
            Assert.Single(reloaded);
            Assert.Equal("PortableUSBPackage", reloaded[0].Name);
            Assert.Single(reloaded[0].Items);
            Assert.Equal("https://example.com/portable_file.zip", reloaded[0].Items[0].OriginalUrl);
        }
        finally
        {
            SettingsService.PortableModeOverride = originalOverride;
            try { Directory.Delete(testDir, true); } catch { }
        }
    }
}
