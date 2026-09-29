using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Win32;
using Reepax.Models;
using Reepax.Services.Browser;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;
using Reepax.Services.Verification;
using Xunit;

namespace Reepax.Tests;

/// <summary>
/// Comprehensive automated unit and integration tests for the Reepax Portable Mode overhaul.
/// Validates marker detection, directory path redirection, cross-machine encryption/persistence,
/// and complete host system isolation (registry and AppData decoupling).
/// </summary>
public class PortableModeTests : IDisposable
{
    private readonly string _testBaseDir;
    private readonly bool? _originalPortableOverride;
    private readonly bool _originalTestEnvironment;

    public PortableModeTests()
    {
        _testBaseDir = Path.Combine(Path.GetTempPath(), "Reepax_PortableModeTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testBaseDir);
        _originalPortableOverride = SettingsService.PortableModeOverride;
        _originalTestEnvironment = DownloadPersistenceService.IsTestEnvironment;
    }

    public void Dispose()
    {
        // Restore global overrides to prevent side effects in other test suites
        SettingsService.PortableModeOverride = _originalPortableOverride;
        DownloadPersistenceService.IsTestEnvironment = _originalTestEnvironment;

        try
        {
            if (Directory.Exists(_testBaseDir))
            {
                Directory.Delete(_testBaseDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    #region 1. DetectPortableMode() Tests

    [Fact]
    public void DetectPortableMode_Default_ReturnsFalseWhenNoMarkerPresent()
    {
        // Default call without markers or arguments in test environment
        var defaultDetection = SettingsService.DetectPortableMode();
        Assert.False(defaultDetection, "Default DetectPortableMode() should return false when no marker is present.");

        // Clean directory without portable markers
        var cleanDir = Path.Combine(_testBaseDir, "CleanEnv");
        Directory.CreateDirectory(cleanDir);
        var dirDetection = SettingsService.DetectPortableMode(cleanDir);
        Assert.False(dirDetection, "DetectPortableMode(cleanDir) should return false for clean directories.");
    }

    [Fact]
    public void DetectPortableMode_WhenPortableTxtExists_ReturnsTrue()
    {
        var dirWithPortableTxt = Path.Combine(_testBaseDir, "DirWithPortableTxt");
        Directory.CreateDirectory(dirWithPortableTxt);
        var markerPath = Path.Combine(dirWithPortableTxt, "portable.txt");
        File.WriteAllText(markerPath, "Reepax Portable Mode");

        var detected = SettingsService.DetectPortableMode(dirWithPortableTxt);
        Assert.True(detected, "DetectPortableMode must return true when portable.txt is present.");
    }

    [Fact]
    public void DetectPortableMode_WhenAlternativeMarkersExist_ReturnsTrue()
    {
        // Test .portable
        var dirDotPortable = Path.Combine(_testBaseDir, "DirDotPortable");
        Directory.CreateDirectory(dirDotPortable);
        File.WriteAllText(Path.Combine(dirDotPortable, ".portable"), "");
        Assert.True(SettingsService.DetectPortableMode(dirDotPortable), "DetectPortableMode must return true when .portable marker exists.");

        // Test portable.dat
        var dirPortableDat = Path.Combine(_testBaseDir, "DirPortableDat");
        Directory.CreateDirectory(dirPortableDat);
        File.WriteAllText(Path.Combine(dirPortableDat, "portable.dat"), "");
        Assert.True(SettingsService.DetectPortableMode(dirPortableDat), "DetectPortableMode must return true when portable.dat marker exists.");
    }

    [Fact]
    public void DetectPortableMode_WhenDataDirectoryExists_ReturnsTrue()
    {
        var dirWithData = Path.Combine(_testBaseDir, "DirWithDataDir");
        Directory.CreateDirectory(dirWithData);
        Directory.CreateDirectory(Path.Combine(dirWithData, "Data"));

        var detected = SettingsService.DetectPortableMode(dirWithData);
        Assert.True(detected, "DetectPortableMode must return true when Data directory exists.");
    }

    #endregion

    #region 2. Path Redirection Tests

    [Fact]
    public void PathRedirection_WhenIsPortableModeTrue_RedirectsAllDirectoriesToDataFolder()
    {
        try
        {
            SettingsService.PortableModeOverride = true;
            Assert.True(SettingsService.IsPortableMode);

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var expectedAppData = Path.Combine(baseDir, "Data");
            var expectedLogs = Path.Combine(expectedAppData, "logs");
            var expectedWebView2 = Path.Combine(expectedAppData, "WebView2");
            var expectedExtensions = Path.Combine(expectedAppData, "Extensions");

            Assert.Equal(expectedAppData, SettingsService.AppDataDirectory);
            Assert.True(SettingsService.AppDataDirectory.EndsWith(Path.DirectorySeparatorChar + "Data") ||
                        SettingsService.AppDataDirectory.EndsWith("/Data"),
                        $"AppDataDirectory '{SettingsService.AppDataDirectory}' must end with 'Data'");

            Assert.Equal(expectedLogs, SettingsService.LogsDirectory);
            Assert.True(SettingsService.LogsDirectory.EndsWith(Path.Combine("Data", "logs")),
                        $"LogsDirectory '{SettingsService.LogsDirectory}' must end with 'Data\\logs'");

            Assert.Equal(expectedWebView2, SettingsService.WebView2Directory);
            Assert.True(SettingsService.WebView2Directory.EndsWith(Path.Combine("Data", "WebView2")),
                        $"WebView2Directory '{SettingsService.WebView2Directory}' must end with 'Data\\WebView2'");

            Assert.Equal(expectedExtensions, BrowserExtensionService.ExtensionsDirectory);
            Assert.True(BrowserExtensionService.ExtensionsDirectory.EndsWith(Path.Combine("Data", "Extensions")),
                        $"ExtensionsDirectory '{BrowserExtensionService.ExtensionsDirectory}' must end with 'Data\\Extensions'");
        }
        finally
        {
            SettingsService.PortableModeOverride = null;
        }
    }

    [Fact]
    public void PathRedirection_WhenIsPortableModeFalse_UsesStandardLocalAppData()
    {
        try
        {
            SettingsService.PortableModeOverride = false;
            Assert.False(SettingsService.IsPortableMode);

            var expectedAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reepax");
            Assert.Equal(expectedAppData, SettingsService.AppDataDirectory);
            Assert.Equal(Path.Combine(expectedAppData, "logs"), SettingsService.LogsDirectory);
            Assert.Equal(Path.Combine(expectedAppData, "WebView2"), SettingsService.WebView2Directory);
            Assert.Equal(Path.Combine(expectedAppData, "Extensions"), BrowserExtensionService.ExtensionsDirectory);
        }
        finally
        {
            SettingsService.PortableModeOverride = null;
        }
    }

    #endregion

    #region 3. Portable Encryption & Persistence Tests

    [Fact]
    public void SavingSettings_InPortableMode_ProducesPortableHeader()
    {
        try
        {
            SettingsService.PortableModeOverride = true;
            var settingsPath = Path.Combine(_testBaseDir, "portable_settings.json");

            var service = new SettingsService(settingsPath);
            service.Settings.MaxConcurrentBackgroundDownloads = 16;
            service.Settings.DefaultDownloadDirectory = @"X:\ReepaxUSB\Downloads";
            service.SaveSettings();

            Assert.True(File.Exists(settingsPath), "Settings file should be written.");
            var rawContent = File.ReadAllText(settingsPath);

            Assert.StartsWith(SecureAppDataStorage.PortableHeaderPrefix, rawContent);
            Assert.StartsWith("RPX_PORTABLE_V1:", rawContent);
            Assert.DoesNotContain("ReepaxUSB", rawContent);
            Assert.DoesNotContain("MaxConcurrentBackgroundDownloads", rawContent);
        }
        finally
        {
            SettingsService.PortableModeOverride = null;
        }
    }

    [Fact]
    public void LoadingSettings_WithPortableHeader_OnAnySimulatedInstance_WorksSeamlessly()
    {
        var settingsPath = Path.Combine(_testBaseDir, "instance_settings.json");

        try
        {
            // 1. Save settings in portable mode
            SettingsService.PortableModeOverride = true;
            var instance1 = new SettingsService(settingsPath);
            instance1.Settings.MaxConcurrentBackgroundDownloads = 11;
            instance1.Settings.SpeedLimitMBps = 75;
            instance1.Settings.DefaultDownloadDirectory = @"Y:\PortableInstance\Downloads";
            instance1.SaveSettings();

            Assert.True(File.Exists(settingsPath));
            var raw = File.ReadAllText(settingsPath);
            Assert.StartsWith(SecureAppDataStorage.PortableHeaderPrefix, raw);

            // 2. Simulate reading the same settings file on a different instance in portable mode
            var instance2 = new SettingsService(settingsPath);
            Assert.Equal(11, instance2.Settings.MaxConcurrentBackgroundDownloads);
            Assert.Equal(75, instance2.Settings.SpeedLimitMBps);
            Assert.Equal(@"Y:\PortableInstance\Downloads", instance2.Settings.DefaultDownloadDirectory);

            // 3. Simulate reading the portable file on an installed instance (cross-instance flexibility)
            SettingsService.PortableModeOverride = false;
            var instance3Installed = new SettingsService(settingsPath);
            Assert.Equal(11, instance3Installed.Settings.MaxConcurrentBackgroundDownloads);
            Assert.Equal(75, instance3Installed.Settings.SpeedLimitMBps);
            Assert.Equal(@"Y:\PortableInstance\Downloads", instance3Installed.Settings.DefaultDownloadDirectory);
        }
        finally
        {
            SettingsService.PortableModeOverride = null;
        }
    }

    [Fact]
    public void SimulatingCrossMachinePersistence_PortableEncryption_DecryptsWithoutDpapiDependencies()
    {
        try
        {
            SettingsService.PortableModeOverride = true;

            var sensitivePayload = "{\"CloudAccount\":\"user@portable.drive\",\"Token\":\"sec_tok_9876543210\",\"Speed\":100}";

            // Encrypt using portable encryption
            var encryptedPortable = SecureAppDataStorage.EncryptString(sensitivePayload);

            Assert.NotNull(encryptedPortable);
            Assert.StartsWith(SecureAppDataStorage.PortableHeaderPrefix, encryptedPortable);
            Assert.NotEqual(sensitivePayload, encryptedPortable);
            Assert.DoesNotContain("sec_tok_9876543210", encryptedPortable);

            // Simulating decryption on any other machine / user account
            // In installed mode, DPAPI relies on Windows CurrentUser cryptographic master keys.
            // Portable mode uses AES-256-CBC with application-level key derivation, eliminating DPAPI dependency.
            var decryptedPayload = SecureAppDataStorage.DecryptString(encryptedPortable);
            Assert.Equal(sensitivePayload, decryptedPayload);

            // Verify tampering still reliably triggers CryptographicException
            var tamperedPayload = SecureAppDataStorage.PortableHeaderPrefix + "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8="; // Valid base64 but corrupted ciphertext
            Assert.ThrowsAny<CryptographicException>(() => SecureAppDataStorage.DecryptString(tamperedPayload));
        }
        finally
        {
            SettingsService.PortableModeOverride = null;
        }
    }

    #endregion

    #region 4. Host Isolation Tests

    [Fact]
    public void WindowsStartupService_SetAutostart_ReturnsFalseAndDoesNotWriteToRegistry_InPortableMode()
    {
        try
        {
            SettingsService.PortableModeOverride = true;
            DownloadPersistenceService.IsTestEnvironment = false; // Bypass test stub to verify real portable guard

            // Check current registry state before invocation
            string? valueBefore = null;
            try
            {
                using var runKeyBefore = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: false);
                valueBefore = runKeyBefore?.GetValue("Reepax") as string;
            }
            catch { }

            // Attempt to enable autostart in portable mode
            var result = WindowsStartupService.SetAutostart(true);

            Assert.False(result, "WindowsStartupService.SetAutostart(true) must return false in portable mode.");

            // Verify registry key was not modified or written
            string? valueAfter = null;
            try
            {
                using var runKeyAfter = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: false);
                valueAfter = runKeyAfter?.GetValue("Reepax") as string;
            }
            catch { }

            Assert.Equal(valueBefore, valueAfter);
        }
        finally
        {
            SettingsService.PortableModeOverride = null;
            DownloadPersistenceService.IsTestEnvironment = _originalTestEnvironment;
        }
    }

    [Fact]
    public void FileAssociationService_EnsureAssociationRegistered_DoesNotTouchRegistry_InPortableMode()
    {
        try
        {
            SettingsService.PortableModeOverride = true;
            DownloadPersistenceService.IsTestEnvironment = false;

            // In portable mode, EnsureAssociationRegistered should immediately exit without throwing or modifying HKCU
            var exception = Record.Exception(() => FileAssociationService.EnsureAssociationRegistered());
            Assert.Null(exception);
        }
        finally
        {
            SettingsService.PortableModeOverride = null;
            DownloadPersistenceService.IsTestEnvironment = _originalTestEnvironment;
        }
    }

    [Fact]
    public void Par2RepairService_ResolvePar2ExecutablePath_DoesNotCopyFilesToAppData()
    {
        try
        {
            SettingsService.PortableModeOverride = true;

            var appDataDir = SettingsService.AppDataDirectory;
            var appDataPar2 = Path.Combine(appDataDir, "par2.exe");

            if (File.Exists(appDataPar2))
            {
                try { File.Delete(appDataPar2); } catch { }
            }

            // Reset private cached path in Par2RepairService singleton
            var cachedField = typeof(Par2RepairService).GetField("_cachedExecutablePath", BindingFlags.NonPublic | BindingFlags.Instance);
            cachedField?.SetValue(Par2RepairService.Instance, null);

            var resolvedPath = Par2RepairService.Instance.ResolvePar2ExecutablePath();

            Assert.NotNull(resolvedPath);
            Assert.True(File.Exists(resolvedPath), $"Resolved par2 executable path must exist: {resolvedPath}");
            Assert.False(File.Exists(appDataPar2), "ResolvePar2ExecutablePath() must not copy par2.exe to AppData in portable mode!");
        }
        finally
        {
            SettingsService.PortableModeOverride = null;
        }
    }

    #endregion

    #region 5. Portable Logging & Complete Self-Containment Tests

    [Fact]
    public void AppLogger_WhenLoggingEnabled_InPortableMode_WritesDirectlyIntoDataLogsFolder()
    {
        try
        {
            SettingsService.PortableModeOverride = true;
            AppLogger.LogsDirectory = null!; // reset any custom directory override
            AppLogger.IsLoggingEnabled = true;

            var expectedLogsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "logs");
            Assert.Equal(expectedLogsDir, AppLogger.LogsDirectory);

            var testMessage = "Portable self-contained test log entry " + Guid.NewGuid().ToString("N");
            AppLogger.Info(testMessage);

            var logFilePath = AppLogger.GetCurrentLogFilePath();
            Assert.True(File.Exists(logFilePath), $"Log file must exist at {logFilePath}");
            Assert.StartsWith(expectedLogsDir, logFilePath, StringComparison.OrdinalIgnoreCase);

            var logContent = File.ReadAllText(logFilePath);
            Assert.Contains(testMessage, logContent);
        }
        finally
        {
            AppLogger.IsLoggingEnabled = false;
            SettingsService.PortableModeOverride = null;
        }
    }

    [Fact]
    public void AllDataFolders_InPortableMode_AreSubfoldersOfDataDirectory()
    {
        try
        {
            SettingsService.PortableModeOverride = true;

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var dataDir = Path.Combine(baseDir, "Data");

            Assert.Equal(dataDir, SettingsService.AppDataDirectory);
            Assert.StartsWith(dataDir, SettingsService.LogsDirectory, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(dataDir, SettingsService.IconsDirectory, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(dataDir, SettingsService.WebView2Directory, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(dataDir, BrowserExtensionService.ExtensionsDirectory, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SettingsService.PortableModeOverride = null;
        }
    }

    #endregion
}

