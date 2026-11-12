using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.ViewModels;

namespace Reepax.Services.Verification;

public partial class Par2RepairService
{
    private static async Task<List<DownloadItem>?> ConfirmRepairTargetsAsync(DownloadPackage package, string directory,
        IReadOnlyCollection<string> targets, CancellationToken ct)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            return await dispatcher.InvokeAsync(() => ConfirmRepairTargets(package, directory, targets), cancellationToken: ct,
                priority: System.Windows.Threading.DispatcherPriority.Normal);
        return ConfirmRepairTargets(package, directory, targets);
    }

    private static List<DownloadItem>? ConfirmRepairTargets(DownloadPackage package, string directory,
        IReadOnlyCollection<string> targets)
    {
        var items = package.ItemsSnapshot;
        if (items.Any(item => item.IsUnavailableForProcessing)) return null;

        var packages = QueueManager.Instance.Packages.ToArray();
        var ownItems = new List<(string Path, DownloadItem Item)>();
        var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            string? path = GetConfirmedPath(item, package.SaveDirectory);
            bool canRepair = item.IsEnabled && !item.IsFileRenameBusy && path != null;
            if (canRepair)
                ownItems.Add((path!, item));
            foreach (string candidate in MainViewModel.GetItemCandidatePaths(item, package.SaveDirectory))
                if (!canRepair || !string.Equals(path, candidate, StringComparison.OrdinalIgnoreCase))
                    protectedPaths.Add(candidate);
        }
        foreach (var other in packages.Where(other => other.Id != package.Id))
        foreach (var item in other.ItemsSnapshot)
            protectedPaths.UnionWith(MainViewModel.GetItemCandidatePaths(item, other.SaveDirectory));

        var ownPaths = ownItems.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetPaths = new HashSet<string>(targets, StringComparer.OrdinalIgnoreCase);
        bool? dedicated = null;
        foreach (string target in targets)
        {
            if (target.EndsWith(".par2", StringComparison.OrdinalIgnoreCase) || protectedPaths.Contains(target) ||
                Directory.Exists(target) || HasReparsePoint(target) ||
                !string.Equals(target, ExpandExistingPath(target), StringComparison.OrdinalIgnoreCase))
                return null;

            if (!ownPaths.Contains(target) &&
                !(dedicated ??= MainViewModel.IsCleanupDirectory(package, directory, packages, protectOtherPaths: true)))
                return null;

            if (!File.Exists(target)) continue;
            string backup = RepairBackupScope.GetBackupPath(target);
            if (protectedPaths.Contains(backup) || ownPaths.Contains(backup) || targetPaths.Contains(backup))
                return null;
        }
        return ownItems.Where(item => targetPaths.Contains(item.Path)).Select(item => item.Item).ToList();
    }

    private static string? GetConfirmedPath(DownloadItem item, string directory)
    {
        string? path = item.SaveFilePath;
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!Path.IsPathFullyQualified(path))
        {
            if (string.IsNullOrWhiteSpace(directory) || Path.IsPathRooted(path)) return null;
            path = Path.Combine(directory, path);
        }
        return Path.GetFullPath(path);
    }

    private static bool HasReparsePoint(string path)
    {
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return false;
    }

    private static string ExpandExistingPath(string path)
    {
        if (!Path.Exists(path))
        {
            string? directory = Path.GetDirectoryName(path);
            return directory == null ? path : Path.Combine(ExpandExistingPath(directory), Path.GetFileName(path));
        }

        var buffer = new StringBuilder(512);
        uint length = GetLongPathNameW(path, buffer, (uint)buffer.Capacity);
        if (length >= buffer.Capacity)
        {
            buffer.EnsureCapacity(checked((int)length + 1));
            length = GetLongPathNameW(path, buffer, (uint)buffer.Capacity);
        }
        return length == 0 || length >= buffer.Capacity ? throw new IOException("Could not resolve the repair path.") : buffer.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint bufferLength);
}
