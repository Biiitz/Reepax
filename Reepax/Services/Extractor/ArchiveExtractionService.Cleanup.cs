using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Reepax.Models;

namespace Reepax.Services.Extractor;

public partial class ArchiveExtractionService
{
    private sealed record ArchiveCleanupPlan(HashSet<string> Sidecars, HashSet<string> Par2Files, HashSet<string> ProtectedPaths);

    private static ArchiveCleanupPlan CaptureArchiveCleanupPlan(DownloadPackage package)
    {
        var sidecars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var par2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var finalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string directory = package.SaveDirectory;
        bool dedicated = false;
        SafeInvoke(() =>
        {
            var items = package.ItemsSnapshot;
            foreach (var item in items)
            {
                if (!TryGetCleanupTarget(item, out string path)) continue;
                finalPaths.Add(path);
                if (!item.IsEnabled || item.Status != DownloadStatus.Completed)
                {
                    protectedPaths.Add(path);
                    protectedPaths.Add(path + ".part");
                    protectedPaths.Add(path + ".part.segments");
                    continue;
                }
                targets[path] = item.DirectDownloadUrl;
                if (Verification.Par2RepairService.Instance.IsPar2File(path)) par2.Add(path);
            }
            var packages = Download.QueueManager.Instance.Packages.ToArray();
            dedicated = ViewModels.MainViewModel.IsDedicatedPackageDirectory(package, directory, packages);
            foreach (var other in packages)
            {
                if (ReferenceEquals(other, package)) continue;
                foreach (var item in other.ItemsSnapshot)
                {
                    if (!TryGetCleanupTarget(item, out string path)) continue;
                    protectedPaths.Add(path);
                    protectedPaths.Add(path + ".part");
                    protectedPaths.Add(path + ".part.segments");
                }
            }
        });
        foreach (var target in targets)
        {
            if (!Download.DownloadEngine.HasMatchingSourceCheckpoint(target.Key, target.Value)) continue;
            sidecars.Add(target.Key + ".part");
            sidecars.Add(target.Key + ".part.segments");
        }
        // A final download target can resemble another item's reserved sidecar path.
        sidecars.ExceptWith(finalPaths);
        // Capture inputs before publication; files created by extraction are never cleanup inputs.
        sidecars.RemoveWhere(path => !File.Exists(path));
        if (dedicated && !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            par2.UnionWith(Directory.GetFiles(directory, "*.par2", SearchOption.TopDirectoryOnly));
        return new(sidecars, par2, protectedPaths);
    }

    private static bool TryGetCleanupTarget(DownloadItem item, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(item.SaveFilePath)) return false;
        try { path = Path.GetFullPath(item.SaveFilePath); return true; }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (IOException) { return false; }
    }
}
