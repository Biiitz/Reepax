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
    public void CrashReport_RedactsLocationsAndNestedExceptionsWhileRetainingDiagnosticDetails()
    {
        var exception = DiagnosticPrivacyTests.CreateSensitiveException();
        string report = CrashLogService.FormatCrashReport("ExampleSource", exception);

        Assert.Contains("App Version:", report);
        Assert.Contains("Runtime:", report);
        Assert.Contains("Source:       ExampleSource", report);
        Assert.Contains("System.InvalidOperationException", report);
        Assert.Contains("System.IO.IOException", report);
        Assert.Contains("Example.Component.Read()", report);
        Assert.Contains(":line 42", report);
        Assert.Contains("code=5", report);
        DiagnosticPrivacyTests.AssertSensitiveDataRemoved(report);
        Assert.DoesNotContain(SettingsService.AppDataDirectory, report);
        Assert.DoesNotContain(AppContext.BaseDirectory, report);

        var result = CrashLogService.LogCrash("ExampleSource", exception);
        Assert.True(result.Success);
        foreach (string path in result.SavedPaths)
        {
            string saved = File.ReadAllText(path);
            DiagnosticPrivacyTests.AssertSensitiveDataRemoved(saved);
            Assert.Contains("System.InvalidOperationException", saved);
            Assert.Contains("System.IO.IOException", saved);
            Assert.DoesNotContain(SettingsService.AppDataDirectory, saved);
            Assert.Contains("Example.Component.Read()", saved);
        }
    }

    [Fact]
    public void TryWriteCrashLog_RedactsDirectContentWithoutChangingItsDestination()
    {
        string path = Path.Combine(_testTempDir, "direct-crash.log");
        Assert.True(CrashLogService.TryWriteCrashLog(path,
            "Source: ExampleSource; https://files.example.test/TestDirectSecret/archive.bin"));
        string content = File.ReadAllText(path);
        Assert.Contains("ExampleSource", content);
        Assert.DoesNotContain("TestDirectSecret", content);
    }

    [Fact]
    public void LogCrash_WhenPrimaryTargetsAreBlocked_WritesOnlyRedactedEmergencyReport()
    {
        string? previousTemp = Environment.GetEnvironmentVariable("TEMP");
        string? previousTmp = Environment.GetEnvironmentVariable("TMP");
        string temporary = Path.Combine(_testTempDir, "fallback");
        Directory.CreateDirectory(temporary);
        try
        {
            Environment.SetEnvironmentVariable("TEMP", temporary);
            Environment.SetEnvironmentVariable("TMP", temporary);
            Assert.Equal(Path.TrimEndingDirectorySeparator(temporary), Path.TrimEndingDirectorySeparator(Path.GetTempPath()));
            Assert.StartsWith(_testTempDir, SettingsService.LogsDirectory, StringComparison.OrdinalIgnoreCase);
            File.WriteAllText(SettingsService.LogsDirectory, "blocked directory");
            Directory.CreateDirectory(Path.Combine(_testTempDir, "crash.log"));
            var result = CrashLogService.LogCrash("ExampleSource", DiagnosticPrivacyTests.CreateSensitiveException());
            Assert.True(result.Success);
            string path = Assert.Single(result.SavedPaths);
            Assert.Equal(Path.Combine(temporary, "Reepax", "crash.log"), path);
            string text = File.ReadAllText(path);
            DiagnosticPrivacyTests.AssertSensitiveDataRemoved(text);
            Assert.Contains("System.InvalidOperationException", text);
            Assert.Contains("System.IO.IOException", text);
            Assert.Contains("Example.Component.Read()", text);
            Assert.Contains(":line 42", text);
        }
        finally
        {
            AppLogger.Flush();
            Environment.SetEnvironmentVariable("TEMP", previousTemp);
            Environment.SetEnvironmentVariable("TMP", previousTmp);
        }
    }

    [Fact]
    public void LogCrash_WhenLoggingDisabled_DoesNotWriteToDisk()
    {
        try
        {
            SettingsService.Instance.Settings.EnableFileLogging = false;

            var ex = DiagnosticPrivacyTests.CreateSensitiveException();
            var result = CrashLogService.LogCrash("DisabledLoggingTest", ex, forceWrite: false);

            Assert.False(result.Success);
            Assert.True(result.LoggingDisabled);
            Assert.Empty(result.SavedPaths);

            var expectedLog = Path.Combine(SettingsService.AppDataDirectory, "crash.log");
            Assert.False(File.Exists(expectedLog), "Crash log file must not be created on disk when logging is disabled.");
            Assert.Empty(Directory.GetFiles(_testTempDir, "*", SearchOption.AllDirectories));
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
    public void LogCrash_WhenDisabled_DoesNotForwardTheReportToAnEnabledLogger()
    {
        bool previousLoggerState = AppLogger.IsLoggingEnabled;
        try
        {
            SettingsService.Instance.Settings.EnableFileLogging = false;
            AppLogger.IsLoggingEnabled = true;
            var result = CrashLogService.LogCrash("ExampleSource", DiagnosticPrivacyTests.CreateSensitiveException());
            AppLogger.Flush();

            Assert.True(result.LoggingDisabled);
            Assert.Empty(result.SavedPaths);
            Assert.Empty(Directory.GetFiles(_testTempDir, "*", SearchOption.AllDirectories));
        }
        finally
        {
            AppLogger.IsLoggingEnabled = previousLoggerState;
            SettingsService.Instance.Settings.EnableFileLogging = true;
        }
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
