using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Reepax.Services.Storage;

/// <summary>
/// Central, thread-safe logging component for Reepax.
/// Writes timestamped log messages with log levels into daily rotating log files
/// under %AppData%\Reepax\logs with collision handling and retry logic.
/// </summary>
public static class AppLogger
{
    private static readonly object _lock = new();
    private static string? _customLogsDirectory;

    /// <summary>
    /// Whether file logging is active. When false, no log files or directories are created.
    /// </summary>
    public static bool IsLoggingEnabled { get; set; } = false;

    /// <summary>
    /// Path to the log directory in the user profile (%LocalAppData%\Reepax\logs).
    /// Can be overridden in tests if needed.
    /// </summary>
    public static string LogsDirectory
    {
        get => _customLogsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Reepax",
            "logs"
        );
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
    /// Writes a formatted entry thread-safely into the current log file.
    /// Format: [yyyy-MM-dd HH:mm:ss] [LEVEL] Message
    /// </summary>
    public static void Log(string message, string level = "INFO", Exception? ex = null)
    {
        if (!IsLoggingEnabled)
            return;

        try
        {
            var dir = LogsDirectory;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var line = FormatLogEntry(timestamp, level, message, ex);
            var logFilePath = GetCurrentLogFilePath();

            lock (_lock)
            {
                WriteWithRetry(logFilePath, line);
            }
        }
        catch
        {
            // Logging failure safety fallback (app must never crash due to logging failure)
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

    private static void WriteWithRetry(string filePath, string content, int maxRetries = 4)
    {
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    filePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, Encoding.UTF8);
                writer.Write(content);
                writer.Flush();
                return;
            }
            catch (Exception ex) when (attempt < maxRetries - 1 && (ex is IOException or UnauthorizedAccessException))
            {
                // Transient file lock collision (e.g. file lock by antivirus or log viewer), wait briefly
                Thread.Sleep(15 * (attempt + 1));
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
                FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, Encoding.UTF8);
            writer.Write(content);
            writer.Flush();
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
