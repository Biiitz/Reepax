using System.IO;
using Microsoft.Win32;
using Velopack;
using Velopack.Locators;

namespace Reepax.Services.Update;

internal sealed class ReepaxPackageLocator(IProcessImpl? process = null) : WindowsVelopackLocator(process, UpdateLogger.Instance)
{
    internal static ReepaxPackageLocator Instance { get; } = new();

    public override List<VelopackAsset> GetLocalPackages()
    {
        if (PackagesDir == null || AppId != AppUpdateService.PackageId) return new();
        for (var parent = new DirectoryInfo(PackagesDir); parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) return new();
        return base.GetLocalPackages().Where(asset => asset.PackageId == AppId && AppUpdateService.IsPackageFileName(asset.FileName) &&
            IsRegularFile(Path.Combine(PackagesDir, asset.FileName))).ToList();
    }

    private static bool IsRegularFile(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

internal static class ManagedInstallation
{
    internal const string InstallerRegistryId = "{B729352A-3D28-444F-A1BC-535A0F507F62}_is1";

    internal static void RefreshRegistration(ReepaxPackageLocator location, string version)
    {
        if (location.AppId != AppUpdateService.PackageId || location.RootAppDir == null) return;
        try
        {
            using var entries = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", writable: true);
            if (entries != null) RefreshRegistration(entries, location.RootAppDir, version);
        }
        catch (Exception ex) { Storage.AppLogger.Warn("[AppUpdateService] Could not refresh installer registration", ex); }
    }

    internal static void RefreshRegistration(RegistryKey entries, string installation, string version)
    {
        using var installer = entries.OpenSubKey(InstallerRegistryId, writable: true);
        if (!MatchesInstallation(installer, installation)) return;
        installer!.SetValue("DisplayVersion", version);
        using var package = entries.OpenSubKey(AppUpdateService.PackageId, writable: true);
        if (MatchesInstallation(package, installation)) package!.SetValue("SystemComponent", 1, RegistryValueKind.DWord);
    }

    private static bool MatchesInstallation(RegistryKey? key, string installation) =>
        key?.GetValue("InstallLocation") is string location &&
        string.Equals(Path.TrimEndingDirectorySeparator(location), Path.TrimEndingDirectorySeparator(installation), StringComparison.OrdinalIgnoreCase);

    internal static void RemoveManagedFiles(ReepaxPackageLocator location)
    {
        if (location.AppId != AppUpdateService.PackageId || location.IsPortable ||
            location.RootAppDir == null || location.AppContentDir == null) return;
        try
        {
            RemoveInstalledPayloadFiles(location.AppContentDir);
            foreach (var package in location.GetLocalPackages())
                TryDeleteFile(Path.Combine(location.PackagesDir!, package.FileName));
            using var entries = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", writable: true);
            if (entries == null) return;
            using var duplicate = entries.OpenSubKey(AppUpdateService.PackageId);
            bool ownsEntry = MatchesInstallation(duplicate, location.RootAppDir);
            duplicate?.Close();
            if (ownsEntry) entries.DeleteSubKeyTree(AppUpdateService.PackageId, throwOnMissingSubKey: false);
        }
        catch (Exception ex) { Storage.AppLogger.Warn("[AppUpdateService] Could not remove all managed installation files", ex); }
    }

    internal static void RemoveInstalledPayloadFiles(string directory)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string inventory = Path.Combine(root, "update-files.txt");
        if (!File.Exists(inventory) || (File.GetAttributes(inventory) & FileAttributes.ReparsePoint) != 0 ||
            new FileInfo(inventory).Length > 4 * 1024 * 1024) return;
        foreach (string name in File.ReadLines(inventory))
        {
            if (!AppUpdateService.IsSafePackageEntry(name)) continue;
            string path = Path.GetFullPath(Path.Combine(root, name));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            TryDeleteFile(path);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
            for (var parent = new FileInfo(path).Directory; parent != null; parent = parent.Parent)
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) return;
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
