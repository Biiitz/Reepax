using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Reepax.Services.Storage;

namespace Reepax.Services.Browser;

/// <summary>
/// An unpacked WebExtension (folder containing manifest.json) from the extensions directory.
/// </summary>
public class BrowserExtensionInfo
{
    /// <summary>Absolute path to the extension folder.</summary>
    public string FolderPath { get; set; } = string.Empty;

    /// <summary>Folder name = stable key for activation state.</summary>
    public string FolderName { get; set; } = string.Empty;

    internal string? ArchiveName { get; set; }
    internal string? ArchiveHash { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Short name from manifest (short_name), if present.</summary>
    public string? ShortName { get; set; }

    public string Version { get; set; } = string.Empty;

    /// <summary>Absolute path to icon file (largest manifest icon), if present.</summary>
    public string? IconPath { get; set; }

    /// <summary>Relative options page from manifest (options_ui.page / options_page), if present.</summary>
    public string? OptionsPage { get; set; }

    /// <summary>Relative popup page from manifest (action.default_popup / browser_action.default_popup), if present.</summary>
    public string? PopupPage { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>ID assigned by WebView2 after AddBrowserExtensionAsync (for chrome-extension:// URLs).</summary>
    public string? InstalledExtensionId { get; set; }

    /// <summary>Installation error (e.g. incompatible manifest), if any occurred.</summary>
    public string? InstallError { get; set; }

    /// <summary>WebView2 handle of the installed extension (for EnableAsync/RemoveAsync).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public CoreWebView2BrowserExtension? NativeExtension { get; set; }

    public string OptionsPageUrl =>
        !string.IsNullOrWhiteSpace(InstalledExtensionId) && !string.IsNullOrWhiteSpace(OptionsPage)
            ? $"chrome-extension://{InstalledExtensionId}/{OptionsPage.TrimStart('/')}"
            : string.Empty;

    public string PopupPageUrl =>
        !string.IsNullOrWhiteSpace(InstalledExtensionId) && !string.IsNullOrWhiteSpace(PopupPage)
            ? $"chrome-extension://{InstalledExtensionId}/{PopupPage.TrimStart('/')}"
            : string.Empty;

    public string PopupOrOptionsUrl =>
        !string.IsNullOrWhiteSpace(PopupPageUrl)
            ? PopupPageUrl
            : OptionsPageUrl;

    public bool HasPage => !string.IsNullOrWhiteSpace(PopupPage) || !string.IsNullOrWhiteSpace(OptionsPage);

    /// <summary>Whether this extension is a redirect or ad blocker.</summary>
    public bool IsBlocker => BrowserExtensionService.IsBlockerExtension(this);
}

/// <summary>
/// Persisted state for an extension (enabled status + assigned WebView2 extension ID).
/// </summary>
public class ExtensionSavedState
{
    public bool IsEnabled { get; set; } = true;
    public string? ExtensionId { get; set; }
    public string? ArchiveName { get; set; }
    public string? ArchiveHash { get; set; }
    public Dictionary<string, string>? RemovedArchives { get; set; }
    public bool RemovalPending { get; set; }
}
public partial class BrowserExtensionService
{
    private static readonly Lazy<BrowserExtensionService> _instance = new(() => new BrowserExtensionService());
    public static BrowserExtensionService Instance => _instance.Value;

    public static string ExtensionsDirectory => Path.Combine(SettingsService.AppDataDirectory, "Extensions");

    private static string StateFilePath => Path.Combine(SettingsService.AppDataDirectory, "extensions.json");

    private static readonly JsonDocumentOptions _tolerantJsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>All discovered extensions (both enabled and disabled).</summary>
    public List<BrowserExtensionInfo> Extensions { get; private set; } = new();

    /// <summary>
    /// Returns true if the extension is an ad blocker or redirect blocker.
    /// </summary>
    public static bool IsBlockerExtension(BrowserExtensionInfo ext)
    {
        var name = ext.Name ?? string.Empty;
        var folder = ext.FolderName ?? string.Empty;

        return name.Contains("block", StringComparison.OrdinalIgnoreCase) ||
               folder.Contains("block", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("redirect", StringComparison.OrdinalIgnoreCase) ||
               folder.Contains("redirect", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("shield", StringComparison.OrdinalIgnoreCase) ||
               folder.Contains("shield", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("adguard", StringComparison.OrdinalIgnoreCase) ||
               folder.Contains("adguard", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("popup", StringComparison.OrdinalIgnoreCase) ||
               folder.Contains("popup", StringComparison.OrdinalIgnoreCase);
    }

    internal BrowserExtensionService()
    {
        try
        {
            Directory.CreateDirectory(ExtensionsDirectory);
            Scan();
        }
        catch (Exception ex)
        {
            AppLogger.Error("[Extensions] Failed to initialize extensions folder", ex);
        }
    }

    /// <summary>
    /// Finds the directory containing manifest.json within topDir (either topDir itself or a subdirectory).
    /// </summary>
    private static string? FindExtensionFolder(string topDir)
    {
        if (File.Exists(Path.Combine(topDir, "manifest.json")))
            return topDir;

        try
        {
            var manifestFiles = Directory.EnumerateFiles(topDir, "manifest.json", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    MatchCasing = MatchCasing.CaseInsensitive
                })
                .Where(p =>
                {
                    var relative = Path.GetRelativePath(topDir, p);
                    return !relative.Contains("node_modules", StringComparison.OrdinalIgnoreCase) &&
                           !relative.Contains(".git", StringComparison.OrdinalIgnoreCase) &&
                           !relative.Contains("__MACOSX", StringComparison.OrdinalIgnoreCase) &&
                           !relative.Contains("test", StringComparison.OrdinalIgnoreCase) &&
                           !relative.Contains("spec", StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(p => p.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length)
                .ToList();

            if (manifestFiles.Count > 0)
            {
                return Path.GetDirectoryName(manifestFiles[0]);
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Rescans the extensions folder (e.g. after the user adds folders or archives).
    /// </summary>
    internal void Scan() => Scan(LoadSavedStates());

    private void Scan(Dictionary<string, ExtensionSavedState> savedStates)
    {
        var found = new List<BrowserExtensionInfo>();

        try
        {
            ExtractArchives(savedStates);
            if (!Directory.Exists(ExtensionsDirectory))
            {
                Extensions = found;
                return;
            }

            foreach (var topDir in Directory.GetDirectories(ExtensionsDirectory))
            {
                try
                {
                    var topDirName = Path.GetFileName(topDir);
                    if (topDirName.StartsWith(ArchiveWorkPrefix, StringComparison.OrdinalIgnoreCase) ||
                        (File.GetAttributes(topDir) & FileAttributes.ReparsePoint) != 0)
                        continue;
                    if (savedStates.TryGetValue(topDirName, out var state) && state.RemovalPending)
                        continue;
                    var extensionFolder = FindExtensionFolder(topDir);
                    if (extensionFolder == null)
                        continue;

                    var manifestPath = Path.Combine(extensionFolder, "manifest.json");
                    if (!File.Exists(manifestPath))
                        continue;

                    var info = ParseManifest(extensionFolder, manifestPath);
                    if (info == null)
                        continue;

                    info.FolderName = topDirName;
                    if (state != null)
                    {
                        info.ArchiveName = state.ArchiveName;
                        info.ArchiveHash = state.ArchiveHash;
                    }

                    if (state != null)
                    {
                        info.IsEnabled = state.IsEnabled;
                    }
                    else if (state == null && savedStates.TryGetValue(info.Name, out var stateByName) &&
                        !stateByName.RemovalPending && stateByName.RemovedArchives == null)
                    {
                        info.IsEnabled = stateByName.IsEnabled;
                    }

                    if (!found.Any(e => string.Equals(e.FolderPath, info.FolderPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        found.Add(info);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"[Extensions] Error reading extension in '{topDir}'", ex);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("[Extensions] Failed to scan extensions folder", ex);
        }

        Extensions = found;
    }

    public static BrowserExtensionInfo? ParseManifest(string folderPath, string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath))
                return null;

            var rawText = File.ReadAllText(manifestPath).Trim();
            using var doc = JsonDocument.Parse(rawText, _tolerantJsonOptions);
            var root = doc.RootElement;

            var folderName = Path.GetFileName(folderPath);
            var info = new BrowserExtensionInfo
            {
                FolderPath = folderPath,
                FolderName = folderName,
                Name = folderName
            };

            if (root.TryGetProperty("name", out var nameProp))
            {
                var resolved = ResolveLocalizedString(folderPath, nameProp.GetString(), root);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    info.Name = resolved;
                }
            }

            if (info.Name.StartsWith("__MSG_", StringComparison.Ordinal))
            {
                info.Name = folderName;
            }

            if (root.TryGetProperty("short_name", out var shortNameProp))
            {
                var resolvedShort = ResolveLocalizedString(folderPath, shortNameProp.GetString(), root);
                if (!string.IsNullOrWhiteSpace(resolvedShort))
                {
                    info.ShortName = resolvedShort;
                }
            }

            if (root.TryGetProperty("version", out var versionProp))
            {
                info.Version = versionProp.GetString() ?? string.Empty;
            }

            // Options page: options_ui.page (MV3) or options_page (MV2)
            if (root.TryGetProperty("options_ui", out var optionsUi) &&
                optionsUi.ValueKind == JsonValueKind.Object &&
                optionsUi.TryGetProperty("page", out var optionsPageProp))
            {
                info.OptionsPage = optionsPageProp.GetString();
            }
            else if (root.TryGetProperty("options_page", out var legacyOptionsProp))
            {
                info.OptionsPage = legacyOptionsProp.GetString();
            }

            // Popup page: action.default_popup (MV3) or browser_action.default_popup (MV2)
            if (root.TryGetProperty("action", out var actionProp) &&
                actionProp.ValueKind == JsonValueKind.Object &&
                actionProp.TryGetProperty("default_popup", out var defaultPopupProp))
            {
                info.PopupPage = defaultPopupProp.GetString();
            }
            else if (root.TryGetProperty("browser_action", out var browserActionProp) &&
                     browserActionProp.ValueKind == JsonValueKind.Object &&
                     browserActionProp.TryGetProperty("default_popup", out var legacyPopupProp))
            {
                info.PopupPage = legacyPopupProp.GetString();
            }

            // Icon: largest from "icons", fallback: action.default_icon
            info.IconPath = ResolveIconPath(folderPath, root);

            return info;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[Extensions] Invalid manifest in '{folderPath}'", ex);
            return null;
        }
    }

    private static string? GetValidIconPath(string folderPath, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
            return null;

        var clean = relative.Trim().TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar);
        if (clean.StartsWith("." + Path.DirectorySeparatorChar))
        {
            clean = clean.Substring(2);
        }

        var fullPath = Path.Combine(folderPath, clean);
        return File.Exists(fullPath) ? fullPath : null;
    }

    private static string? ResolveIconPath(string folderPath, JsonElement root)
    {
        var candidates = new List<(int Size, string Relative)>();

        if (root.TryGetProperty("icons", out var icons) && icons.ValueKind == JsonValueKind.Object)
        {
            foreach (var icon in icons.EnumerateObject())
            {
                if (icon.Value.ValueKind == JsonValueKind.String)
                {
                    int size = int.TryParse(icon.Name, out int s) ? s : 0;
                    var path = icon.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        candidates.Add((size, path));
                    }
                }
            }
        }

        // Fallback: action (MV3) / browser_action (MV2) default_icon
        foreach (var actionKey in new[] { "action", "browser_action" })
        {
            if (root.TryGetProperty(actionKey, out var action) && action.ValueKind == JsonValueKind.Object &&
                action.TryGetProperty("default_icon", out var defaultIcon))
            {
                if (defaultIcon.ValueKind == JsonValueKind.String)
                {
                    var path = defaultIcon.GetString();
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        candidates.Add((0, path));
                    }
                }
                else if (defaultIcon.ValueKind == JsonValueKind.Object)
                {
                    foreach (var icon in defaultIcon.EnumerateObject())
                    {
                        if (icon.Value.ValueKind == JsonValueKind.String)
                        {
                            int size = int.TryParse(icon.Name, out int s) ? s : 0;
                            var path = icon.Value.GetString();
                            if (!string.IsNullOrWhiteSpace(path))
                            {
                                candidates.Add((size, path));
                            }
                        }
                    }
                }
            }
        }

        foreach (var candidate in candidates.OrderByDescending(c => c.Size))
        {
            var valid = GetValidIconPath(folderPath, candidate.Relative);
            if (valid != null)
                return valid;
        }

        var commonNames = new[] { "icon.png", "icon128.png", "icon-128.png", "icon48.png", "icon-48.png", "icon16.png", "icon-16.png", "icon.ico" };
        foreach (var name in commonNames)
        {
            var check = Path.Combine(folderPath, name);
            if (File.Exists(check))
                return check;
            var subCheck = Path.Combine(folderPath, "icons", name);
            if (File.Exists(subCheck))
                return subCheck;
            var imgCheck = Path.Combine(folderPath, "images", name);
            if (File.Exists(imgCheck))
                return imgCheck;
        }

        return null;
    }

    /// <summary>
    /// Resolves __MSG_xxx__ placeholders using the first available _locales file.
    /// </summary>
    private static string? ResolveLocalizedString(string folderPath, string? value, JsonElement? root = null)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        if (!value.StartsWith("__MSG_", StringComparison.Ordinal) || !value.EndsWith("__", StringComparison.Ordinal))
            return value;

        try
        {
            var key = value.Substring(6, value.Length - 8);
            var localesDir = Path.Combine(folderPath, "_locales");
            if (!Directory.Exists(localesDir))
                return value;

            var localeDirs = Directory.GetDirectories(localesDir);
            if (localeDirs.Length == 0)
                return value;

            string? defaultLocale = null;
            if (root.HasValue && root.Value.TryGetProperty("default_locale", out var dlProp))
            {
                defaultLocale = dlProp.GetString();
            }

            var ordered = localeDirs.OrderByDescending(d =>
            {
                var dirName = Path.GetFileName(d);
                if (!string.IsNullOrWhiteSpace(defaultLocale) &&
                    string.Equals(dirName, defaultLocale, StringComparison.OrdinalIgnoreCase))
                    return 10;
                if (dirName.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                    return 5;
                if (dirName.StartsWith("de", StringComparison.OrdinalIgnoreCase))
                    return 4;
                return 1;
            });

            foreach (var localeDir in ordered)
            {
                var messagesPath = Path.Combine(localeDir, "messages.json");
                if (!File.Exists(messagesPath))
                    continue;

                using var doc = JsonDocument.Parse(File.ReadAllText(messagesPath), _tolerantJsonOptions);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase) &&
                        prop.Value.ValueKind == JsonValueKind.Object &&
                        prop.Value.TryGetProperty("message", out var messageProp))
                    {
                        var msgStr = messageProp.GetString();
                        if (!string.IsNullOrWhiteSpace(msgStr))
                            return msgStr;
                    }
                }
            }
        }
        catch { }

        return value;
    }

    internal static Dictionary<string, ExtensionSavedState> LoadSavedStates()
    {
        try
        {
            if (File.Exists(StateFilePath))
            {
                var raw = File.ReadAllText(StateFilePath);
                var json = SecureAppDataStorage.DecryptString(raw);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    using var doc = JsonDocument.Parse(json, _tolerantJsonOptions);
                    var dict = new Dictionary<string, ExtensionSavedState>(StringComparer.OrdinalIgnoreCase);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.True || prop.Value.ValueKind == JsonValueKind.False)
                        {
                            dict[prop.Name] = new ExtensionSavedState { IsEnabled = prop.Value.GetBoolean() };
                        }
                        else if (prop.Value.ValueKind == JsonValueKind.Object)
                        {
                            var state = new ExtensionSavedState();
                            if (prop.Value.TryGetProperty("IsEnabled", out var isEn))
                                state.IsEnabled = isEn.GetBoolean();
                            else if (prop.Value.TryGetProperty("isEnabled", out var isEnLower))
                                state.IsEnabled = isEnLower.GetBoolean();

                            if (prop.Value.TryGetProperty("ExtensionId", out var extId))
                                state.ExtensionId = extId.GetString();
                            else if (prop.Value.TryGetProperty("extensionId", out var extIdLower))
                                state.ExtensionId = extIdLower.GetString();
                            if (prop.Value.TryGetProperty(nameof(ExtensionSavedState.ArchiveName), out var archiveName))
                                state.ArchiveName = archiveName.GetString();
                            if (prop.Value.TryGetProperty(nameof(ExtensionSavedState.ArchiveHash), out var archiveHash))
                                state.ArchiveHash = archiveHash.GetString();
                            if (prop.Value.TryGetProperty(nameof(ExtensionSavedState.RemovedArchives), out var removed))
                                state.RemovedArchives = removed.Deserialize<Dictionary<string, string>>();
                            if (prop.Value.TryGetProperty(nameof(ExtensionSavedState.RemovalPending), out var pending))
                                state.RemovalPending = pending.GetBoolean();

                            dict[prop.Name] = state;
                        }
                    }
                    return dict;
                }
            }
        }
        catch { }

        return new Dictionary<string, ExtensionSavedState>(StringComparer.OrdinalIgnoreCase);
    }

    internal static bool SaveSavedStates(Dictionary<string, ExtensionSavedState> states)
    {
        string temporary = StateFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(states, new JsonSerializerOptions { WriteIndented = true });
            var encryptedData = SecureAppDataStorage.EncryptString(json);
            using (var stream = File.Open(temporary, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(stream))
                writer.Write(encryptedData);
            File.Move(temporary, StateFilePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("[Extensions] Could not save extension activation state", ex);
            return false;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLogger.Warn("[Extensions] Could not clean up temporary state", ex);
            }
        }
    }

    private static Dictionary<string, bool> LoadEnabledStates()
    {
        var saved = LoadSavedStates();
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in saved)
        {
            result[kvp.Key] = kvp.Value.IsEnabled;
        }
        return result;
    }

    private static void SaveEnabledStates(Dictionary<string, bool> states)
    {
        var current = LoadSavedStates();
        foreach (var kvp in states)
        {
            if (!current.TryGetValue(kvp.Key, out var st))
            {
                st = new ExtensionSavedState();
                current[kvp.Key] = st;
            }
            st.IsEnabled = kvp.Value;
        }
        SaveSavedStates(current);
    }
}
