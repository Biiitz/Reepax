using System;
using System.IO;
using Reepax.Services.Storage;
using Xunit;

namespace Reepax.Tests;

/// <summary>
/// Unit and integration tests for CrashLogService verifying crash reporting,
/// disk flushing, multi-path resolution in installed and portable modes,
/// host system isolation, setting enforcement, and nested exception formatting.
/// </summary>
public class CrashLogServiceTests : IDisposable
{
    private readonly bool? _originalPortableOverride;
    private readonly string? _originalAppDataOverride;
    private readonly bool _originalFileLogging;
    private readonly string _testTempDir;

    public CrashLogServiceTests()
    {
        _originalPortableOverride = SettingsService.PortableModeOverride;
        _originalAppDataOverride = SettingsService.AppDataDirectoryOverride;
        _originalFileLogging = SettingsService.Instance.Settings.EnableFileLogging;

        _testTempDir = Path.Combine(Path.GetTempPath(), "Reepax_CrashLogTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testTempDir);
        SettingsService.AppDataDirectoryOverride = _testTempDir;
        SettingsService.Instance.Settings.EnableFileLogging = true;
    }

    public void Dispose()
    {
        SettingsService.PortableModeOverride = _originalPortableOverride;
        SettingsService.AppDataDirectoryOverride = _originalAppDataOverride;
        SettingsService.Instance.Settings.EnableFileLogging = _originalFileLogging;

        try
        {
            var baseDirLog = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
            if (File.Exists(baseDirLog))
            {
                File.Delete(baseDirLog);
            }
        }
        catch { }

        try
        {
            if (Directory.Exists(_testTempDir))
            {
                Directory.Delete(_testTempDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void FormatCrashReport_IncludesAllDiagnosticInfoAndInnerExceptions()
    {
        var inner = new ArgumentNullException("paramName", "Inner parameter was null");
        var outer = new InvalidOperationException("Outer operation failed", inner);

        var report = CrashLogService.FormatCrashReport("TestDispatcher", outer);

        Assert.Contains("[CRASH REPORT]", report);
        Assert.Contains("Source:       TestDispatcher", report);
        Assert.Contains("InvalidOperationException: Outer operation failed", report);
        Assert.Contains("Inner Exception [1]: System.ArgumentNullException", report);
        Assert.Contains("Inner parameter was null", report);
        Assert.Contains("Runtime:", report);
        Assert.Contains("OS Version:", report);
    }

    [Fact]
    public void LogCrash_InInstalledMode_WritesToAppDataAndLogsDirectory()
    {
        try
        {
            SettingsService.PortableModeOverride = false;
            Assert.False(SettingsService.IsPortableMode);

            var ex = new Exception("Simulated installed mode crash exception");
            var result = CrashLogService.LogCrash("InstalledModeTest", ex);

            Assert.True(result.Success, "CrashLogService should succeed writing in installed mode.");
            Assert.False(string.IsNullOrEmpty(result.PrimaryLogPath));
            Assert.True(File.Exists(result.PrimaryLogPath), $"Primary crash log must exist at: {result.PrimaryLogPath}");

            var expectedAppDataCrashLog = Path.Combine(SettingsService.AppDataDirectory, "crash.log");
            Assert.True(File.Exists(expectedAppDataCrashLog), $"Crash log must exist in AppDataDirectory: {expectedAppDataCrashLog}");

            var expectedLogsCrashLog = Path.Combine(SettingsService.LogsDirectory, "crash.log");
            Assert.True(File.Exists(expectedLogsCrashLog), $"Crash log must exist in LogsDirectory: {expectedLogsCrashLog}");

            var content = File.ReadAllText(result.PrimaryLogPath);
            Assert.Contains("Simulated installed mode crash exception", content);
            Assert.Contains("InstalledModeTest", content);

            // Ensure test wrote exclusively inside isolated test directory
            Assert.StartsWith(_testTempDir, result.PrimaryLogPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SettingsService.PortableModeOverride = _originalPortableOverride;
        }
    }

    [Fact]
    public void LogCrash_InPortableMode_WritesToPortableDataAndIsolatesHost()
    {
        try
        {
            SettingsService.PortableModeOverride = true;
            Assert.True(SettingsService.IsPortableMode);

            var ex = new Exception("Simulated portable mode crash exception");
            var result = CrashLogService.LogCrash("PortableModeTest", ex);

            Assert.True(result.Success, "CrashLogService should succeed writing in portable mode.");

            // 1. Portable data directory
            var portableAppDataLog = Path.Combine(SettingsService.AppDataDirectory, "crash.log");
            Assert.True(File.Exists(portableAppDataLog), $"Crash log must exist in portable AppData: {portableAppDataLog}");

            // 2. Portable logs directory
            var portableLogsLog = Path.Combine(SettingsService.LogsDirectory, "crash.log");
            Assert.True(File.Exists(portableLogsLog), $"Crash log must exist in portable Logs: {portableLogsLog}");

            // 3. Base directory (root of portable distribution)
            var baseDirLog = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
            Assert.True(File.Exists(baseDirLog), $"Crash log must exist in BaseDirectory: {baseDirLog}");

            // 4. Host %LOCALAPPDATA% must NOT be touched in portable mode (complete host isolation)
            var hostAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reepax", "crash.log");
            Assert.DoesNotContain(result.SavedPaths, p => p.Equals(hostAppData, StringComparison.OrdinalIgnoreCase));

            var content = File.ReadAllText(portableAppDataLog);
            Assert.Contains("Simulated portable mode crash exception", content);
            Assert.Contains("Portable:     True", content);
        }
        finally
        {
            SettingsService.PortableModeOverride = _originalPortableOverride;
        }
    }

    [Fact]
    public void LogCrash_WhenLoggingDisabled_DoesNotWriteToDisk()
    {
        try
        {
            SettingsService.Instance.Settings.EnableFileLogging = false;

            var ex = new Exception("Simulated crash with logging disabled");
            var result = CrashLogService.LogCrash("DisabledLoggingTest", ex, forceWrite: false);

            Assert.False(result.Success);
            Assert.True(result.LoggingDisabled);
            Assert.Empty(result.SavedPaths);

            var expectedLog = Path.Combine(SettingsService.AppDataDirectory, "crash.log");
            Assert.False(File.Exists(expectedLog), "Crash log file must not be created on disk when logging is disabled.");
        }
        finally
        {
            SettingsService.Instance.Settings.EnableFileLogging = true;
        }
    }

    [Fact]
    public void CleanupLogFiles_RemovesLogsDirectoryAndCrashLog()
    {
        var dummyLogDir = SettingsService.LogsDirectory;
        Directory.CreateDirectory(dummyLogDir);
        File.WriteAllText(Path.Combine(dummyLogDir, "reepax_test.log"), "Test log entry");

        var dummyCrashLog = Path.Combine(SettingsService.AppDataDirectory, "crash.log");
        File.WriteAllText(dummyCrashLog, "Test crash entry");

        Assert.True(Directory.Exists(dummyLogDir));
        Assert.True(File.Exists(dummyCrashLog));

        SettingsService.CleanupLogFiles();

        Assert.False(Directory.Exists(dummyLogDir), "Logs directory must be removed by CleanupLogFiles().");
        Assert.False(File.Exists(dummyCrashLog), "crash.log must be removed by CleanupLogFiles().");
    }

    [Fact]
    public void TryWriteCrashLog_CreatesDirectoryAutomaticallyAndAppends()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Reepax_CrashLogTest_" + Guid.NewGuid().ToString("N"));
        var testFile = Path.Combine(tempDir, "subfolder", "crash.log");

        try
        {
            var success1 = CrashLogService.TryWriteCrashLog(testFile, "Entry 1\n");
            Assert.True(success1);
            Assert.True(File.Exists(testFile));

            var success2 = CrashLogService.TryWriteCrashLog(testFile, "Entry 2\n");
            Assert.True(success2);

            var content = File.ReadAllText(testFile);
            Assert.Contains("Entry 1", content);
            Assert.Contains("Entry 2", content);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}
