using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Localization;
using Reepax.Services.Storage;

namespace Reepax.Services.Extractor;

public partial class ArchiveExtractionService
{
    private static ArchiveCleanupState CreateCleanupState(DownloadPackage package, IEnumerable<string> archives,
        bool recycle, bool deletePar2, ArchiveCleanupPlan plan, IEnumerable<ArchiveCleanupFile> verifiedPar2)
    {
        var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in plan.Sidecars) paths[Path.GetFullPath(path)] = false;
        foreach (string path in archives) paths[Path.GetFullPath(path)] = recycle;
        if (deletePar2)
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
            if (plan.ProtectedPaths.Contains(pair.Key) || !owners.TryGetValue(pair.Key, out var owner)) continue;
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
            files.Add(new(pair.Key, owner.Id, owner.Suffix, relative.Length == 0 ? null : relative, length, write, pair.Value));
        }
        files.AddRange(verifiedPar2.Where(file => !plan.ProtectedPaths.Contains(file.Path) && !owners.ContainsKey(file.Path)));
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
        // Old plans could include any PAR2 file in a matching folder.
        var remaining = state.Files.Where(entry => entry.ItemId.HasValue || entry.Par2Sha256 != null).ToHashSet();
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
                    else if (entry.Par2Sha256 != null && !entry.Recycle && entry.Suffix.Length == 0 &&
                        entry.RelativePath is { } relative && Verification.Par2RepairService.Instance.IsPar2File(relative))
                        path = GetSafeArchiveDestination(package.SaveDirectory, relative);
                    if (path != null) resolved[entry] = path;
                }
                foreach (var output in package.ExtractedFiles) protectedPaths.Add(output.Path);
                foreach (var other in Download.QueueManager.Instance.Packages)
                {
                    foreach (var output in other.ExtractedFiles) protectedPaths.Add(output.Path);
                    foreach (var item in other.ItemsSnapshot)
                    {
                        if (!TryGetCleanupTarget(item, out string path)) continue;
                        if (ReferenceEquals(other, package)) finalPaths.Add(path);
                        if (ReferenceEquals(other, package) && item.IsEnabled && item.IsReadyForPostProcessing) continue;
                        protectedPaths.Add(path); protectedPaths.Add(path + ".part"); protectedPaths.Add(path + ".part.segments");
                    }
                }
            });
            await Task.Run(async () =>
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
                        if (entry.Par2Sha256 != null)
                        {
                            using var source = RelocationFileAccess.OpenSource(path);
                            if (!source.CanDelete) continue;
                            var hash = await SHA256.HashDataAsync(source.Stream, token).ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                            if (!string.Equals(Convert.ToHexString(hash), entry.Par2Sha256, StringComparison.Ordinal) ||
                                !source.TryDelete()) continue;
                        }
                        else if (entry.Recycle) DeleteToRecycleBin(path);
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
