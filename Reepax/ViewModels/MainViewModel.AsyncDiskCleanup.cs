using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Reepax.Converters;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Extractor;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;
using Reepax.Services.Verification;

namespace Reepax.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty]
    private bool _isRemovalInProgress;
    private TaskCompletionSource<bool>? _removalFinished;

    internal Task WaitForRemovalAsync() => _removalFinished?.Task ?? Task.CompletedTask;

    internal Func<Action, Task> DiskCleanupRunner { get; set; } = action => Task.Run(action);
    internal Func<string?, (bool Confirmed, bool DeleteFiles, bool RecycleFiles)>? CleanupConfirmationOverride { get; set; }

    partial void OnIsRemovalInProgressChanged(bool value)
    {
        OnPropertyChanged(nameof(CanDeleteSelected));
        OnPropertyChanged(nameof(CanClearCompleted));
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        ClearCompletedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    public Task RemovePackage(DownloadPackage? package) => RemovePackage(package, null);

    public Task RemovePackage(DownloadPackage? package, bool? deleteFilesFromDisk)
        => package == null ? Task.CompletedTask : RemoveSelectionAsync(new[] { package }, Array.Empty<DownloadItem>(), deleteFilesFromDisk);

    [RelayCommand]
    public Task RemoveItem(DownloadItem? item) => RemoveItem(item, null);

    public Task RemoveItem(DownloadItem? item, bool? deleteFilesFromDisk)
        => item == null ? Task.CompletedTask : RemoveSelectionAsync(Array.Empty<DownloadPackage>(), new[] { item }, deleteFilesFromDisk);

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    public Task DeleteSelected()
    {
        if (SelectedMainTab != AppMainTab.Downloads || IsRemovalInProgress)
            return Task.CompletedTask;

        var packages = Packages.Where(package => package.IsSelected).Distinct().ToList();
        if (packages.Count == 0 && SelectedPackage != null && SelectedItem == null)
            packages.Add(SelectedPackage);
        var items = Packages.Where(package => !packages.Contains(package))
            .SelectMany(package => package.Items).Where(item => item.IsSelected).Distinct().ToList();
        if (packages.Count == 0 && items.Count == 0 && SelectedItem != null)
            items.Add(SelectedItem);
        return RemoveSelectionAsync(packages, items, null);
    }

    private async Task RemoveSelectionAsync(IReadOnlyCollection<DownloadPackage> selectedPackages, IReadOnlyCollection<DownloadItem> selectedItems, bool? deleteFilesFromDisk)
    {
        if (IsRemovalInProgress || selectedPackages.Count + selectedItems.Count == 0)
            return;
        if (selectedPackages.Any(package => !Packages.Contains(package)) ||
            selectedItems.Any(item => !Packages.Any(package => package.Items.Contains(item))))
            return;

        _removalFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IsRemovalInProgress = true;
        using var cleanupLease = PostDownloadActionService.Instance.BeginCleanup();
        var targets = selectedPackages.SelectMany(package => package.Items).Concat(selectedItems).Distinct().ToArray();
        var affectedPackages = selectedPackages.Concat(Packages.Where(package => selectedItems.Any(package.Items.Contains))).Distinct().ToArray();
        bool reserved = false;
        bool removed = false;
        bool moveToRecycleBin = false;
        bool recoveredPathsConfirmed = false;
        try
        {
            CleanupSnapshot? preview = null;
            bool deleteFiles = deleteFilesFromDisk ?? false;
            if (!deleteFilesFromDisk.HasValue)
            {
                using (await PackagePathOperationGate.Instance.EnterAsync())
                {
                    preview = CaptureCleanupSnapshot(selectedPackages, selectedItems);
                    await DiskCleanupRunner(() => preview.Scan());
                    ApplyHealedPaths(preview, affectedPackages);
                }
                if (!ConfirmCleanup(preview, selectedPackages, selectedItems, out deleteFiles, out moveToRecycleBin))
                    return;
                if (deleteFiles && preview.HasUnconfirmedPaths)
                {
                    recoveredPathsConfirmed = ConfirmRecoveredCleanupPaths(preview);
                    if (!recoveredPathsConfirmed)
                    {
                        StatusSummary = Loc.Get("Status_CleanupUnconfirmedPath");
                        return;
                    }
                }
            }

            var completionStop = _queueManager.StopCompletionsForRemovalAsync(targets);
            reserved = true;
            var downloadStops = targets.Select(item => DownloadEngine.Instance.CancelOrPauseDownload(item.Id)).Where(task => task != null).Cast<Task>().ToArray();
            var nativeStop = _queueManager.StopNativeTransfersForRemovalAsync(targets);
            var pathStop = _queueManager.WaitForPathOperationsAsync(targets);
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var extractionStop = deleteFiles ? StopSelectedExtractionsAsync(selectedPackages, selectedItems, stopTimeout.Token) : Task.CompletedTask;
            var stop = Task.WhenAll(downloadStops.Append(completionStop).Append(nativeStop).Append(pathStop).Append(extractionStop));
            try
            {
                await stop.WaitAsync(stopTimeout.Token);
            }
            catch
            {
                await completionStop;
                _ = stop.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                throw;
            }

            foreach (var item in targets)
            {
                if (item.Status is DownloadStatus.Queued or DownloadStatus.InBrowser or DownloadStatus.InBrowserSlot1 or DownloadStatus.InBrowserSlot2 or DownloadStatus.SolvingCaptcha or DownloadStatus.WaitingForBrowser)
                {
                    item.Status = DownloadStatus.Paused;
                    item.StatusMessage = Loc.Get("Status_Paused");
                }
            }
            _queueManager.CloseBrowserWindowsForItems(targets);

            if (deleteFiles)
            {
                while (true)
                {
                    Task[] repairsToWait;
                    using (await PackagePathOperationGate.Instance.EnterAsync(stopTimeout.Token))
                    {
                        var current = CaptureCleanupSnapshot(selectedPackages, selectedItems);
                        await DiskCleanupRunner(() => current.Scan());
                        if (preview != null && !preview.HasSameSelectionPaths(current))
                        {
                            StatusSummary = Loc.Get("Status_CleanupChanged");
                            return;
                        }
                        if (current.HasUnconfirmedPaths && !recoveredPathsConfirmed)
                        {
                            StatusSummary = Loc.Get("Status_CleanupUnconfirmedPath");
                            return;
                        }
                        var extractingIds = current.Packages.Where(package => package.IsExtracting).Select(package => package.Id);
                        if (current.GetConflictingOperationIds(extractingIds).Count > 0)
                        {
                            StatusSummary = Loc.Get("Status_CleanupBusy");
                            return;
                        }
                        var repairs = Par2RepairService.Instance.GetActiveRepairs();
                        var conflicts = current.GetConflictingOperationIds(repairs.Keys, repairs);
                        repairsToWait = conflicts.Select(id => repairs[id].Finished).ToArray();
                        if (repairsToWait.Length == 0)
                        {
                            if (recoveredPathsConfirmed)
                                current.ConfirmRecoveredPaths();
                            await DiskCleanupRunner(() =>
                            {
                                if (moveToRecycleBin)
                                    current.Recycle(RecyclePaths);
                                else
                                    current.Delete();
                            });
                            ApplyHealedPaths(current, affectedPackages);
                            RemoveSelectionFromUi(selectedPackages, selectedItems);
                            removed = true;
                            break;
                        }
                    }
                    await Task.WhenAll(repairsToWait).WaitAsync(stopTimeout.Token);
                }
            }
            else
            {
                RemoveSelectionFromUi(selectedPackages, selectedItems);
                removed = true;
            }
        }
        catch (Exception ex)
        {
            if (moveToRecycleBin)
                AppLogger.Warn("[MainViewModel] Could not recycle the selected downloads.");
            else
                AppLogger.Warn($"[MainViewModel] Could not remove the selected downloads: {ex.Message}");
            StatusSummary = Loc.Get(moveToRecycleBin ? "Status_RecycleFailed" : "Status_CleanupFailed");
        }
        finally
        {
            if (reserved)
            {
                _ = _queueManager.ReleaseItemsFromRemovalAsync(targets, removed);
                foreach (var package in affectedPackages.Where(Packages.Contains))
                    LinkMetadataResolverService.TryUpdatePackageName(package);
            }
            IsRemovalInProgress = false;
            _removalFinished?.TrySetResult(true);
            if (_queueManager.IsRunning)
                _queueManager.ProcessQueue();
        }
    }

    private async Task StopSelectedExtractionsAsync(IReadOnlyCollection<DownloadPackage> selectedPackages, IReadOnlyCollection<DownloadItem> selectedItems, CancellationToken cancellationToken)
    {
        var packages = selectedPackages.Concat(Packages.Where(package => selectedItems.Any(package.Items.Contains))).Distinct().ToArray();
        while (packages.Any(package => ArchiveExtractionService.Instance.IsPackageExtracting(package.Id)))
        {
            foreach (var package in packages)
                ArchiveExtractionService.Instance.CancelPackageExtraction(package.Id);
            await Task.Delay(50, cancellationToken);
        }
    }

    private CleanupSnapshot CaptureCleanupSnapshot(IReadOnlyCollection<DownloadPackage> selectedPackages, IReadOnlyCollection<DownloadItem> selectedItems)
    {
        var packages = Packages.Select(package => package.CreateDiskCleanupSnapshot()).ToList();
        var selectedPackageIds = selectedPackages.Select(package => package.Id).ToHashSet();
        var selectedItemIds = selectedItems.Select(item => item.Id).ToHashSet();
        return new CleanupSnapshot(packages, selectedPackageIds, selectedItemIds);
    }

    private static void ApplyHealedPaths(CleanupSnapshot snapshot, IReadOnlyCollection<DownloadPackage> selectedPackages)
    {
        bool changed = false;
        foreach (var package in selectedPackages)
        {
            var copy = snapshot.Packages.FirstOrDefault(candidate => candidate.Id == package.Id);
            if (copy == null)
                continue;
            if (copy.RequiresRecoveredPathConfirmation != package.RequiresRecoveredPathConfirmation)
            {
                package.RequiresRecoveredPathConfirmation = copy.RequiresRecoveredPathConfirmation;
                changed = true;
            }
            if (string.Equals(copy.SaveDirectory, package.SaveDirectory, StringComparison.Ordinal))
                continue;
            package.SaveDirectory = copy.SaveDirectory;
            foreach (var item in package.Items)
            {
                var itemCopy = copy.Items.FirstOrDefault(candidate => candidate.Id == item.Id);
                if (itemCopy != null)
                    item.SaveFilePath = itemCopy.SaveFilePath;
            }
            changed = true;
        }
        if (changed)
            DownloadPersistenceService.Instance.RequestSave();
    }

    private bool ConfirmCleanup(CleanupSnapshot preview, IReadOnlyCollection<DownloadPackage> packages, IReadOnlyCollection<DownloadItem> items, out bool deleteFiles, out bool recycleFiles)
    {
        string? option = null;
        string title;
        string message;
        int count = packages.Count + items.Count;
        if (packages.Count == 1 && items.Count == 0)
        {
            title = Loc.Get("Dialog_DeletePackageTitle");
            message = Loc.Format("Dialog_DeletePackageMessage", packages.First().Name);
            if (preview.Files.Count == 1 || (preview.Files.Count == 0 && preview.HasEmptyPackageDirectory))
                option = Loc.Format("Dialog_DeletePackageOptionSingleFile", BytesToHumanReadableConverter.FormatBytes(preview.Size));
            else if (preview.Files.Count > 1)
                option = Loc.Format("Dialog_DeletePackageOptionFiles", preview.Files.Count, BytesToHumanReadableConverter.FormatBytes(preview.Size));
        }
        else if (items.Count == 1 && packages.Count == 0)
        {
            title = Loc.Get("Dialog_DeleteItemTitle");
            message = Loc.Format("Dialog_DeleteItemMessage", items.First().FileName);
            if (preview.Files.Count > 0)
                option = Loc.Format("Dialog_DeleteItemOptionFiles", BytesToHumanReadableConverter.FormatBytes(preview.Size));
        }
        else
        {
            title = Loc.Get("Dialog_DeleteMultipleTitle");
            message = Loc.Format("Dialog_DeleteMultipleMessage", count);
            if (preview.Files.Count > 0)
                option = Loc.Format("Dialog_DeleteMultipleOptionFiles", BytesToHumanReadableConverter.FormatBytes(preview.Size));
        }
        if (CleanupConfirmationOverride != null)
        {
            var response = CleanupConfirmationOverride(option);
            deleteFiles = response.DeleteFiles;
            recycleFiles = response.RecycleFiles;
            return response.Confirmed;
        }
        return Views.ConfirmDialog.ShowWithFileDeletionOptions(title, message, option, out deleteFiles, out recycleFiles);
    }

    private void RemoveSelectionFromUi(IReadOnlyCollection<DownloadPackage> packages, IReadOnlyCollection<DownloadItem> items)
    {
        foreach (var item in items)
        {
            var parent = Packages.FirstOrDefault(package => package.Items.Contains(item));
            if (parent == null)
                continue;
            parent.Items.Remove(item);
            if (parent.Items.Count == 0)
            {
                Packages.Remove(parent);
                if (SelectedPackage == parent)
                    SelectedPackage = null;
            }
            else
                parent.RecalculateAggregates();
            if (SelectedItem == item)
                SelectedItem = null;
        }
        foreach (var package in packages)
        {
            foreach (var child in package.ClippedPackages.ToList())
            {
                child.ParentPackageId = null;
                child.ParentPackageName = null;
                package.ClippedPackages.Remove(child);
                if (!RootPackages.Contains(child))
                    RootPackages.Add(child);
            }
            if (package.ParentPackageId.HasValue)
                Packages.FirstOrDefault(parent => parent.Id == package.ParentPackageId.Value)?.ClippedPackages.Remove(package);
            Packages.Remove(package);
            RootPackages.Remove(package);
            if (SelectedPackage == package)
                SelectedPackage = null;
        }
        RecalculateGlobalStats();
        DownloadPersistenceService.Instance.RequestSave();
    }

    private sealed partial class CleanupSnapshot
    {
        internal List<DownloadPackage> Packages { get; }
        private readonly HashSet<Guid> _packageIds;
        private readonly HashSet<Guid> _itemIds;
        internal HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal long Size { get; private set; }
        internal bool HasEmptyPackageDirectory { get; private set; }

        internal CleanupSnapshot(List<DownloadPackage> packages, HashSet<Guid> packageIds, HashSet<Guid> itemIds)
        {
            Packages = packages;
            _packageIds = packageIds;
            _itemIds = itemIds;
        }

        internal void Scan()
        {
            foreach (var package in Packages.Where(package => _packageIds.Contains(package.Id)))
            {
                Files.UnionWith(GetPackageFilesOnDisk(package, Packages, false));
                if (Directory.Exists(package.SaveDirectory) && !IsProtectedDirectory(package.SaveDirectory))
                    HasEmptyPackageDirectory = true;
            }
            foreach (var package in Packages)
            {
                foreach (var item in package.Items.Where(item => _itemIds.Contains(item.Id)))
                {
                    var protectedPaths = GetProtectedItemPaths(Packages, null, item.Id);
                    Files.UnionWith(GetItemFilesOnDisk(item, package.SaveDirectory).Where(path => !protectedPaths.Contains(path)));
                }
            }
            Size = GetTotalFilesSizeOnDisk(Files);
        }

        internal bool HasSameSelectionPaths(CleanupSnapshot other)
        {
            foreach (var package in Packages)
            {
                var otherPackage = other.Packages.FirstOrDefault(candidate => candidate.Id == package.Id);
                if (_packageIds.Contains(package.Id))
                {
                    if (otherPackage == null || !SamePath(package.Name, otherPackage.Name) ||
                        package.RequiresRecoveredPathConfirmation != otherPackage.RequiresRecoveredPathConfirmation ||
                        !SamePath(package.SaveDirectory, otherPackage.SaveDirectory) ||
                        !SamePath(package.ExtractionDirectory, otherPackage.ExtractionDirectory) ||
                        package.Items.Count != otherPackage.Items.Count)
                        return false;
                    foreach (var item in package.Items)
                    {
                        var otherItem = otherPackage.Items.FirstOrDefault(candidate => candidate.Id == item.Id);
                        if (otherItem == null || !SamePath(item.FileName, otherItem.FileName) || !SamePath(item.SaveFilePath, otherItem.SaveFilePath))
                            return false;
                    }
                }
                else
                {
                    foreach (var item in package.Items.Where(item => _itemIds.Contains(item.Id)))
                    {
                        var otherItem = otherPackage?.Items.FirstOrDefault(candidate => candidate.Id == item.Id);
                        if (otherItem == null || !SamePath(package.SaveDirectory, otherPackage!.SaveDirectory) ||
                            package.RequiresRecoveredPathConfirmation != otherPackage.RequiresRecoveredPathConfirmation ||
                            !SamePath(item.FileName, otherItem.FileName) || !SamePath(item.SaveFilePath, otherItem.SaveFilePath))
                            return false;
                    }
                }
            }
            return true;
        }

        private static bool SamePath(string? first, string? second)
            => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

        internal HashSet<Guid> GetConflictingOperationIds(IEnumerable<Guid> operationIds, IReadOnlyDictionary<Guid, Par2RepairService.RepairActivity>? repairs = null)
        {
            var ids = operationIds.ToHashSet();
            if (ids.Count == 0)
                return ids;
            try
            {
                var selected = Packages.Where(package => _packageIds.Contains(package.Id) ||
                    package.Items.Any(item => _itemIds.Contains(item.Id))).ToArray();
                var selectedDirectories = selected.SelectMany(GetPackageDirectories).ToArray();
                foreach (var id in ids.ToArray())
                {
                    var package = Packages.FirstOrDefault(candidate => candidate.Id == id);
                    if (package == null || selected.Any(candidate => candidate.Id == id))
                        continue;
                    var directories = GetPackageDirectories(package);
                    if (repairs != null && repairs.TryGetValue(id, out var repair))
                        directories = directories.Concat(repair.Directories);
                    var overlaps = directories.Any(directory => selectedDirectories.Any(selectedDirectory =>
                        IsWithinCleanupDirectory(directory, selectedDirectory) || IsWithinCleanupDirectory(selectedDirectory, directory)));
                    if (!overlaps)
                        ids.Remove(id);
                }
            }
            catch
            {
                return operationIds.ToHashSet();
            }
            return ids;
        }

        private static IEnumerable<string> GetPackageDirectories(DownloadPackage package)
        {
            if (!string.IsNullOrWhiteSpace(package.SaveDirectory))
                yield return package.SaveDirectory;
            if (!string.IsNullOrWhiteSpace(package.ExtractionDirectory))
                yield return package.ExtractionDirectory;
            foreach (var item in package.Items)
            {
                if (!string.IsNullOrWhiteSpace(item.SaveFilePath))
                {
                    var directory = Path.GetDirectoryName(item.SaveFilePath);
                    if (!string.IsNullOrWhiteSpace(directory))
                        yield return directory;
                }
            }
        }

        internal void Delete()
        {
            EnsureRecoveredPathsConfirmed();
            foreach (var package in Packages.ToList())
            {
                var selected = package.Items.Where(item => _itemIds.Contains(item.Id)).ToArray();
                foreach (var item in selected)
                {
                    var protectedPaths = GetProtectedItemPaths(Packages, null, item.Id);
                    foreach (var path in GetItemFilesOnDisk(item, package.SaveDirectory).Where(path => !protectedPaths.Contains(path)))
                        ArchiveExtractionService.DeleteOrMoveToTemp(path);
                    package.Items.Remove(item);
                }
                if (selected.Length > 0 && package.Items.Count == 0 && !_packageIds.Contains(package.Id))
                    Packages.Remove(package);
            }
            foreach (var package in Packages.Where(package => _packageIds.Contains(package.Id)).ToArray())
            {
                DeletePackageFilesFromDisk(package, Packages, false, false);
                Packages.Remove(package);
            }
        }
    }
}
