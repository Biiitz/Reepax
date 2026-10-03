using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Reepax.Services.Storage;

/// <summary>
/// Central, thread-safe and non-blocking logging component for Reepax.
/// Writes timestamped log messages with log levels into daily rotating log files
/// under %AppData%\Reepax\logs via an asynchronous background channel with retry logic.
/// Never blocks callers (e.g. WPF UI thread) even on file lock collisions or slow disk I/O.
/// </summary>
public static class AppLogger
{
    private readonly record struct LogItem(string? FilePath, string? Content, TaskCompletionSource<bool>? FlushTcs);

    private static readonly Channel<LogItem> _logChannel = Channel.CreateUnbounded<LogItem>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    private static string? _customLogsDirectory;

    static AppLogger()
    {
        Task.Run(ProcessQueueAsync);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush(1000);
    }

    public static bool IsLoggingEnabled { get; set; } = false;

    public static string LogsDirectory
    {
        get => _customLogsDirectory ?? Path.Combine(SettingsService.AppDataDirectory, "logs");
        set => _customLogsDirectory = value;
    }

    /// <summary>
    /// Returns the path to the current rotating daily log file.
    /// Format: reepax_YYYY-MM-DD.log
    /// </summary>
    public static string GetCurrentLogFilePath()
    {
        var dir = LogsDirectory;
        return Path.Combine(dir, $"reepax_{DateTime.Now:yyyy-MM-dd}.log");
    }

    /// <summary>
    /// Writes a formatted entry asynchronously without blocking the calling thread.
    /// Format: [yyyy-MM-dd HH:mm:ss] [LEVEL] Message
    /// </summary>
    public static void Log(string message, string level = "INFO", Exception? ex = null)
    {
        if (!IsLoggingEnabled)
            return;

        try
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var line = FormatLogEntry(timestamp, level, message, ex);
            var logFilePath = GetCurrentLogFilePath();

            _logChannel.Writer.TryWrite(new LogItem(logFilePath, line, null));
        }
        catch
        {
            // Logging failure safety fallback (app must never crash due to logging failure)
        }
    }

    /// <summary>
    /// Flushes all queued log entries to disk and waits for completion (or timeout).
    /// </summary>
    public static void Flush(int timeoutMs = 3000)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_logChannel.Writer.TryWrite(new LogItem(null, null, tcs)))
        {
            tcs.Task.Wait(timeoutMs);
        }
    }

    private static async Task ProcessQueueAsync()
    {
        var reader = _logChannel.Reader;
        var batch = new List<LogItem>();

        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            batch.Clear();

            while (reader.TryRead(out var item))
            {
                batch.Add(item);
                if (batch.Count >= 200)
                    break;
            }

            if (batch.Count == 0)
                continue;

            string? currentFile = null;
            var sb = new StringBuilder();
            var pendingFlushTcs = new List<TaskCompletionSource<bool>>();

            foreach (var item in batch)
            {
                if (item.FlushTcs != null)
                {
                    if (currentFile != null && sb.Length > 0)
                    {
                        await WriteWithRetryAsync(currentFile, sb.ToString()).ConfigureAwait(false);
                        sb.Clear();
                        currentFile = null;
                    }
                    pendingFlushTcs.Add(item.FlushTcs);
                    continue;
                }

                if (string.IsNullOrEmpty(item.FilePath) || string.IsNullOrEmpty(item.Content))
                    continue;

                if (currentFile != null && !string.Equals(currentFile, item.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    await WriteWithRetryAsync(currentFile, sb.ToString()).ConfigureAwait(false);
                    sb.Clear();
                }

                currentFile = item.FilePath;
                sb.Append(item.Content);
            }

            if (currentFile != null && sb.Length > 0)
            {
                await WriteWithRetryAsync(currentFile, sb.ToString()).ConfigureAwait(false);
            }

            foreach (var tcs in pendingFlushTcs)
            {
                tcs.TrySetResult(true);
            }
        }
    }

    private static string FormatLogEntry(string timestamp, string level, string message, Exception? ex)
    {
        if (ex == null)
        {
            return $"[{timestamp}] [{level}] {message}{Environment.NewLine}";
        }

        var sb = new StringBuilder();
        sb.Append($"[{timestamp}] [{level}] {message}{Environment.NewLine}");
        sb.Append($"{ex.GetType().FullName}: {ex.Message}{Environment.NewLine}");
        if (!string.IsNullOrWhiteSpace(ex.StackTrace))
        {
            sb.Append($"{ex.StackTrace}{Environment.NewLine}");
        }
        if (ex.InnerException != null)
        {
            sb.Append($"---> Inner Exception: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}{Environment.NewLine}");
            if (!string.IsNullOrWhiteSpace(ex.InnerException.StackTrace))
            {
                sb.Append($"{ex.InnerException.StackTrace}{Environment.NewLine}");
            }
        }
        return sb.ToString();
    }

    private static async Task WriteWithRetryAsync(string filePath, string content, int maxRetries = 4)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            try { Directory.CreateDirectory(dir); } catch { }
        }

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    filePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    bufferSize: 4096,
                    useAsync: true);
                using var writer = new StreamWriter(stream, Encoding.UTF8);
                await writer.WriteAsync(content).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < maxRetries - 1 && (ex is IOException or UnauthorizedAccessException))
            {
                // Transient file lock collision (e.g. antivirus or log viewer), wait non-blockingly on background task
                await Task.Delay(15 * (attempt + 1)).ConfigureAwait(false);
            }
            catch
            {
                // Fallback below
                break;
            }
        }

        // Fallback: If primary log file is locked, write to fallback log file
        try
        {
            var fallbackPath = Path.Combine(LogsDirectory, $"reepax_fallback_{DateTime.Now:yyyy-MM-dd}.log");
            using var stream = new FileStream(
                fallbackPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 4096,
                useAsync: true);
            using var writer = new StreamWriter(stream, Encoding.UTF8);
            await writer.WriteAsync(content).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
        catch
        {
            // Logging must never crash the application
        }
    }

    /// <summary>
    /// Logs a debug message.
    /// </summary>
    public static void Debug(string message) => Log(message, "DEBUG");

    /// <summary>
    /// Logs an informational message.
    /// </summary>
    public static void Info(string message) => Log(message, "INFO");

    /// <summary>
    /// Logs a warning message, optionally with exception details.
    /// </summary>
    public static void Warn(string message, Exception? ex = null) => Log(message, "WARN", ex);

    /// <summary>
    /// Logs an error message, optionally with exception details and stack trace.
    /// </summary>
    public static void Error(string message, Exception? ex = null) => Log(message, "ERROR", ex);
}
