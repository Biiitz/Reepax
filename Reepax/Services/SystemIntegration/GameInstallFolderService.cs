using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using Reepax.Models;
using Reepax.Services.Extractor;
using Reepax.Services.Localization;
using Reepax.Services.Storage;

namespace Reepax.Services.SystemIntegration;

/// <summary>
/// Status of folder creation / validation.
/// </summary>
public enum GameInstallFolderStatus
{
    Created,
    AlreadyExists,
    Failed,
    Cancelled
}

/// <summary>
/// Result of folder creation.
/// </summary>
public record GameInstallFolderResult(GameInstallFolderStatus Status, string? Path, string? ErrorMessage = null);

/// <summary>
/// Service for automatic and manual creation of game installation folders
/// and copying the directory path to the Windows clipboard (e.g. for setup.exe).
/// </summary>
public static class GameInstallFolderService
{
    private static readonly Regex TagBracketsRegex = new(
        @"(?i)(?:\[[^\]]*(?:setup|update|patch|edition|dlc|multi\d*|v\d+|build|\d+bit|x64|x86|win\d*)[^\]]*\]|\([^\)]*(?:setup|update|patch|edition|dlc|multi\d*|v\d+|build|\d+bit|x64|x86|win\d*)[^\)]*\))",
        RegexOptions.Compiled);

    private static readonly Regex TrailingReleaseTagsRegex = new(
        @"(?i)[\s._-]+(?:setup|update|patch|release|edition|installer|standalone)(?:[\s._-]+|$).*$",
        RegexOptions.Compiled);

    private static readonly Regex TrailingVersionRegex = new(
        @"(?i)[\s._-]+(?:v\d+(?:\.\d+)*|build[.\s_-]*\d+)(?:[\s._-]+|$).*$",
        RegexOptions.Compiled);

    private static readonly HashSet<char> InvalidFolderChars = Path.GetInvalidFileNameChars()
        .Concat(Path.GetInvalidPathChars())
        .Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' })
        .Distinct()
        .ToHashSet();

    private static readonly HashSet<string> ReservedDosNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Determines the installation folder name for the package.
    /// Uses the same name as the in-app package (package.Name),
    /// filtering out unsupported filesystem characters (\ / : * ? " < > |).
    /// </summary>
    public static string GetGameInstallName(DownloadPackage? package)
    {
        if (package == null)
            return "Game";

        var name = package.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            var candidate = package.Items
                .Select(i => i.FileName)
                .FirstOrDefault(f => !string.IsNullOrWhiteSpace(f));

            name = candidate ?? "Game";
        }

        return SanitizeFolderName(name);
    }

    /// <summary>
    /// Cleans a folder name for the Windows filesystem.
    /// Preserves the in-app package name while filtering out
    /// invalid characters.
    /// </summary>
    public static string SanitizeFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Game";

        // Replace unsupported characters with spaces
        var chars = name.Select(c => InvalidFolderChars.Contains(c) ? ' ' : c).ToArray();
        var sanitized = new string(chars);

        // Collapse multiple whitespace characters
        sanitized = Regex.Replace(sanitized, @"\s+", " ").Trim();

        // Windows does not allow folder names ending in '.' or ' '
        sanitized = sanitized.TrimEnd('.', ' ');

        // Prevent directory traversal (e.g. "..")
        while (sanitized.Contains(".."))
        {
            sanitized = sanitized.Replace("..", ".");
        }
        sanitized = sanitized.Trim('.', ' ');

        if (string.IsNullOrWhiteSpace(sanitized))
            return "Game";

        var baseName = sanitized.Split('.')[0];
        if (ReservedDosNames.Contains(sanitized) || ReservedDosNames.Contains(baseName))
        {
            sanitized += "_Folder";
        }

        return sanitized;
    }

    /// <summary>
    /// Cleans raw text by stripping release tags, version numbers, and bracket tags.
    /// </summary>
    public static string CleanGameInstallTitle(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var name = UpdateDetector.ExtractGameName(text);
        if (string.IsNullOrWhiteSpace(name))
            name = text;

        // 1. Remove bracket tags like [Update] or (Patch)
        name = TagBracketsRegex.Replace(name, "").Trim();

        // 2. Remove trailing release tags
        name = TrailingReleaseTagsRegex.Replace(name, "").Trim();

        // 3. Remove trailing version indicators like .v2.1 or build 1234
        name = TrailingVersionRegex.Replace(name, "").Trim();

        // 4. Replace dots and underscores with spaces (if any remain)
        name = Regex.Replace(name, @"[_\.]+", " ").Trim();

        // 5. Collapse duplicate whitespace
        name = Regex.Replace(name, @"\s{2,}", " ").Trim(' ', '-', '_', ':');

        return name;
    }

    /// <summary>
    /// Returns the configured base directory (default: C:\Games or UserProfile\Games).
    /// </summary>
    public static string GetEffectiveBaseDirectory()
    {
        var configured = SettingsService.Instance.Settings.GameInstallDirectory;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        return Directory.Exists(systemDrive) 
            ? Path.Combine(systemDrive, "Games") 
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games");
    }

    /// <summary>
    /// Creates or validates the target folder for the game and copies the full path to the clipboard.
    /// Accurately detects whether the folder was newly created or already existed.
    /// </summary>
    public static GameInstallFolderResult CreateOrValidateGameInstallFolder(DownloadPackage package, string? customBaseDir = null)
    {
        if (package == null)
            return new GameInstallFolderResult(GameInstallFolderStatus.Failed, null, "No package specified");

        try
        {
            var gameName = GetGameInstallName(package);
            var baseDir = !string.IsNullOrWhiteSpace(customBaseDir)
                ? customBaseDir.Trim()
                : GetEffectiveBaseDirectory();

            if (!Directory.Exists(baseDir))
            {
                Directory.CreateDirectory(baseDir);
            }

            var targetPath = Path.Combine(baseDir, gameName);
            bool alreadyExists = Directory.Exists(targetPath);

            if (!alreadyExists)
            {
                Directory.CreateDirectory(targetPath);
            }

            // Copy to clipboard (unless in a pure headless test environment)
            if (!DownloadPersistenceService.IsTestEnvironment)
            {
                CopyToClipboard(targetPath);
            }

            AppLogger.Info($"[GameInstallFolderService] {(alreadyExists ? "Folder already exists" : "Folder newly created")}: '{targetPath}' for package '{package.Name}'");

            return new GameInstallFolderResult(
                alreadyExists ? GameInstallFolderStatus.AlreadyExists : GameInstallFolderStatus.Created,
                targetPath);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[GameInstallFolderService] Error creating game installation folder for '{package.Name}'", ex);
            return new GameInstallFolderResult(GameInstallFolderStatus.Failed, null, ex.Message);
        }
    }

    /// <summary>
    /// Creates the game installation target folder and copies the full path to clipboard.
    /// </summary>
    public static string? CreateAndCopyGameInstallFolder(DownloadPackage package, bool showNotification = true)
    {
        if (package == null)
            return null;

        var result = CreateOrValidateGameInstallFolder(package);
        if (result.Status is GameInstallFolderStatus.Created or GameInstallFolderStatus.AlreadyExists)
        {
            if (showNotification && !string.IsNullOrWhiteSpace(result.Path))
            {
                var msg = Loc.Format("Status_GameInstallFolderCreated", result.Path);
                SafeInvoke(() =>
                {
                    if (Application.Current?.MainWindow?.DataContext is ViewModels.MainViewModel vm)
                    {
                        vm.StatusSummary = msg;
                    }
                });
            }
            return result.Path;
        }

        return null;
    }

    /// <summary>
    /// Robustly copies text to the Windows clipboard with retries.
    /// </summary>
    public static bool CopyToClipboard(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        bool success = false;
        Action copyAction = () =>
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Clipboard.SetText(text);
                    success = true;
                    break;
                }
                catch
                {
                    Thread.Sleep(40);
                }
            }
        };

        try
        {
            var app = Application.Current;
            if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.Invoke(copyAction);
            }
            else
            {
                copyAction();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[GameInstallFolderService] Zwischenablage konnte nicht gesetzt werden: {ex.Message}");
        }

        return success;
    }

    private static void SafeInvoke(Action action)
    {
        try
        {
            var app = Application.Current;
            if (app?.Dispatcher != null && !app.Dispatcher.HasShutdownStarted && !app.Dispatcher.HasShutdownFinished)
            {
                if (!app.Dispatcher.CheckAccess())
                {
                    app.Dispatcher.BeginInvoke(action);
                }
                else
                {
                    action();
                }
            }
            else
            {
                action();
            }
        }
        catch { }
    }
}
