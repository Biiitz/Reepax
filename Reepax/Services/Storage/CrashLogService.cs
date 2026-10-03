using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace Reepax.Services.Storage;

/// <summary>
/// Result containing details of a crash logging operation.
/// </summary>
public class CrashLogResult
{
    public bool Success { get; set; }
    public string PrimaryLogPath { get; set; } = string.Empty;
    public List<string> SavedPaths { get; set; } = new();
    public string? ErrorMessage { get; set; }
    public bool LoggingDisabled { get; set; }
}

/// <summary>
/// Resilient service for recording fatal and unhandled exceptions to disk across multiple locations
/// (including Windows AppData and portable mode directories) with immediate disk flushing.
/// </summary>
public static class CrashLogService
{
    private static readonly object _fileLock = new();

    /// <summary>
    /// Gets whether file logging and error reporting are currently enabled in application settings.
    /// </summary>
    public static bool IsLoggingEnabled
    {
        get
        {
            try
            {
                return SettingsService.Instance.Settings.EnableFileLogging;
            }
            catch
            {
                return AppLogger.IsLoggingEnabled;
            }
        }
    }

    /// <summary>
    /// Formats and records crash details to all designated crash log locations if logging is enabled.
    /// </summary>
    public static CrashLogResult LogCrash(string source, Exception ex, bool forceWrite = false)
    {
        var result = new CrashLogResult();

        if (!forceWrite && !IsLoggingEnabled)
        {
            result.Success = false;
            result.LoggingDisabled = true;
            result.ErrorMessage = "Logging is disabled in application settings.";
            result.PrimaryLogPath = string.Empty;

            try
            {
                AppLogger.Log($"[CRASH] [{source}] {ex.Message}", "CRITICAL", ex);
            }
            catch
            {
                // Logging must never crash the crash handler
            }

            return result;
        }

        var reportText = FormatCrashReport(source, ex);

        var targetPaths = ResolveTargetPaths();
        var successfulPaths = new List<string>();

        foreach (var path in targetPaths)
        {
            if (TryWriteCrashLog(path, reportText))
            {
                successfulPaths.Add(path);
            }
        }

        // If all primary targets failed, attempt emergency fallback in %TEMP%
        if (successfulPaths.Count == 0)
        {
            var fallbackTempPath = Path.Combine(Path.GetTempPath(), "Reepax", "crash.log");
            if (TryWriteCrashLog(fallbackTempPath, reportText))
            {
                successfulPaths.Add(fallbackTempPath);
            }
        }

        result.SavedPaths = successfulPaths;
        result.Success = successfulPaths.Count > 0;
        result.PrimaryLogPath = successfulPaths.Count > 0
            ? successfulPaths[0]
            : Path.Combine(SettingsService.AppDataDirectory, "crash.log");

        if (!result.Success)
        {
            result.ErrorMessage = "Failed to write crash log to all candidate paths.";
        }

        // Also record to rotating daily log via AppLogger if available
        try
        {
            AppLogger.Log($"[CRASH] [{source}] {ex.Message}", "CRITICAL", ex);
        }
        catch
        {
            // Logging must never crash the crash handler
        }

        return result;
    }

    /// <summary>
    /// Resolves target file paths for crash logging based on current environment and portable mode.
    /// </summary>
    public static List<string> ResolveTargetPaths()
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfValid(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && seen.Add(path))
            {
                paths.Add(path);
            }
        }

        // 1. Logs Directory (e.g. %LOCALAPPDATA%\Reepax\logs\crash.log or BaseDir\Data\logs\crash.log)
        try
        {
            var logsDir = SettingsService.LogsDirectory;
            if (!string.IsNullOrEmpty(logsDir))
            {
                AddIfValid(Path.Combine(logsDir, "crash.log"));
            }
        }
        catch { }

        // 2. Active AppData Directory (e.g. %LOCALAPPDATA%\Reepax\crash.log or BaseDir\Data\crash.log)
        try
        {
            var appDataDir = SettingsService.AppDataDirectory;
            if (!string.IsNullOrEmpty(appDataDir))
            {
                AddIfValid(Path.Combine(appDataDir, "crash.log"));
            }
        }
        catch { }

        // 3. Portable mode targets (strictly isolated to the portable base directory)
        if (SettingsService.IsPortableMode)
        {
            // Base directory (next to executable) for direct visibility
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                if (!string.IsNullOrEmpty(baseDir))
                {
                    AddIfValid(Path.Combine(baseDir, "crash.log"));
                }
            }
            catch { }
        }

        return paths;
    }

    /// <summary>
    /// Writes content to the specified file path atomically with explicit disk flushing.
    /// </summary>
    public static bool TryWriteCrashLog(string filePath, string content)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            lock (_fileLock)
            {
                using var fileStream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(fileStream, Encoding.UTF8);
                writer.Write(content);
                writer.Flush();
                fileStream.Flush(flushToDisk: true);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Generates a standardized, detailed crash report string.
    /// </summary>
    public static string FormatCrashReport(string source, Exception ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine("================================================================================");
        sb.AppendLine($"[CRASH REPORT] {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Source:       {source}");
        sb.AppendLine($"App Version:  {GetAppVersion()}");
        sb.AppendLine($"OS Version:   {Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})");
        sb.AppendLine($"Runtime:      .NET {Environment.Version}");
        sb.AppendLine($"Portable:     {SettingsService.IsPortableMode}");
        sb.AppendLine($"Base Dir:     {AppDomain.CurrentDomain.BaseDirectory}");
        sb.AppendLine($"AppData Dir:  {SettingsService.AppDataDirectory}");
        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine($"Exception:    {ex.GetType().FullName}: {ex.Message}");
        sb.AppendLine("Stack Trace:");
        sb.AppendLine(ex.StackTrace ?? "  (No stack trace available)");

        var inner = ex.InnerException;
        int depth = 1;
        while (inner != null && depth <= 10)
        {
            sb.AppendLine("--------------------------------------------------------------------------------");
            sb.AppendLine($"Inner Exception [{depth}]: {inner.GetType().FullName}: {inner.Message}");
            sb.AppendLine("Stack Trace:");
            sb.AppendLine(inner.StackTrace ?? "  (No stack trace available)");
            inner = inner.InnerException;
            depth++;
        }

        sb.AppendLine("================================================================================");
        sb.AppendLine();
        return sb.ToString();
    }

    private static string GetAppVersion()
    {
        try
        {
            return Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
                   ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                   ?? "1.0.0";
        }
        catch
        {
            return "1.0.0";
        }
    }
}
