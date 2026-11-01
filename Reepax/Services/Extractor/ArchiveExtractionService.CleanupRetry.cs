using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Localization;
using Reepax.Services.Storage;

namespace Reepax.Services.Extractor;

public partial class ArchiveExtractionService
{
    private static ArchiveCleanupState CreateCleanupState(DownloadPackage package, IEnumerable<string> archives,
        bool recycle, ArchiveCleanupPlan plan)
    {
        var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in plan.Sidecars) paths[Path.GetFullPath(path)] = false;
        foreach (string path in archives) paths[Path.GetFullPath(path)] = recycle;
        if (SettingsService.Instance.Settings.DeletePar2AfterExtraction)
            foreach (string path in plan.Par2Files) paths[Path.GetFullPath(path)] = false;
        var owners = new Dictionary<string, (Guid Id, string Suffix)>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in package.ItemsSnapshot)
            if (TryGetCleanupTarget(item, out string path)) owners[path] = (item.Id, "");
        foreach (var item in package.ItemsSnapshot)
        {
            if (!TryGetCleanupTarget(item, out string path)) continue;
            owners.TryAdd(path + ".part", (item.Id, ".part"));
            owners.TryAdd(path + ".part.segments", (item.Id, ".part.segments"));
        }
        var files = new List<ArchiveCleanupFile>();
        foreach (var pair in paths)
        {
            if (plan.ProtectedPaths.Contains(pair.Key)) continue;
            Guid? itemId = null;
            string suffix = "";
            if (owners.TryGetValue(pair.Key, out var owner)) { itemId = owner.Id; suffix = owner.Suffix; }
            string relative = Path.GetRelativePath(package.SaveDirectory, pair.Key);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                relative = "";
            long length = -1, write = 0;
            try
            {
                var attributes = File.GetAttributes(pair.Key);
                var info = new FileInfo(pair.Key);
                if ((attributes & FileAttributes.Directory) != 0) throw new IOException("A cleanup file was replaced by a directory.");
                length = info.Length;
                write = info.LastWriteTimeUtc.Ticks;
            }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            files.Add(new(pair.Key, itemId, suffix, relative.Length == 0 ? null : relative, length, write, pair.Value));
        }
        return new(recycle, files.ToArray());
    }

    private async Task RunArchiveCleanupAsync(DownloadPackage package, CancellationToken token, ManualResetEventSlim pause)
    {
        var state = package.PendingArchiveCleanup;
        if (state == null) return;
        SafeInvoke(() =>
        {
            EnsureCleanupStep(package, state.RecycleArchives);
            package.SetNextTaskRunning("Cleanup");
            package.StatusMessage = Loc.Get("Status_ArchiveCleanupRunning");
        });
        var remaining = state.Files.ToHashSet();
        try
        {
            using var lease = await PackagePathOperationGate.Instance.EnterAsync(token);
            var resolved = new Dictionary<ArchiveCleanupFile, string>();
            var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var finalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            SafeInvoke(() =>
            {
                if (package.RequiresRecoveredPathConfirmation || package.ItemsSnapshot.Any(item => item.IsUnavailableForProcessing)) return;
                var items = new Dictionary<Guid, DownloadItem>();
                foreach (var item in package.ItemsSnapshot) items.TryAdd(item.Id, item);
                foreach (var entry in state.Files)
                {
                    if (entry.Suffix is not ("" or ".part" or ".part.segments")) continue;
                    string? path = null;
                    if (entry.ItemId is Guid id)
                    {
                        if (items.TryGetValue(id, out var item) && item.IsEnabled && item.IsReadyForPostProcessing &&
                            TryGetCleanupTarget(item, out string target)) path = target + entry.Suffix;
                    }
                    else if (entry.RelativePath != null) path = GetSafeArchiveDestination(package.SaveDirectory, entry.RelativePath);
                    if (path != null) resolved[entry] = path;
                }
                foreach (var other in Download.QueueManager.Instance.Packages)
                foreach (var item in other.ItemsSnapshot)
                {
                    if (!TryGetCleanupTarget(item, out string path)) continue;
                    if (ReferenceEquals(other, package)) finalPaths.Add(path);
                    if (ReferenceEquals(other, package) && item.IsEnabled && item.IsReadyForPostProcessing) continue;
                    protectedPaths.Add(path); protectedPaths.Add(path + ".part"); protectedPaths.Add(path + ".part.segments");
                }
            });
            await Task.Run(() =>
            {
                foreach (var entry in state.Files)
                {
                    token.ThrowIfCancellationRequested();
                    if (!pause.IsSet)
                    {
                        SafeInvoke(() => package.IsExtractionPaused = true);
                        pause.Wait(token);
                        SafeInvoke(() => package.IsExtractionPaused = false);
                    }
                    if (!resolved.TryGetValue(entry, out string? path) || protectedPaths.Contains(path) ||
                        (entry.Suffix.Length > 0 && finalPaths.Contains(path))) continue;
                    try
                    {
                        ArchiveExtractionWorkspace.RejectReparsePoints(path);
                        FileAttributes attributes;
                        try { attributes = File.GetAttributes(path); }
                        catch (FileNotFoundException) { remaining.Remove(entry); continue; }
                        catch (DirectoryNotFoundException) { remaining.Remove(entry); continue; }
                        if ((attributes & FileAttributes.Directory) != 0) continue;
                        var info = new FileInfo(path);
                        // Delayed retries must not delete a replacement file at the same location.
                        if (entry.Length < 0 || info.Length != entry.Length || info.LastWriteTimeUtc.Ticks != entry.LastWriteTicks) continue;
                        if (entry.Recycle) DeleteToRecycleBin(path);
                        else if (!DeleteOrMoveToTemp(path)) continue;
                        remaining.Remove(entry);
                    }
                    catch (Exception)
                    {
                        if (token.IsCancellationRequested) throw;
                    }
                }
            }, token);
        }
        catch (Exception)
        {
            // Keep every unfinished entry available for an explicit retry.
        }
        SafeInvoke(() =>
        {
            if (remaining.Count == 0)
            {
                package.SetNextTaskDone("Cleanup");
                package.PendingArchiveCleanup = null;
                package.StatusMessage = Loc.Get("Status_CompletedAndExtracted");
            }
            else
            {
                package.PendingArchiveCleanup = state with { Files = state.Files.Where(remaining.Contains).ToArray() };
                package.SetNextTaskFailed("Cleanup");
                package.StatusMessage = state.RecycleArchives ? Loc.Get("Status_ArchiveRecycleFailed")
                    : Loc.Format("Status_ArchiveCleanupFailed", remaining.Count);
            }
            DownloadPersistenceService.Instance.RequestSave();
        });
    }

    private static void EnsureCleanupStep(DownloadPackage package, bool recycle)
    {
        if (package.NextTaskSteps.Any(step => step.Key == "Cleanup")) return;
        package.NextTaskSteps.Add(new NextTaskStep { Key = "Cleanup", Name = Loc.Get(recycle ? "NextTask_MoveToRecycleBin" : "NextTask_DeleteArchive") });
    }
}
