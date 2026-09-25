using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Reepax.Services.Storage;
using Xunit;

namespace Reepax.Tests;

public class AppLoggerTests : IDisposable
{
    private readonly string _testLogsDirectory;

    public AppLoggerTests()
    {
        _testLogsDirectory = Path.Combine(Path.GetTempPath(), "ReepaxTest_Logs_" + Guid.NewGuid().ToString("N"));
        AppLogger.LogsDirectory = _testLogsDirectory;
        AppLogger.IsLoggingEnabled = true;
    }

    public void Dispose()
    {
        AppLogger.LogsDirectory = null!;
        AppLogger.IsLoggingEnabled = false;
        try
        {
            if (Directory.Exists(_testLogsDirectory))
            {
                Directory.Delete(_testLogsDirectory, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void AppLogger_CreatesDirectoryAndDailyLogFile()
    {
        Assert.False(Directory.Exists(_testLogsDirectory));

        AppLogger.Info("Test message 1");

        Assert.True(Directory.Exists(_testLogsDirectory));
        var logFile = AppLogger.GetCurrentLogFilePath();
        Assert.True(File.Exists(logFile));
        Assert.Contains(DateTime.Now.ToString("yyyy-MM-dd"), Path.GetFileName(logFile));
    }

    [Fact]
    public void AppLogger_WritesFormattedEntriesWithTimestampAndLevel()
    {
        AppLogger.Info("Information notification");
        AppLogger.Warn("Warning notification");
        AppLogger.Error("Error notification");

        var logFile = AppLogger.GetCurrentLogFilePath();
        var lines = File.ReadAllLines(logFile);

        Assert.Equal(3, lines.Length);

        // Regex format: [yyyy-MM-dd HH:mm:ss] [LEVEL] Message
        var timestampRegex = new Regex(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\] \[(INFO|WARN|ERROR)\] (.*)$");

        var match1 = timestampRegex.Match(lines[0]);
        Assert.True(match1.Success);
        Assert.Equal("INFO", match1.Groups[1].Value);
        Assert.Equal("Information notification", match1.Groups[2].Value);

        var match2 = timestampRegex.Match(lines[1]);
        Assert.True(match2.Success);
        Assert.Equal("WARN", match2.Groups[1].Value);
        Assert.Equal("Warning notification", match2.Groups[2].Value);

        var match3 = timestampRegex.Match(lines[2]);
        Assert.True(match3.Success);
        Assert.Equal("ERROR", match3.Groups[1].Value);
        Assert.Equal("Error notification", match3.Groups[2].Value);
    }

    [Fact]
    public void AppLogger_WritesExceptionDetails()
    {
        try
        {
            throw new InvalidOperationException("Test exception message for logger");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Operation failed", ex);
        }

        var logFile = AppLogger.GetCurrentLogFilePath();
        var content = File.ReadAllText(logFile);

        Assert.Contains("[ERROR] Operation failed", content);
        Assert.Contains("System.InvalidOperationException: Test exception message for logger", content);
        Assert.Contains(nameof(AppLoggerTests), content); // Stack trace verification
    }

    [Fact]
    public async Task AppLogger_ConcurrentWriting_ThreadSafeWithoutExceptions()
    {
        const int taskCount = 30;
        const int writesPerTask = 40;
        int totalExpectedLines = taskCount * writesPerTask;

        var tasks = Enumerable.Range(0, taskCount).Select(taskId => Task.Run(() =>
        {
            for (int i = 0; i < writesPerTask; i++)
            {
                if (i % 3 == 0)
                    AppLogger.Info($"Task {taskId} - Log entry {i}");
                else if (i % 3 == 1)
                    AppLogger.Warn($"Task {taskId} - Warning entry {i}");
                else
                    AppLogger.Error($"Task {taskId} - Error entry {i}");
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        var logFile = AppLogger.GetCurrentLogFilePath();
        var writtenLines = File.ReadAllLines(logFile);
        var taskLines = writtenLines.Where(l => l.Contains("Task ")).ToArray();

        Assert.Equal(totalExpectedLines, taskLines.Length);
    }

    [Fact]
    public void AppLogger_WhenDisabled_DoesNotCreateDirectoryOrFile()
    {
        AppLogger.IsLoggingEnabled = false;
        try
        {
            Assert.False(Directory.Exists(_testLogsDirectory));

            AppLogger.Info("This message should not be logged");
            AppLogger.Warn("Nor should this warning");
            AppLogger.Error("Nor this error");

            Assert.False(Directory.Exists(_testLogsDirectory));
            Assert.False(File.Exists(AppLogger.GetCurrentLogFilePath()));
        }
        finally
        {
            AppLogger.IsLoggingEnabled = true;
        }
    }
}
