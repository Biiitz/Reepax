using System;
using System.IO;
using System.Text.Json;
using Reepax.Models;

namespace Reepax.Services.Storage;

public partial class SettingsService
{
    private static readonly Lazy<SettingsService> _instance = new(() => new SettingsService());
    public static SettingsService Instance => _instance.Value;

    public static string? AppDataDirectoryOverride { get; set; }

    public static string AppDataDirectory => AppDataDirectoryOverride ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reepax");

    public static string LogsDirectory => AppLogger.LogsDirectory;
    public static string IconsDirectory => Path.Combine(AppDataDirectory, "Icons");
    public static string WebView2Directory => Path.Combine(AppDataDirectory, "WebView2");

    private readonly string _settingsFilePath;
    private readonly string _backupFilePath;
    private readonly bool _isCustomPath;
    private readonly object _fileLock = new();
    private readonly System.Threading.Timer? _settingsDebounceTimer;
    private volatile bool _isSettingsDirty;
    private AppSettings _currentSettings = new();
    private volatile bool _hasSettingsLoadFailed;

    public bool HasSettingsLoadFailed => _hasSettingsLoadFailed;
    public static event Action<string>? OnSettingsWarning;

    public AppSettings Settings => _currentSettings;

    public SettingsService(string? customSettingsFilePath = null)
    {
        _settingsDebounceTimer = new System.Threading.Timer(OnSettingsDebounceTimerElapsed, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        if (!string.IsNullOrWhiteSpace(customSettingsFilePath))
        {
            _settingsFilePath = customSettingsFilePath;
            _isCustomPath = true;
            var dir = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }
            _backupFilePath = _settingsFilePath + ".bak";
        }
        else
        {
            _isCustomPath = false;
            try
            {
                MigrateFromRoamingIfNeeded();

                Directory.CreateDirectory(AppDataDirectory);
                Directory.CreateDirectory(IconsDirectory);
                Directory.CreateDirectory(WebView2Directory);

                // Clean up unused legacy Temp folder if present
                var legacyTemp = Path.Combine(AppDataDirectory, "Temp");
                if (Directory.Exists(legacyTemp))
                {
                    try { Directory.Delete(legacyTemp, true); } catch { }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Could not create application directories", ex);
            }
            _settingsFilePath = Path.Combine(AppDataDirectory, "settings.json");
            _backupFilePath = Path.Combine(AppDataDirectory, "settings.json.bak");
        }

        LoadSettings();
    }

    public void LoadSettings()
    {
        if (!_isCustomPath && DownloadPersistenceService.IsTestEnvironment)
        {
            _currentSettings = new AppSettings();
            _hasSettingsLoadFailed = false;
            return;
        }

        lock (_fileLock)
        {
            bool loadedSuccessfully = false;
            bool primaryExists = File.Exists(_settingsFilePath);
            bool backupExists = File.Exists(_backupFilePath);

            if (primaryExists)
            {
                try
                {
                    var raw = DownloadPersistenceService.ReadFileWithRetry(_settingsFilePath);
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        var json = SecureAppDataStorage.DecryptString(raw);
                        var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                        if (loaded != null)
                        {
                            loaded.ColumnOrder = AppSettings.SanitizeColumnOrder(loaded.ColumnOrder);
                            ArchivePasswordEntry.NormalizeSettings(loaded);
                            AppSettings.SanitizeSoundSettings(loaded);
                            _currentSettings = loaded;
                            loadedSuccessfully = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error("Failed to load settings; the file may be damaged", ex);

                    // Retain backup of corrupt settings file for diagnostics/recovery
                    try
                    {
                        var corruptBackup = _settingsFilePath + $".corrupt_{DateTime.Now:yyyyMMdd_HHmmss}.bak";
                        File.Copy(_settingsFilePath, corruptBackup, overwrite: true);
                    }
                    catch { }
                }
            }

            // If loading primary settings failed or file did not exist, attempt to load from last known good backup
            if (!loadedSuccessfully && backupExists)
            {
                try
                {
                    var backupRaw = DownloadPersistenceService.ReadFileWithRetry(_backupFilePath);
                    if (!string.IsNullOrWhiteSpace(backupRaw))
                    {
                        var backupJson = SecureAppDataStorage.DecryptString(backupRaw);
                        var loadedFromBackup = JsonSerializer.Deserialize<AppSettings>(backupJson);
                        if (loadedFromBackup != null)
                        {
                            loadedFromBackup.ColumnOrder = AppSettings.SanitizeColumnOrder(loadedFromBackup.ColumnOrder);
                            ArchivePasswordEntry.NormalizeSettings(loadedFromBackup);
                            AppSettings.SanitizeSoundSettings(loadedFromBackup);
                            _currentSettings = loadedFromBackup;
                            loadedSuccessfully = true;
                            AppLogger.Warn("Settings successfully restored from backup.");
                        }
                    }
                }
                catch (Exception backupEx)
                {
                    AppLogger.Error("Could not restore settings from backup", backupEx);
                }
            }

            if (loadedSuccessfully)
            {
                _hasSettingsLoadFailed = false;
            }
            else if (primaryExists || backupExists)
            {
                _hasSettingsLoadFailed = true;
                _currentSettings = new AppSettings();
                AppLogger.Error("[SettingsService] settings.json or backup exists but could not be read. Saving blocked to prevent data loss.");
                NotifyUserLoadFailure();
            }
            else
            {
                _hasSettingsLoadFailed = false;
                _currentSettings = new AppSettings();
            }

            AppSettings.SanitizeSoundSettings(_currentSettings);
        }

        // Ensure default download directory is set (NOT created eagerly – folders are
        // only created when a download actually starts, so no empty folders appear)
        try
        {
            if (string.IsNullOrWhiteSpace(_currentSettings.DefaultDownloadDirectory))
            {
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                _currentSettings.DefaultDownloadDirectory = Path.Combine(profile, "Downloads");
            }
        }
        catch { }

        // Sync logger state with loaded settings
        AppLogger.IsLoggingEnabled = _currentSettings.EnableFileLogging;
        if (!_currentSettings.EnableFileLogging)
        {
            CleanupLogFiles();
        }
    }

    /// <summary>
    /// Removes rotating logs and crash log files from disk when file logging is disabled.
    /// </summary>
    public static void CleanupLogFiles()
        => CleanupLogFiles(LogsDirectory, AppDataDirectory);

    internal static void CleanupLogFiles(string logsDirectory, string appDataDirectory)
    {
        try
        {
            if (Directory.Exists(logsDirectory))
            {
                Directory.Delete(logsDirectory, true);
            }
        }
        catch { }

        try
        {
            var appDataCrashLog = Path.Combine(appDataDirectory, "crash.log");
            if (File.Exists(appDataCrashLog))
            {
                File.Delete(appDataCrashLog);
            }
        }
        catch { }
    }

    /// <summary>
    /// Schedules a debounced save of the settings in the background.
    /// Prevents blocking the UI thread during rapid interactions (such as filter switching or column resizing).
    /// </summary>
    public void RequestSaveSettings(int delayMs = 350)
    {
        if (!_isCustomPath && DownloadPersistenceService.IsTestEnvironment)
        {
            SaveSettings(force: false);
            return;
        }

        _isSettingsDirty = true;
        try
        {
            _settingsDebounceTimer?.Change(delayMs, System.Threading.Timeout.Infinite);
        }
        catch (ObjectDisposedException) { }
    }

    private void OnSettingsDebounceTimerElapsed(object? state)
    {
        if (!_isSettingsDirty) return;
        _isSettingsDirty = false;
        try
        {
            SaveSettings(force: false);
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"[SettingsService] Debounced save failed", ex);
        }
    }

    public void SaveSettings(bool force = false)
    {
        _isSettingsDirty = false;
        if (!_isCustomPath && DownloadPersistenceService.IsTestEnvironment) return;

        lock (_fileLock)
        {
            if (_hasSettingsLoadFailed && !force && (File.Exists(_settingsFilePath) || File.Exists(_backupFilePath)))
            {
                AppLogger.Warn("[SettingsService] Save aborted: Initial settings load failed. Existing settings files are protected and will not be overwritten.");
                return;
            }

            // If a force save was explicitly requested after a previous failure, create an emergency backup before overwriting
            if (_hasSettingsLoadFailed && force && File.Exists(_settingsFilePath))
            {
                try
                {
                    var emergencyBak = _settingsFilePath + $".emergency_{DateTime.Now:yyyyMMdd_HHmmss}.bak";
                    File.Copy(_settingsFilePath, emergencyBak, overwrite: true);
                    _hasSettingsLoadFailed = false;
                }
                catch { }
            }

            AppLogger.IsLoggingEnabled = _currentSettings.EnableFileLogging;

            string? tempFile = null;
            try
            {
                EnsureArchivePasswordIndex(validateEntries: true);
                _currentSettings.SecurityDescriptor = "made by Biiitz";
                _currentSettings.EngineSignature = "made by Biiitz";
                var json = JsonSerializer.Serialize(_currentSettings, new JsonSerializerOptions { WriteIndented = true });
                var encrypted = SecureAppDataStorage.EncryptString(json);
                var dir = Path.GetDirectoryName(_settingsFilePath) ?? AppDataDirectory;
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                tempFile = _settingsFilePath + ".tmp";
                File.WriteAllText(tempFile, encrypted);

                ProtectedStorageBackup.Update<AppSettings>(_settingsFilePath);
                File.Move(tempFile, _settingsFilePath, overwrite: true);

                _hasSettingsLoadFailed = false;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Could not save settings", ex);
            }
            finally
            {
                if (tempFile != null)
                {
                    try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                }
            }
        }
    }

    private static void NotifyUserLoadFailure()
    {
        if (DownloadPersistenceService.IsTestEnvironment) return;

        try
        {
            var title = Localization.Loc.Get("Dialog_SettingsLoadFailedTitle");
            var message = Localization.Loc.Get("Dialog_SettingsLoadFailedMessage");

            if (string.IsNullOrWhiteSpace(title) || title == "Dialog_SettingsLoadFailedTitle")
            {
                title = "Reepax - Warnung – Einstellungs-Datei gesperrt";
            }
            if (string.IsNullOrWhiteSpace(message) || message == "Dialog_SettingsLoadFailedMessage")
            {
                message = "Die Einstellungen ('settings.json') konnten nicht geladen werden, da die Datei gesperrt oder beschädigt ist.\n\n" +
                          "Um Datenverlust zu verhindern, wurde das automatische Überschreiben der Einstellungen deaktiviert.\n" +
                          "Bitte stellen Sie sicher, dass keine andere Anwendung (wie ein Virenscanner oder Editor) die Datei sperrt, und starten Sie Reepax neu.\n\n" +
                          "The settings ('settings.json') could not be loaded because the file is locked or corrupt.\n\n" +
                          "To prevent data loss, automatic overwriting of settings has been disabled.\n" +
                          "Please ensure no other application is locking the file, and restart Reepax.";
            }

            OnSettingsWarning?.Invoke(message);

            var app = System.Windows.Application.Current;
            if (app?.Dispatcher != null && !app.Dispatcher.HasShutdownStarted && !app.Dispatcher.HasShutdownFinished)
            {
                app.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        System.Windows.MessageBox.Show(
                            message,
                            title,
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Warning);
                    }
                    catch { }
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }
        catch { }
    }

    private static void MigrateFromRoamingIfNeeded()
    {
        var roamingDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Reepax"
        );
        MigrateFromRoamingInternal(roamingDir, AppDataDirectory);
    }

    internal static bool MigrateFromRoamingInternal(string roamingDir, string targetDir)
    {
        if (string.IsNullOrWhiteSpace(roamingDir) || string.IsNullOrWhiteSpace(targetDir))
            return false;

        try
        {
            roamingDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(roamingDir));
            targetDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDir));
            if (string.Equals(roamingDir, targetDir, StringComparison.OrdinalIgnoreCase) ||
                roamingDir.StartsWith(targetDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                targetDir.StartsWith(roamingDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!Directory.Exists(roamingDir))
                return true;

            Directory.CreateDirectory(targetDir);
            if (IsMigrationLink(roamingDir) || IsMigrationLink(targetDir)) return false;

            bool allSucceeded = true;
            foreach (var src in Directory.GetFiles(roamingDir))
            {
                var file = Path.GetFileName(src);
                if (!Array.Exists(RoamingFilesToMigrate, name => string.Equals(name, file, StringComparison.OrdinalIgnoreCase)) ||
                    !CopyMigrationFile(src, Path.Combine(targetDir, file)))
                    allSucceeded = false;
            }

            foreach (var srcSub in Directory.GetDirectories(roamingDir))
            {
                var subDir = Path.GetFileName(srcSub);
                if (!Array.Exists(RoamingDirectoriesToMigrate, name => string.Equals(name, subDir, StringComparison.OrdinalIgnoreCase)) ||
                    !CopyDirectory(srcSub, Path.Combine(targetDir, subDir)))
                    allSucceeded = false;
            }

            if (allSucceeded)
                allSucceeded = RemoveMigratedDirectory(roamingDir, targetDir, isRoot: true);

            if (!allSucceeded)
                AppLogger.Warn("[SettingsService] Roaming data retained because migration is incomplete or conflicting.");

            return allSucceeded;
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"[SettingsService] Roaming migration note", ex);
            return false;
        }
    }

    private static readonly string[] RoamingFilesToMigrate =
        { "settings.json", "settings.json.bak", "downloads.json", "downloads.json.bak", "history.json", "history.json.bak", "extensions.json" };
    private static readonly string[] RoamingDirectoriesToMigrate = { "Extensions", "Icons" };

    private static bool IsMigrationLink(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool CopyMigrationFile(string source, string target)
    {
        string? temporary = null;
        try
        {
            if (IsMigrationLink(source) || (File.Exists(target) && IsMigrationLink(target))) return false;
            using var input = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (File.Exists(target))
            {
                using var existing = File.Open(target, FileMode.Open, FileAccess.Read, FileShare.Read);
                // A different destination remains authoritative, even if it is smaller or unreadable.
                return MigrationBytesEqual(input, existing);
            }

            temporary = Path.Combine(Path.GetDirectoryName(target)!, $".migration-{Guid.NewGuid():N}.tmp");
            using (var staged = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                input.CopyTo(staged);
                staged.Flush(flushToDisk: true);
                if (!MigrationBytesEqual(input, staged))
                    throw new IOException("Migration copy verification failed.");
            }
            // Publication must also refuse a destination created after the existence check.
            File.Move(temporary, target);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[SettingsService] Migration copy failed: {ex.GetType().Name}");
            return false;
        }
        finally
        {
            if (temporary != null)
            {
                try { File.Delete(temporary); } catch { }
            }
        }
    }

    private static bool MigrationBytesEqual(Stream first, Stream second)
    {
        if (first.Length != second.Length) return false;
        first.Position = second.Position = 0;
        Span<byte> firstBuffer = stackalloc byte[4096];
        Span<byte> secondBuffer = stackalloc byte[4096];
        long remaining = first.Length;
        while (remaining > 0)
        {
            int count = (int)Math.Min(remaining, firstBuffer.Length);
            first.ReadExactly(firstBuffer[..count]);
            second.ReadExactly(secondBuffer[..count]);
            if (!firstBuffer[..count].SequenceEqual(secondBuffer[..count])) return false;
            remaining -= count;
        }
        return true;
    }

    private static bool CopyDirectory(string sourceDir, string targetDir)
    {
        try
        {
            Directory.CreateDirectory(targetDir);
            if (IsMigrationLink(sourceDir) || IsMigrationLink(targetDir)) return false;
            bool allFilesSucceeded = true;

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var dest = Path.Combine(targetDir, Path.GetFileName(file));
                if (!CopyMigrationFile(file, dest))
                    allFilesSucceeded = false;
            }

            foreach (var subDir in Directory.GetDirectories(sourceDir))
            {
                var destSub = Path.Combine(targetDir, Path.GetFileName(subDir));
                if (!CopyDirectory(subDir, destSub))
                {
                    allFilesSucceeded = false;
                }
            }

            return allFilesSucceeded;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[SettingsService] Failed to copy directory '{sourceDir}'", ex);
            return false;
        }
    }

    private static bool RemoveMigratedDirectory(string sourceDir, string targetDir, bool isRoot = false)
    {
        // Recheck each source before deletion; never recursively remove unexamined data.
        if (IsMigrationLink(sourceDir) || IsMigrationLink(targetDir)) return false;
        foreach (string source in Directory.GetFiles(sourceDir))
        {
            string file = Path.GetFileName(source);
            if (isRoot && !Array.Exists(RoamingFilesToMigrate, name => string.Equals(name, file, StringComparison.OrdinalIgnoreCase))) return false;
            string target = Path.Combine(targetDir, file);
            if (IsMigrationLink(source) || IsMigrationLink(target)) return false;
            using (var input = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = File.Open(target, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (!MigrationBytesEqual(input, output)) return false;
            }
            File.Delete(source);
        }
        foreach (string directory in Directory.GetDirectories(sourceDir))
        {
            string name = Path.GetFileName(directory);
            if (isRoot && !Array.Exists(RoamingDirectoriesToMigrate, entry => string.Equals(entry, name, StringComparison.OrdinalIgnoreCase))) return false;
            if (!RemoveMigratedDirectory(directory, Path.Combine(targetDir, name))) return false;
        }
        Directory.Delete(sourceDir);
        return true;
    }
}
