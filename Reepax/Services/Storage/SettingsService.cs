using System;
using System.IO;
using System.Text.Json;
using Reepax.Models;

namespace Reepax.Services.Storage;

public class SettingsService
{
    private static readonly Lazy<SettingsService> _instance = new(() => new SettingsService());
    public static SettingsService Instance => _instance.Value;

    public static string AppDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Reepax"
    );

    public static string LogsDirectory => AppLogger.LogsDirectory;
    public static string IconsDirectory { get; } = Path.Combine(AppDataDirectory, "Icons");
    public static string WebView2Directory { get; } = Path.Combine(AppDataDirectory, "WebView2");

    private readonly string _settingsFilePath;
    private readonly string _backupFilePath;
    private readonly bool _isCustomPath;
    private readonly object _fileLock = new();
    private AppSettings _currentSettings = new();

    public AppSettings Settings => _currentSettings;

    public SettingsService(string? customSettingsFilePath = null)
    {
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
                AppLogger.Error("Fehler beim Erstellen der Anwendungsordner", ex);
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
            return;
        }

        lock (_fileLock)
        {
            bool loadedSuccessfully = false;

            if (File.Exists(_settingsFilePath))
            {
                try
                {
                    var raw = File.ReadAllText(_settingsFilePath);
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        var json = SecureAppDataStorage.DecryptString(raw);
                        var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                        if (loaded != null)
                        {
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
            if (!loadedSuccessfully && File.Exists(_backupFilePath))
            {
                try
                {
                    var backupRaw = File.ReadAllText(_backupFilePath);
                    if (!string.IsNullOrWhiteSpace(backupRaw))
                    {
                        var backupJson = SecureAppDataStorage.DecryptString(backupRaw);
                        var loadedFromBackup = JsonSerializer.Deserialize<AppSettings>(backupJson);
                        if (loadedFromBackup != null)
                        {
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

            if (!loadedSuccessfully && !File.Exists(_settingsFilePath) && !File.Exists(_backupFilePath))
            {
                _currentSettings = new AppSettings();
            }
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
            try
            {
                if (Directory.Exists(LogsDirectory))
                {
                    Directory.Delete(LogsDirectory, true);
                }
            }
            catch { }
        }
    }

    public void SaveSettings()
    {
        if (!_isCustomPath && DownloadPersistenceService.IsTestEnvironment) return;

        AppLogger.IsLoggingEnabled = _currentSettings.EnableFileLogging;

        lock (_fileLock)
        {
            string? tempFile = null;
            try
            {
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

    private static void MigrateFromRoamingIfNeeded()
    {
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

            // Migrate all files from Roaming to LocalAppData
            string[] filesToMigrate = { "settings.json", "settings.json.bak", "downloads.json", "downloads.json.bak", "history.json", "history.json.bak", "extensions.json", "adblock_whitelist.txt" };
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
