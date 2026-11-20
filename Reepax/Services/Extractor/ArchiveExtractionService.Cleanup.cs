using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Verification;

namespace Reepax.Services.Extractor;

public partial class ArchiveExtractionService
{
    private sealed record ArchiveCleanupPlan(HashSet<string> Sidecars, HashSet<string> Par2Files,
        HashSet<string> ProtectedPaths, string? Par2Directory);

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
                foreach (var output in other.ExtractedFiles) protectedPaths.Add(output.Path);
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
        return new(sidecars, par2, protectedPaths, dedicated ? Path.GetFullPath(directory) : null);
    }

    private static async Task<ArchiveCleanupFile[]> FindUntrackedPar2ForCleanupAsync(string directory, ArchiveCleanupPlan plan,
        IEnumerable<string> extractedArchives, CancellationToken token)
    {
        var archives = extractedArchives.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        archives.ExceptWith(plan.ProtectedPaths);
        if (archives.Count == 0) return [];

        string[] candidates;
        try
        {
            ArchiveExtractionWorkspace.RejectReparsePoints(directory);
            candidates = Directory.GetFiles(directory, "*.par2", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var verified = new List<ArchiveCleanupFile>();
        var archiveHashes = new Dictionary<string, (long Length, byte[] Md5)>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in candidates)
        {
            token.ThrowIfCancellationRequested();
            if (plan.Par2Files.Contains(path) || plan.ProtectedPaths.Contains(path)) continue;

            try
            {
                ArchiveExtractionWorkspace.RejectReparsePoints(path);
                using var recovery = File.OpenRead(path);
                string? hash = await Par2RepairService.MatchArchiveRecoveryFileAsync(recovery, directory, archives, archiveHashes, token)
                    .ConfigureAwait(false);
                if (hash == null) continue;
                verified.Add(new(path, null, "", Path.GetFileName(path), recovery.Length, File.GetLastWriteTimeUtc(path).Ticks, false)
                    { Par2Sha256 = hash });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            {
                // An unreadable or invalid set cannot establish ownership for cleanup.
            }
        }
        return verified.ToArray();
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
