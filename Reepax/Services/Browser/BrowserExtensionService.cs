using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
}

/// <summary>
/// Manages WebExtensions (MV2/MV3) from %LocalAppData%/Reepax/Extensions.
/// Scans the directory, parses manifests (name, icon, options page), extracts archive drops (.zip, .crx),
/// discovers manifests in subfolders, installs enabled extensions into the shared WebView2 profile,
/// and persists activation states.
/// </summary>
public class BrowserExtensionService
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

    private BrowserExtensionService()
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
    /// Extracts any dropped .zip or .crx archives in the extensions directory into folders.
    /// </summary>
    private static void ExtractArchives(string extensionsDir)
    {
        try
        {
            if (!Directory.Exists(extensionsDir))
                return;

            var zipFiles = Directory.GetFiles(extensionsDir, "*.zip");
            var crxFiles = Directory.GetFiles(extensionsDir, "*.crx");
            var allArchives = zipFiles.Concat(crxFiles).ToArray();

            foreach (var archivePath in allArchives)
            {
                try
                {
                    var baseName = Path.GetFileNameWithoutExtension(archivePath);
                    if (string.IsNullOrWhiteSpace(baseName))
                        continue;

                    var targetDir = Path.Combine(extensionsDir, baseName);
                    // If target directory already exists and contains files, do not re-extract
                    if (Directory.Exists(targetDir) && Directory.EnumerateFileSystemEntries(targetDir).Any())
                        continue;

                    Directory.CreateDirectory(targetDir);

                    if (archivePath.EndsWith(".crx", StringComparison.OrdinalIgnoreCase))
                    {
                        ExtractCrx(archivePath, targetDir);
                    }
                    else
                    {
                        ZipFile.ExtractToDirectory(archivePath, targetDir, overwriteFiles: true);
                    }

                    AppLogger.Info($"[Extensions] Extracted archive '{Path.GetFileName(archivePath)}' to '{targetDir}'");
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"[Extensions] Failed to extract archive '{archivePath}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[Extensions] Error checking for extension archives: {ex.Message}");
        }
    }

    /// <summary>
    /// Unpacks a Chrome CRX archive (CRX2/CRX3) by locating the inner zip archive stream.
    /// </summary>
    private static void ExtractCrx(string crxPath, string targetDir)
    {
        using var stream = File.OpenRead(crxPath);
        
        long zipOffset = -1;
        var buffer = new byte[8192];
        long totalRead = 0;
        int bytesRead;

        byte[] magic = { 0x50, 0x4B, 0x03, 0x04 };
        int magicIndex = 0;

        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0 && totalRead < 1024 * 1024)
        {
            for (int i = 0; i < bytesRead; i++)
            {
                if (buffer[i] == magic[magicIndex])
                {
                    magicIndex++;
                    if (magicIndex == magic.Length)
                    {
                        zipOffset = totalRead + i - magic.Length + 1;
                        break;
                    }
                }
                else
                {
                    magicIndex = (buffer[i] == magic[0]) ? 1 : 0;
                }
            }

            if (zipOffset != -1)
                break;

            totalRead += bytesRead;
        }

        if (zipOffset == -1)
        {
            // Fallback: try standard zip
            stream.Position = 0;
            using var regularZip = new ZipArchive(stream, ZipArchiveMode.Read);
            regularZip.ExtractToDirectory(targetDir, overwriteFiles: true);
            return;
        }

        stream.Position = zipOffset;
        using var zipArchive = new ZipArchive(stream, ZipArchiveMode.Read);
        zipArchive.ExtractToDirectory(targetDir, overwriteFiles: true);
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
            var manifestFiles = Directory.GetFiles(topDir, "manifest.json", SearchOption.AllDirectories)
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
    public void Scan()
    {
        ExtractArchives(ExtensionsDirectory);

        var found = new List<BrowserExtensionInfo>();
        var savedStates = LoadSavedStates();
        var profileMap = TryReadProfileExtensionPaths();

        try
        {
            if (!Directory.Exists(ExtensionsDirectory))
                return;

            foreach (var topDir in Directory.GetDirectories(ExtensionsDirectory))
            {
                try
                {
                    var extensionFolder = FindExtensionFolder(topDir);
                    if (extensionFolder == null)
                        continue;

                    var manifestPath = Path.Combine(extensionFolder, "manifest.json");
                    if (!File.Exists(manifestPath))
                        continue;

                    var info = ParseManifest(extensionFolder, manifestPath);
                    if (info == null)
                        continue;

                    var topDirName = Path.GetFileName(topDir);
                    info.FolderName = topDirName;

                    if (savedStates.TryGetValue(topDirName, out var state))
                    {
                        info.IsEnabled = state.IsEnabled;
                        if (!string.IsNullOrWhiteSpace(state.ExtensionId))
                        {
                            info.InstalledExtensionId = state.ExtensionId;
                        }
                    }
                    else if (savedStates.TryGetValue(info.Name, out var stateByName))
                    {
                        info.IsEnabled = stateByName.IsEnabled;
                        if (!string.IsNullOrWhiteSpace(stateByName.ExtensionId))
                        {
                            info.InstalledExtensionId = stateByName.ExtensionId;
                        }
                    }

                    // Fallback to profile preferences mapping if extension ID not yet known
                    if (string.IsNullOrWhiteSpace(info.InstalledExtensionId))
                    {
                        var normPath = Path.GetFullPath(info.FolderPath).TrimEnd('\\', '/');
                        if (profileMap.TryGetValue(normPath, out var profId))
                        {
                            info.InstalledExtensionId = profId;
                        }
                    }

                    if (!found.Any(e => string.Equals(e.FolderPath, info.FolderPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        found.Add(info);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"[Extensions] Error reading extension in '{topDir}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("[Extensions] Failed to scan extensions folder", ex);
        }

        Extensions = found;
    }

    /// <summary>
    /// Installs all enabled extensions into the specified WebView2 profile.
    /// Extension settings automatically persist in the profile's user data directory.
    /// </summary>
    public async Task InstallIntoProfileAsync(CoreWebView2Profile profile)
    {
        Scan();

        IReadOnlyList<CoreWebView2BrowserExtension>? existing = null;
        try
        {
            existing = await profile.GetBrowserExtensionsAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[Extensions] GetBrowserExtensionsAsync failed: {ex.Message}");
        }

        var folderToIdMap = TryReadProfileExtensionPaths();
        var states = LoadSavedStates();
        bool stateChanged = false;
        var matchedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ext in Extensions)
        {
            // 1. Resolve ID from profile mapping if missing
            if (string.IsNullOrWhiteSpace(ext.InstalledExtensionId) &&
                folderToIdMap.TryGetValue(Path.GetFullPath(ext.FolderPath).TrimEnd('\\', '/'), out var mappedId))
            {
                ext.InstalledExtensionId = mappedId;
            }

            CoreWebView2BrowserExtension? alreadyInstalled = null;

            if (existing != null)
            {
                // Match by known ID
                if (!string.IsNullOrWhiteSpace(ext.InstalledExtensionId))
                {
                    alreadyInstalled = existing.FirstOrDefault(e =>
                        string.Equals(e.Id, ext.InstalledExtensionId, StringComparison.OrdinalIgnoreCase));
                }

                // Match by Name, ShortName, FolderName
                if (alreadyInstalled == null)
                {
                    alreadyInstalled = existing.FirstOrDefault(e =>
                        !matchedIds.Contains(e.Id) &&
                        !string.IsNullOrWhiteSpace(e.Name) && (
                            string.Equals(e.Name, ext.Name, StringComparison.OrdinalIgnoreCase) ||
                            (!string.IsNullOrWhiteSpace(ext.ShortName) && string.Equals(e.Name, ext.ShortName, StringComparison.OrdinalIgnoreCase)) ||
                            string.Equals(e.Name, ext.FolderName, StringComparison.OrdinalIgnoreCase) ||
                            ext.Name.Contains(e.Name, StringComparison.OrdinalIgnoreCase) ||
                            e.Name.Contains(ext.Name, StringComparison.OrdinalIgnoreCase)));
                }

                // If folderToIdMap had an ID, try matching that
                if (alreadyInstalled == null && !string.IsNullOrWhiteSpace(ext.InstalledExtensionId))
                {
                    alreadyInstalled = existing.FirstOrDefault(e =>
                        string.Equals(e.Id, ext.InstalledExtensionId, StringComparison.OrdinalIgnoreCase));
                }
            }

            if (!ext.IsEnabled)
            {
                if (alreadyInstalled != null)
                {
                    matchedIds.Add(alreadyInstalled.Id);
                    ext.InstalledExtensionId = alreadyInstalled.Id;
                    ext.NativeExtension = alreadyInstalled;
                    try { await alreadyInstalled.EnableAsync(false); } catch { }

                    if (!states.TryGetValue(ext.FolderName, out var st))
                    {
                        st = new ExtensionSavedState();
                        states[ext.FolderName] = st;
                    }
                    st.IsEnabled = false;
                    st.ExtensionId = alreadyInstalled.Id;
                    stateChanged = true;
                }
                continue;
            }

            if (alreadyInstalled != null)
            {
                matchedIds.Add(alreadyInstalled.Id);
                ext.InstalledExtensionId = alreadyInstalled.Id;
                ext.NativeExtension = alreadyInstalled;
                ext.InstallError = null;

                if (!string.IsNullOrWhiteSpace(alreadyInstalled.Name) &&
                    !alreadyInstalled.Name.StartsWith("__MSG_", StringComparison.Ordinal))
                {
                    ext.Name = alreadyInstalled.Name;
                }

                if (!alreadyInstalled.IsEnabled)
                {
                    try { await alreadyInstalled.EnableAsync(true); } catch { }
                }

                if (!states.TryGetValue(ext.FolderName, out var st))
                {
                    st = new ExtensionSavedState();
                    states[ext.FolderName] = st;
                }
                st.IsEnabled = true;
                st.ExtensionId = alreadyInstalled.Id;
                stateChanged = true;

                AppLogger.Info($"[Extensions] '{ext.Name}' already installed (ID: {alreadyInstalled.Id})");
                continue;
            }

            // Not yet registered in profile -> install it
            try
            {
                var installed = await profile.AddBrowserExtensionAsync(ext.FolderPath);
                ext.InstalledExtensionId = installed.Id;
                ext.NativeExtension = installed;
                ext.InstallError = null;
                matchedIds.Add(installed.Id);

                if (!string.IsNullOrWhiteSpace(installed.Name) &&
                    !installed.Name.StartsWith("__MSG_", StringComparison.Ordinal))
                {
                    ext.Name = installed.Name;
                }

                if (!states.TryGetValue(ext.FolderName, out var st))
                {
                    st = new ExtensionSavedState();
                    states[ext.FolderName] = st;
                }
                st.IsEnabled = true;
                st.ExtensionId = installed.Id;
                stateChanged = true;

                AppLogger.Info($"[Extensions] '{ext.Name}' installed (ID: {installed.Id})");
            }
            catch (Exception ex)
            {
                // If AddBrowserExtensionAsync threw because the extension already exists, recover it
                CoreWebView2BrowserExtension? fallbackMatch = null;
                try
                {
                    var reExisting = await profile.GetBrowserExtensionsAsync();
                    var reMap = TryReadProfileExtensionPaths();
                    if (reMap.TryGetValue(Path.GetFullPath(ext.FolderPath).TrimEnd('\\', '/'), out var recoveredId))
                    {
                        fallbackMatch = reExisting?.FirstOrDefault(e => string.Equals(e.Id, recoveredId, StringComparison.OrdinalIgnoreCase));
                    }

                    if (fallbackMatch == null && reExisting != null)
                    {
                        fallbackMatch = reExisting.FirstOrDefault(e => !matchedIds.Contains(e.Id));
                    }
                }
                catch { }

                if (fallbackMatch != null)
                {
                    matchedIds.Add(fallbackMatch.Id);
                    ext.InstalledExtensionId = fallbackMatch.Id;
                    ext.NativeExtension = fallbackMatch;
                    ext.InstallError = null;

                    if (!string.IsNullOrWhiteSpace(fallbackMatch.Name) &&
                        !fallbackMatch.Name.StartsWith("__MSG_", StringComparison.Ordinal))
                    {
                        ext.Name = fallbackMatch.Name;
                    }

                    if (!fallbackMatch.IsEnabled && ext.IsEnabled)
                    {
                        try { await fallbackMatch.EnableAsync(true); } catch { }
                    }

                    if (!states.TryGetValue(ext.FolderName, out var st))
                    {
                        st = new ExtensionSavedState();
                        states[ext.FolderName] = st;
                    }
                    st.IsEnabled = ext.IsEnabled;
                    st.ExtensionId = fallbackMatch.Id;
                    stateChanged = true;

                    AppLogger.Info($"[Extensions] Recovered existing installation for '{ext.Name}' (ID: {fallbackMatch.Id})");
                }
                else
                {
                    ext.InstallError = ex.Message;
                    AppLogger.Warn($"[Extensions] '{ext.Name}' could not be installed: {ex.Message}");
                }
            }
        }

        // Remove any orphaned extensions from profile that no longer exist on disk
        if (existing != null)
        {
            foreach (var profileExt in existing)
            {
                if (!matchedIds.Contains(profileExt.Id))
                {
                    try
                    {
                        AppLogger.Info($"[Extensions] Removing orphaned profile extension '{profileExt.Name}' ({profileExt.Id})");
                        await profileExt.RemoveAsync();
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn($"[Extensions] Failed to remove orphaned extension: {ex.Message}");
                    }
                }
            }
        }

        if (stateChanged)
        {
            SaveSavedStates(states);
        }
    }

    /// <summary>
    /// Enables/disables an extension — immediately at runtime (if installed)
    /// and persistently via folder name.
    /// </summary>
    public void SetEnabled(BrowserExtensionInfo ext, bool enabled)
    {
        ext.IsEnabled = enabled;

        try
        {
            _ = ext.NativeExtension?.EnableAsync(enabled);
        }
        catch { }

        var states = LoadSavedStates();
        if (!states.TryGetValue(ext.FolderName, out var state))
        {
            state = new ExtensionSavedState();
            states[ext.FolderName] = state;
        }
        state.IsEnabled = enabled;
        if (!string.IsNullOrWhiteSpace(ext.InstalledExtensionId))
        {
            state.ExtensionId = ext.InstalledExtensionId;
        }
        SaveSavedStates(states);
    }

    /// <summary>
    /// Completely removes an extension (from profile and deletes directory).
    /// </summary>
    public void Remove(BrowserExtensionInfo ext)
    {
        try
        {
            _ = ext.NativeExtension?.RemoveAsync();
        }
        catch { }

        try
        {
            if (Directory.Exists(ext.FolderPath))
            {
                Directory.Delete(ext.FolderPath, recursive: true);
            }

            var topFolder = Path.Combine(ExtensionsDirectory, ext.FolderName);
            if (Directory.Exists(topFolder) && !string.Equals(topFolder, ext.FolderPath, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(topFolder, recursive: true);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[Extensions] Fehler beim Löschen von '{ext.Name}'", ex);
        }

        var states = LoadSavedStates();
        states.Remove(ext.FolderName);
        SaveSavedStates(states);

        Extensions.Remove(ext);
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
            AppLogger.Warn($"[Extensions] Invalid manifest in '{folderPath}': {ex.Message}");
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

    /// <summary>
    /// Reads the WebView2 profile's Preferences / Secure Preferences files to discover
    /// mappings from extension folder path to extension ID.
    /// </summary>
    internal static Dictionary<string, string> TryReadProfileExtensionPaths()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var baseDir = SettingsService.WebView2Directory;
            if (!Directory.Exists(baseDir))
                return result;

            var defaultDirs = Directory.GetDirectories(baseDir, "Default", SearchOption.AllDirectories);
            foreach (var defDir in defaultDirs)
            {
                foreach (var prefFile in new[] { "Secure Preferences", "Preferences" })
                {
                    var fullPath = Path.Combine(defDir, prefFile);
                    if (!File.Exists(fullPath))
                        continue;

                    try
                    {
                        using var stream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var doc = JsonDocument.Parse(stream, _tolerantJsonOptions);
                        if (doc.RootElement.TryGetProperty("extensions", out var extObj) &&
                            extObj.TryGetProperty("settings", out var settingsObj) &&
                            settingsObj.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in settingsObj.EnumerateObject())
                            {
                                var id = prop.Name;
                                if (prop.Value.TryGetProperty("path", out var pathProp))
                                {
                                    var path = pathProp.GetString();
                                    if (!string.IsNullOrWhiteSpace(path))
                                    {
                                        var norm = Path.GetFullPath(path).TrimEnd('\\', '/');
                                        result[norm] = id;
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }

        return result;
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

    internal static void SaveSavedStates(Dictionary<string, ExtensionSavedState> states)
    {
        try
        {
            var json = JsonSerializer.Serialize(states, new JsonSerializerOptions { WriteIndented = true });
            var encryptedData = SecureAppDataStorage.EncryptString(json);
            File.WriteAllText(StateFilePath, encryptedData);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[Extensions] Fehler beim Speichern des Aktivierungs-Status: {ex.Message}");
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
