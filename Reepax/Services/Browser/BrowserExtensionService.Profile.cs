using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Reepax.Services.Storage;

namespace Reepax.Services.Browser;

public partial class BrowserExtensionService
{
    private readonly SemaphoreSlim _profileGate = new(1, 1);
    private readonly Dictionary<string, Dictionary<string, string>> _knownProfilePaths = new(StringComparer.OrdinalIgnoreCase);

    public async Task InstallIntoProfileAsync(CoreWebView2Profile profile)
    {
        await _profileGate.WaitAsync();
        try
        {
            var states = LoadSavedStates();
            var existing = await GetProfileExtensionsAsync(profile);
            var knownPaths = GetKnownPaths(profile.ProfilePath);
            var profilePaths = ReadProfilePaths(profile.ProfilePath, knownPaths);
            if (await FinishPendingRemovalsAsync(states, existing, profilePaths, knownPaths))
                existing = await GetProfileExtensionsAsync(profile);
            Scan(states);
            var matchedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool stateChanged = false;
            foreach (var extension in Extensions)
            {
                string path = NormalizePath(extension.FolderPath);
                var installed = FindInstalledExtension(existing, profilePaths, matchedIds, path);
                if (installed == null && extension.IsEnabled)
                {
                    try
                    {
                        installed = await profile.AddBrowserExtensionAsync(path);
                        if (matchedIds.Contains(installed.Id))
                            throw new InvalidOperationException("The profile extension ID is already assigned.");
                    }
                    catch (Exception ex)
                    {
                        existing = await GetProfileExtensionsAsync(profile);
                        profilePaths = ReadProfilePaths(profile.ProfilePath, knownPaths);
                        installed = FindInstalledExtension(existing, profilePaths, matchedIds, path);
                        if (installed == null)
                        {
                            extension.InstallError = ex.Message;
                            if (states.TryGetValue(extension.FolderName, out var failedState) && failedState.ExtensionId != null)
                            {
                                failedState.ExtensionId = null;
                                stateChanged = true;
                            }
                            AppLogger.Warn("[Extensions] Could not install browser extension", ex);
                        }
                    }
                }
                if (installed == null) continue;

                matchedIds.Add(installed.Id);
                knownPaths[installed.Id] = path;
                extension.InstalledExtensionId = installed.Id;
                extension.NativeExtension = installed;
                if (!string.IsNullOrWhiteSpace(installed.Name) && !installed.Name.StartsWith("__MSG_", StringComparison.Ordinal))
                    extension.Name = installed.Name;
                try
                {
                    if (installed.IsEnabled != extension.IsEnabled)
                        await installed.EnableAsync(extension.IsEnabled);
                }
                catch (Exception ex)
                {
                    extension.InstallError = ex.Message;
                    AppLogger.Warn("[Extensions] Could not synchronize extension activation", ex);
                }

                if (!states.TryGetValue(extension.FolderName, out var state))
                    states[extension.FolderName] = state = new ExtensionSavedState();
                stateChanged |= state.ExtensionId != installed.Id || state.IsEnabled != extension.IsEnabled;
                state.ExtensionId = installed.Id;
                state.IsEnabled = extension.IsEnabled;
            }

            // Only clean up entries whose source is confirmed to be a missing managed folder.
            if (existing != null)
            {
                foreach (var installed in existing)
                {
                    if (matchedIds.Contains(installed.Id) || !profilePaths.TryGetValue(installed.Id, out var paths) || paths.Count != 1)
                        continue;
                    string path = paths.Single();
                    if (!IsManagedPath(path) || Directory.Exists(path)) continue;
                    try
                    {
                        await installed.RemoveAsync();
                        knownPaths.Remove(installed.Id);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("[Extensions] Could not remove an orphaned browser extension", ex);
                    }
                }
            }
            if (stateChanged) SaveSavedStates(states);
        }
        finally
        {
            _profileGate.Release();
        }
    }

    public async Task SetEnabledAsync(BrowserExtensionInfo extension, bool enabled)
    {
            await _profileGate.WaitAsync();
        try
        {
            var current = FindCurrentExtension(extension);
            if (current == null) return;
            var states = LoadSavedStates();
            if (!states.TryGetValue(current.FolderName, out var state))
                states[current.FolderName] = state = new ExtensionSavedState();
            if (current.NativeExtension != null)
                await current.NativeExtension.EnableAsync(enabled);
            state.IsEnabled = enabled;
            state.ExtensionId = current.InstalledExtensionId;
            if (!SaveSavedStates(states))
            {
                if (current.NativeExtension != null)
                    await current.NativeExtension.EnableAsync(current.IsEnabled);
                throw new IOException("Could not save extension activation state.");
            }
            current.IsEnabled = enabled;
        }
        finally
        {
            _profileGate.Release();
        }
    }

    public async Task RemoveAsync(BrowserExtensionInfo extension)
    {
        await _profileGate.WaitAsync();
        try
        {
            var current = FindCurrentExtension(extension);
            if (current == null) return;
            var states = LoadSavedStates();
            states.TryGetValue(current.FolderName, out var state);
            var removed = new ExtensionSavedState
            {
                IsEnabled = false,
                ArchiveName = current.ArchiveName,
                ArchiveHash = current.ArchiveHash,
                RemovedArchives = GetRemovedArchives(current, state),
                RemovalPending = true
            };
            states[current.FolderName] = removed;
            if (!SaveSavedStates(states)) throw new IOException("Could not save extension removal.");

            if (current.NativeExtension != null)
            {
                await current.NativeExtension.RemoveAsync();
                current.NativeExtension = null;
                current.InstalledExtensionId = null;
            }
            CompleteRemoval(states, current.FolderName, removed);
            Extensions.Remove(current);
        }
        finally
        {
            _profileGate.Release();
        }
    }

    private async Task<bool> FinishPendingRemovalsAsync(Dictionary<string, ExtensionSavedState> states,
        IReadOnlyList<CoreWebView2BrowserExtension>? existing, Dictionary<string, HashSet<string>> paths,
        Dictionary<string, string> knownPaths)
    {
        if (existing == null) return false;
        bool changed = false;
        foreach (var (folder, state) in states.Where(pair => pair.Value.RemovalPending).ToArray())
        {
            try
            {
                string top = GetManagedFolder(folder);
                string prefix = top + Path.DirectorySeparatorChar;
                foreach (var installed in existing)
                {
                    if (!paths.TryGetValue(installed.Id, out var folders) ||
                        !folders.Any(path => path.Equals(top, StringComparison.OrdinalIgnoreCase) ||
                                             path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    if (folders.Count != 1) throw new IOException("The profile extension path is ambiguous.");
                    await installed.RemoveAsync();
                    knownPaths.Remove(installed.Id);
                    changed = true;
                }
                CompleteRemoval(states, folder, state);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("[Extensions] Could not finish pending removal", ex);
            }
        }
        return changed;
    }

    private static void CompleteRemoval(Dictionary<string, ExtensionSavedState> states, string folder, ExtensionSavedState pending)
    {
        string top = GetManagedFolder(folder);
        if (Directory.Exists(top))
        {
            if ((File.GetAttributes(top) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Cannot remove a linked extension folder.");
            Directory.Delete(top, recursive: true);
        }
        if (pending.RemovedArchives is { Count: > 0 })
            states[folder] = new ExtensionSavedState { RemovedArchives = pending.RemovedArchives };
        else
            states.Remove(folder);
        if (SaveSavedStates(states)) return;
        states[folder] = pending;
        throw new IOException("Could not finalize extension removal.");
    }

    private static string GetManagedFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder != Path.GetFileName(folder) || folder is "." or "..")
            throw new IOException("Invalid extension folder.");
        return Path.Combine(ExtensionsDirectory, folder);
    }

    private BrowserExtensionInfo? FindCurrentExtension(BrowserExtensionInfo selected)
    {
        if (Extensions.Contains(selected)) return selected;
        if (string.IsNullOrWhiteSpace(selected.InstalledExtensionId)) return null;
        return Extensions.FirstOrDefault(extension =>
            string.Equals(extension.FolderPath, selected.FolderPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(extension.InstalledExtensionId, selected.InstalledExtensionId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(extension.ArchiveHash, selected.ArchiveHash, StringComparison.Ordinal));
    }

    private Dictionary<string, string> GetKnownPaths(string profilePath)
    {
        if (!_knownProfilePaths.TryGetValue(profilePath, out var paths))
            _knownProfilePaths[profilePath] = paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return paths;
    }

    private static async Task<IReadOnlyList<CoreWebView2BrowserExtension>?> GetProfileExtensionsAsync(CoreWebView2Profile profile)
    {
        try { return await profile.GetBrowserExtensionsAsync(); }
        catch (Exception ex)
        {
            AppLogger.Warn("[Extensions] Could not list profile extensions", ex);
            return null;
        }
    }

    private static CoreWebView2BrowserExtension? FindInstalledExtension(IReadOnlyList<CoreWebView2BrowserExtension>? existing,
        Dictionary<string, HashSet<string>> paths, HashSet<string> matchedIds, string folder)
    {
        if (existing == null) return null;
        CoreWebView2BrowserExtension? match = null;
        foreach (var installed in existing)
        {
            if (matchedIds.Contains(installed.Id) || !paths.TryGetValue(installed.Id, out var folders) ||
                folders.Count != 1 || !folders.Contains(folder))
                continue;
            if (match != null) return null;
            match = installed;
        }
        return match;
    }

    private static Dictionary<string, HashSet<string>> ReadProfilePaths(string profilePath, Dictionary<string, string> knownPaths)
    {
        var paths = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in new[] { "Secure Preferences", "Preferences" })
        {
            try
            {
                using var stream = File.Open(Path.Combine(profilePath, file), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var doc = JsonDocument.Parse(stream, _tolerantJsonOptions);
                if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                    !doc.RootElement.TryGetProperty("extensions", out var extensions) || extensions.ValueKind != JsonValueKind.Object ||
                    !extensions.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object)
                    continue;
                foreach (var property in settings.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.Object ||
                        !property.Value.TryGetProperty("path", out var pathProperty) || pathProperty.ValueKind != JsonValueKind.String ||
                        pathProperty.GetString() is not { } path || !Path.IsPathFullyQualified(path))
                        continue;
                    if (!paths.TryGetValue(property.Name, out var folders))
                        paths[property.Name] = folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    folders.Add(NormalizePath(path));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        }
        foreach (var (id, path) in knownPaths)
        {
            if (!paths.TryGetValue(id, out var folders))
                paths.Add(id, folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            folders.Add(path);
        }
        return paths;
    }

    private static bool IsManagedPath(string path)
    {
        string root = NormalizePath(ExtensionsDirectory) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
