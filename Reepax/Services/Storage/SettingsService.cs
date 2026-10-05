using System;
using System.IO;
using System.Text.Json;
using Reepax.Models;

namespace Reepax.Services.Storage;

public class SettingsService
{
    private static readonly Lazy<SettingsService> _instance = new(() => new SettingsService());
    public static SettingsService Instance => _instance.Value;

    public static bool? PortableModeOverride { get; set; }

    public static bool IsPortableMode
    {
        get => PortableModeOverride ?? DetectPortableMode();
        set => PortableModeOverride = value;
    }

    public static string? AppDataDirectoryOverride { get; set; }

    public static string AppDataDirectory => AppDataDirectoryOverride ?? (IsPortableMode
        ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reepax"));

    public static string LogsDirectory => AppLogger.LogsDirectory;
    public static string IconsDirectory => Path.Combine(AppDataDirectory, "Icons");
    public static string WebView2Directory => Path.Combine(AppDataDirectory, "WebView2");

    public static bool DetectPortableMode(string? baseDirectory = null)
    {
        if (baseDirectory == null && DownloadPersistenceService.IsTestEnvironment)
        {
            return false;
        }

        try
        {
            // 1. Command-line args for --portable
            if (baseDirectory == null)
            {
                var args = Environment.GetCommandLineArgs();
                if (args != null)
                {
                    for (int i = 0; i < args.Length; i++)
                    {
                        if (string.Equals(args[i], "--portable", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }

            // 2. Presence of portable.txt OR .portable OR portable.dat in AppDomain.CurrentDomain.BaseDirectory
            var baseDir = baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory;
            if (!string.IsNullOrEmpty(baseDir))
            {
                if (File.Exists(Path.Combine(baseDir, "portable.txt")) ||
                    File.Exists(Path.Combine(baseDir, ".portable")) ||
                    File.Exists(Path.Combine(baseDir, "portable.dat")))
                {
                    return true;
                }

                // 3. Presence of an existing Data directory in AppDomain.CurrentDomain.BaseDirectory
                if (Directory.Exists(Path.Combine(baseDir, "Data")))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Fallback if environment or security permissions prevent inspection
        }

        return false;
    }

    private readonly string _settingsFilePath;
    private readonly string _backupFilePath;
    private readonly bool _isCustomPath;
    private readonly object _fileLock = new();
    private AppSettings _currentSettings = new();
    private volatile bool _hasSettingsLoadFailed;

    public bool HasSettingsLoadFailed => _hasSettingsLoadFailed;
    public static event Action<string>? OnSettingsWarning;

    public AppSettings Settings => _currentSettings;

    public SettingsService(string? customSettingsFilePath = null)
    {
        if (IsPortableMode)
        {
            try
            {
                Directory.CreateDirectory(AppDataDirectory);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Fehler beim Erstellen des Datenordners im Portabel-Modus", ex);
            }
        }

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
                if (!IsPortableMode)
                {
                    MigrateFromRoamingIfNeeded();
                }

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
                AppLogger.Error("Fehler beim Erstellen der Anwendungsordner", ex);
            }
            _settingsFilePath = Path.Combine(AppDataDirectory, "settings.json");
            _backupFilePath = Path.Combine(AppDataDirectory, "settings.json.bak");
        }

        if (!_isCustomPath)
        {
            MigratePortableSettingsIfNeeded();
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
                            loaded.ExtractionPasswords = AppSettings.SanitizeExtractionPasswords(loaded.ExtractionPasswords);
                            AppSettings.SanitizeSoundSettings(loaded);
                            _currentSettings = loaded;
                            loadedSuccessfully = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error("Fehler beim Laden der Einstellungen (Datei möglicherweise korrupt)", ex);

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
                            loadedFromBackup.ExtractionPasswords = AppSettings.SanitizeExtractionPasswords(loadedFromBackup.ExtractionPasswords);
                            AppSettings.SanitizeSoundSettings(loadedFromBackup);
                            _currentSettings = loadedFromBackup;
                            loadedSuccessfully = true;
                            AppLogger.Warn("Einstellungen erfolgreich aus Backup wiederhergestellt.");
                        }
                    }
                }
                catch (Exception backupEx)
                {
                    AppLogger.Error("Fehler beim Wiederherstellen der Einstellungen aus dem Backup", backupEx);
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
                AppLogger.Error("[SettingsService] KRITISCHER FEHLER / CRITICAL ERROR: settings.json oder Backup existiert, konnte aber nicht geladen werden. Speichern wird blockiert, um Datenverlust zu verhindern. / settings.json or backup exists but could not be read. Saving blocked to prevent data loss.");
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
    {
        try
        {
            if (Directory.Exists(LogsDirectory))
            {
                Directory.Delete(LogsDirectory, true);
            }
        }
        catch { }

        try
        {
            var appDataCrashLog = Path.Combine(AppDataDirectory, "crash.log");
            if (File.Exists(appDataCrashLog))
            {
                File.Delete(appDataCrashLog);
            }
        }
        catch { }
    }

    public void SaveSettings(bool force = false)
    {
        if (!_isCustomPath && DownloadPersistenceService.IsTestEnvironment) return;

        lock (_fileLock)
        {
            if (_hasSettingsLoadFailed && !force && (File.Exists(_settingsFilePath) || File.Exists(_backupFilePath)))
            {
                AppLogger.Warn("[SettingsService] Speichern abgebrochen: Initiales Laden der Einstellungen ist fehlgeschlagen. Vorhandene Einstellungsdateien werden geschützt und nicht überschrieben. / Save aborted: Initial settings load failed. Existing settings files are protected and will not be overwritten.");
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
                _currentSettings.ExtractionPasswords = AppSettings.SanitizeExtractionPasswords(_currentSettings.ExtractionPasswords);
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

                // Atomic write: write to temp file then replace/move to destination and preserve backup
                if (File.Exists(_settingsFilePath))
                {
                    try
                    {
                        File.Replace(tempFile, _settingsFilePath, _backupFilePath);
                    }
                    catch
                    {
                        try { File.Copy(_settingsFilePath, _backupFilePath, overwrite: true); } catch { }
                        File.Move(tempFile, _settingsFilePath, overwrite: true);
                    }
                }
                else
                {
                    File.Move(tempFile, _settingsFilePath, overwrite: true);
                }

                _hasSettingsLoadFailed = false;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Fehler beim Speichern der Einstellungen", ex);
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
        if (IsPortableMode)
            return;

        try
        {
            var roamingDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Reepax"
            );

            if (!Directory.Exists(roamingDir))
                return;

            if (!Directory.Exists(AppDataDirectory))
            {
                Directory.CreateDirectory(AppDataDirectory);
            }
            
            string[] filesToMigrate = { "settings.json", "settings.json.bak", "downloads.json", "downloads.json.bak", "history.json", "history.json.bak", "extensions.json" };
            foreach (var file in filesToMigrate)
            {
                var src = Path.Combine(roamingDir, file);
                var dest = Path.Combine(AppDataDirectory, file);
                if (File.Exists(src))
                {
                    bool shouldCopy = !File.Exists(dest);
                    if (!shouldCopy)
                    {
                        var srcInfo = new FileInfo(src);
                        var destInfo = new FileInfo(dest);
                        // If source has actual data and destination is empty/default, prioritize user's actual data
                        if (srcInfo.Length > destInfo.Length || destInfo.Length <= 10)
                        {
                            shouldCopy = true;
                        }
                    }

                    if (shouldCopy)
                    {
                        try { File.Copy(src, dest, overwrite: true); } catch { }
                    }
                }
            }

            // Migrate Extensions, Icons, and any subdirectories
            string[] dirsToMigrate = { "Extensions", "Icons" };
            foreach (var subDir in dirsToMigrate)
            {
                var srcSub = Path.Combine(roamingDir, subDir);
                var destSub = Path.Combine(AppDataDirectory, subDir);
                if (Directory.Exists(srcSub))
                {
                    try
                    {
                        CopyDirectory(srcSub, destSub);
                    }
                    catch { }
                }
            }

            // Clean up old Roaming directory completely so nothing is left in Roaming
            try
            {
                Directory.Delete(roamingDir, recursive: true);
            }
            catch { }
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"[SettingsService] Roaming migration note: {ex.Message}");
        }
    }

    private void MigratePortableSettingsIfNeeded()
    {
        if (!IsPortableMode || DownloadPersistenceService.IsTestEnvironment)
            return;

        try
        {
            if (File.Exists(_settingsFilePath))
                return;

            var localAppDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Reepax"
            );

            if (!Directory.Exists(localAppDataDir))
                return;

            string[] filesToMigrate = { "settings.json", "settings.json.bak", "downloads.json", "downloads.json.bak", "history.json", "history.json.bak", "extensions.json" };
            foreach (var file in filesToMigrate)
            {
                var src = Path.Combine(localAppDataDir, file);
                var dest = Path.Combine(AppDataDirectory, file);
                if (File.Exists(src) && !File.Exists(dest))
                {
                    try
                    {
                        File.Copy(src, dest, overwrite: false);
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[SettingsService] Fehler beim Migrieren bestehender Einstellungen in Portabel-Modus: {ex.Message}");
        }
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var dest = Path.Combine(targetDir, Path.GetFileName(file));
            if (!File.Exists(dest))
            {
                try { File.Copy(file, dest, overwrite: false); } catch { }
            }
        }
        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var destSub = Path.Combine(targetDir, Path.GetFileName(subDir));
            CopyDirectory(subDir, destSub);
        }
    }
}
