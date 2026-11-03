using System;
using System.IO;
using Reepax.Services.Storage;
using Xunit;

namespace Reepax.Tests;

/// <summary>
/// Unit and integration tests for CrashLogService verifying crash reporting,
/// disk flushing and application data path resolution,
/// host system isolation, setting enforcement, and nested exception formatting.
/// </summary>
public class CrashLogServiceTests : IDisposable
{
    private readonly string? _originalAppDataOverride;
    private readonly bool _originalFileLogging;
    private readonly string _testTempDir;

    public CrashLogServiceTests()
    {
        _originalAppDataOverride = SettingsService.AppDataDirectoryOverride;
        _originalFileLogging = SettingsService.Instance.Settings.EnableFileLogging;

        _testTempDir = Path.Combine(Path.GetTempPath(), "Reepax_CrashLogTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testTempDir);
        SettingsService.AppDataDirectoryOverride = _testTempDir;
        SettingsService.Instance.Settings.EnableFileLogging = true;
    }

    public void Dispose()
    {
        SettingsService.AppDataDirectoryOverride = _originalAppDataOverride;
        SettingsService.Instance.Settings.EnableFileLogging = _originalFileLogging;

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
    public void LogCrash_WritesOnlyToAppDataAndLogsDirectory()
    {
        var result = CrashLogService.LogCrash("StorageTest", new Exception("Simulated failure"));
        string appDataLog = Path.Combine(SettingsService.AppDataDirectory, "crash.log");
        string logsLog = Path.Combine(SettingsService.LogsDirectory, "crash.log");

        Assert.True(result.Success);
        Assert.True(File.Exists(appDataLog));
        Assert.True(File.Exists(logsLog));
        Assert.Equal(2, result.SavedPaths.Count);
        Assert.Contains(appDataLog, result.SavedPaths);
        Assert.Contains(logsLog, result.SavedPaths);
        Assert.All(result.SavedPaths, path => Assert.StartsWith(_testTempDir, path, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Simulated failure", File.ReadAllText(result.PrimaryLogPath));
        Assert.Contains("StorageTest", File.ReadAllText(result.PrimaryLogPath));
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
