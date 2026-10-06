using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Reepax.Converters;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Extractor;
using Reepax.Services.Localization;
using Reepax.Services.Storage;

namespace Reepax.ViewModels;

public partial class MainViewModel
{
    // =========================================================================
    // Package & Item Removal Commands and Disk Cleanup Helpers
    // =========================================================================

    [RelayCommand]
    public void RemovePackage(DownloadPackage? package)
    {
        RemovePackage(package, null);
    }

    public void RemovePackage(DownloadPackage? package, bool? deleteFilesFromDisk)
    {
        if (package == null)
            return;

        bool deleteFiles = false;

        if (deleteFilesFromDisk.HasValue)
        {
            deleteFiles = deleteFilesFromDisk.Value;
        }
        else
        {
            var filesOnDisk = GetPackageFilesOnDisk(package, Packages);
            string? optionText = null;

            if (filesOnDisk.Count == 1)
            {
                long size = GetTotalFilesSizeOnDisk(filesOnDisk);
                optionText = Loc.Format("Dialog_DeletePackageOptionSingleFile", BytesToHumanReadableConverter.FormatBytes(size));
            }
            else if (filesOnDisk.Count > 1)
            {
                long size = GetTotalFilesSizeOnDisk(filesOnDisk);
                optionText = Loc.Format("Dialog_DeletePackageOptionFiles", filesOnDisk.Count, BytesToHumanReadableConverter.FormatBytes(size));
            }
            else if (Directory.Exists(package.SaveDirectory) && !IsProtectedDirectory(package.SaveDirectory))
            {
                optionText = Loc.Format("Dialog_DeletePackageOptionSingleFile", BytesToHumanReadableConverter.FormatBytes(0));
            }

            if (!Views.ConfirmDialog.ShowWithOption(
                Loc.Get("Dialog_DeletePackageTitle"),
                Loc.Format("Dialog_DeletePackageMessage", package.Name),
                optionText,
                out deleteFiles,
                defaultOptionChecked: false,
                Loc.Get("Common_Yes"),
                Loc.Get("Common_No")))
            {
                return;
            }
        }

        foreach (var item in package.Items)
        {
            _ = DownloadEngine.Instance.CancelOrPauseDownload(item.Id, waitForCompletion: deleteFiles, timeoutMs: 500);
        }

        if (deleteFiles)
        {
            DeletePackageFilesFromDisk(package, Packages);
        }

        // Unclip any attached children so they are promoted to root and not lost
        foreach (var child in package.ClippedPackages.ToList())
        {
            child.ParentPackageId = null;
            child.ParentPackageName = null;
            package.ClippedPackages.Remove(child);
            if (!RootPackages.Contains(child))
            {
                RootPackages.Add(child);
            }
        }

        if (package.ParentPackageId.HasValue)
        {
            var parent = Packages.FirstOrDefault(p => p.Id == package.ParentPackageId.Value);
            parent?.ClippedPackages.Remove(package);
        }

        Packages.Remove(package);
        RootPackages.Remove(package);

        if (SelectedPackage == package)
        {
            SelectedPackage = null;
        }
        RecalculateGlobalStats();
        DownloadPersistenceService.Instance.RequestSave();
    }

    [RelayCommand]
    public void RemoveItem(DownloadItem? item)
    {
        RemoveItem(item, null);
    }

    public void RemoveItem(DownloadItem? item, bool? deleteFilesFromDisk)
    {
        if (item == null)
            return;

        DownloadPackage? parentPackage = null;
        foreach (var p in Packages)
        {
            if (p.Items.Contains(item))
            {
                parentPackage = p;
                break;
            }
        }

        bool deleteFiles = false;

        if (deleteFilesFromDisk.HasValue)
        {
            deleteFiles = deleteFilesFromDisk.Value;
        }
        else
        {
            var filesOnDisk = GetItemFilesOnDisk(item, parentPackage?.SaveDirectory);
            string? optionText = null;

            if (filesOnDisk.Count > 0)
            {
                long size = GetTotalFilesSizeOnDisk(filesOnDisk);
                optionText = Loc.Format("Dialog_DeleteItemOptionFiles", BytesToHumanReadableConverter.FormatBytes(size));
            }

            if (!Views.ConfirmDialog.ShowWithOption(
                Loc.Get("Dialog_DeleteItemTitle"),
                Loc.Format("Dialog_DeleteItemMessage", item.FileName),
                optionText,
                out deleteFiles,
                defaultOptionChecked: false,
                Loc.Get("Common_Yes"),
                Loc.Get("Common_No")))
            {
                return;
            }
        }

        _ = DownloadEngine.Instance.CancelOrPauseDownload(item.Id, waitForCompletion: deleteFiles, timeoutMs: 500);

        if (deleteFiles)
        {
            DeleteItemFilesFromDisk(item, parentPackage?.SaveDirectory);
        }

        foreach (var package in Packages.ToList())
        {
            if (package.Items.Contains(item))
            {
                package.Items.Remove(item);
                if (package.Items.Count == 0)
                {
                    Packages.Remove(package);
                    if (SelectedPackage == package)
                    {
                        SelectedPackage = null;
                    }
                }
                else
                {
                    package.RecalculateAggregates();
                }
                break;
            }
        }

        if (SelectedItem == item)
        {
            SelectedItem = null;
        }

        RecalculateGlobalStats();
    }

    #region Disk Cleanup Helpers

    public static bool IsDedicatedPackageDirectory(DownloadPackage package, string? dirPath, IEnumerable<DownloadPackage>? allPackages = null)
    {
        if (string.IsNullOrWhiteSpace(dirPath))
            return false;

        try
        {
            if (!Directory.Exists(dirPath) || IsProtectedDirectory(dirPath))
                return false;

            var fullDir = Path.GetFullPath(dirPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var packagesToCheck = allPackages ?? QueueManager.Instance?.Packages;
            if (packagesToCheck != null)
            {
                foreach (var other in packagesToCheck)
                {
                    if (other == package || other.Id == package.Id) continue;
                    if (other.ParentPackageId == package.Id) continue;
                    if (package.ClippedPackages.Any(c => c.Id == other.Id || c == other)) continue;

                    if (!string.IsNullOrWhiteSpace(other.SaveDirectory))
                    {
                        var otherFull = Path.GetFullPath(other.SaveDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        if (string.Equals(fullDir, otherFull, StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }
                    }
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static List<string> GetPackageFilesOnDisk(DownloadPackage package)
    {
        return GetPackageFilesOnDisk(package, null);
    }

    public static List<string> GetPackageFilesOnDisk(DownloadPackage package, IEnumerable<DownloadPackage>? allPackages)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Refresh & self-heal path in case of renaming or moved folders
        package.RefreshAndCheckExistsOnDisk();

        // 2. Check if SaveDirectory is a dedicated package folder
        if (IsDedicatedPackageDirectory(package, package.SaveDirectory, allPackages))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(package.SaveDirectory, "*", SearchOption.AllDirectories))
                {
                    result.Add(Path.GetFullPath(file));
                }
            }
            catch { }
        }
        else
        {
            // Non-dedicated (e.g. shared or root/Downloads directly): collect package item files specifically
            foreach (var item in package.Items)
            {
                var itemFiles = GetItemFilesOnDisk(item, package.SaveDirectory);
                foreach (var f in itemFiles)
                {
                    result.Add(f);
                }
            }

            // Also check for orphaned temp/par2 files belonging to this package in SaveDirectory
            if (!string.IsNullOrWhiteSpace(package.SaveDirectory) && Directory.Exists(package.SaveDirectory))
            {
                try
                {
                    var itemBaseNames = package.Items
                        .Where(i => !string.IsNullOrWhiteSpace(i.FileName))
                        .Select(i => Path.GetFileNameWithoutExtension(i.FileName))
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .ToList();

                    foreach (var f in Directory.GetFiles(package.SaveDirectory, "*", SearchOption.TopDirectoryOnly))
                    {
                        var fileName = Path.GetFileName(f);
                        bool matchesPackage = itemBaseNames.Any(baseName => fileName.Contains(baseName, StringComparison.OrdinalIgnoreCase));

                        if (matchesPackage && (ArchiveExtractionService.IsTempFile(f) || f.EndsWith(".par2", StringComparison.OrdinalIgnoreCase)))
                        {
                            result.Add(Path.GetFullPath(f));
                        }
                    }
                }
                catch { }

                // Check if a subfolder in SaveDirectory matches package name or safe name
                var safeName = PackageGrouper.MakeSafeDirectoryName(package.Name);
                var subfolderCandidates = new[]
                {
                    Path.Combine(package.SaveDirectory, package.Name),
                    Path.Combine(package.SaveDirectory, safeName)
                }.Distinct(StringComparer.OrdinalIgnoreCase);

                foreach (var subfolder in subfolderCandidates)
                {
                    if (Directory.Exists(subfolder) && !IsProtectedDirectory(subfolder) && IsDedicatedPackageDirectory(package, subfolder, allPackages))
                    {
                        try
                        {
                            foreach (var f in Directory.EnumerateFiles(subfolder, "*", SearchOption.AllDirectories))
                            {
                                result.Add(Path.GetFullPath(f));
                            }
                        }
                        catch { }
                    }
                }
            }
        }

        // 3. Check separate ExtractionDirectory if set, exists, and is different from SaveDirectory
        if (!string.IsNullOrWhiteSpace(package.ExtractionDirectory) &&
            Directory.Exists(package.ExtractionDirectory) &&
            !IsProtectedDirectory(package.ExtractionDirectory))
        {
            var fullExt = Path.GetFullPath(package.ExtractionDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullSave = !string.IsNullOrWhiteSpace(package.SaveDirectory)
                ? Path.GetFullPath(package.SaveDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                : null;

            // If ExtractionDirectory is not already inside or identical to SaveDirectory
            if (fullSave == null || (!fullExt.Equals(fullSave, StringComparison.OrdinalIgnoreCase) && !fullExt.StartsWith(fullSave + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                if (IsDedicatedPackageDirectory(package, package.ExtractionDirectory, allPackages))
                {
                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(package.ExtractionDirectory, "*", SearchOption.AllDirectories))
                        {
                            result.Add(Path.GetFullPath(file));
                        }
                    }
                    catch { }
                }
            }
        }

        // 4. Any items that point to explicit paths outside SaveDirectory
        foreach (var item in package.Items)
        {
            if (!string.IsNullOrWhiteSpace(item.SaveFilePath) && Path.IsPathRooted(item.SaveFilePath))
            {
                var itemFiles = GetItemFilesOnDisk(item, null);
                foreach (var f in itemFiles)
                {
                    result.Add(f);
                }
            }
        }

        return result.ToList();
    }

    public static List<string> GetItemFilesOnDisk(DownloadItem item, string? fallbackDir = null)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidatePaths = new List<string>();

        if (!string.IsNullOrWhiteSpace(item.SaveFilePath))
        {
            candidatePaths.Add(item.SaveFilePath);
            if (!Path.IsPathRooted(item.SaveFilePath) && !string.IsNullOrWhiteSpace(fallbackDir))
            {
                candidatePaths.Add(Path.Combine(fallbackDir, item.SaveFilePath));
            }
        }

        if (!string.IsNullOrWhiteSpace(fallbackDir) && !string.IsNullOrWhiteSpace(item.FileName))
        {
            candidatePaths.Add(Path.Combine(fallbackDir, item.FileName));
        }

        foreach (var basePath in candidatePaths)
        {
            try
            {
                // 1. Direct file
                if (File.Exists(basePath))
                {
                    result.Add(Path.GetFullPath(basePath));
                }

                // 2. Part file
                var partPath = basePath + ".part";
                if (File.Exists(partPath))
                {
                    result.Add(Path.GetFullPath(partPath));
                }

                // 3. Part segments file
                var segmentsPath = partPath + ".segments";
                if (File.Exists(segmentsPath))
                {
                    result.Add(Path.GetFullPath(segmentsPath));
                }

                // 4. Additional temporary sidecars (.segments, .tmp, .temp, .reepax_tmp)
                var directSegPath = basePath + ".segments";
                if (File.Exists(directSegPath))
                {
                    result.Add(Path.GetFullPath(directSegPath));
                }
                var tmpPath = basePath + ".tmp";
                if (File.Exists(tmpPath))
                {
                    result.Add(Path.GetFullPath(tmpPath));
                }
                var tempPath = basePath + ".temp";
                if (File.Exists(tempPath))
                {
                    result.Add(Path.GetFullPath(tempPath));
                }
                var reepaxTmpPath = basePath + ".reepax_tmp";
                if (File.Exists(reepaxTmpPath))
                {
                    result.Add(Path.GetFullPath(reepaxTmpPath));
                }

                // If basePath was already ending with .part or .part.segments
                if (basePath.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                {
                    var segPath = basePath + ".segments";
                    if (File.Exists(segPath))
                    {
                        result.Add(Path.GetFullPath(segPath));
                    }
                    var stripped = basePath.Substring(0, basePath.Length - 5);
                    if (File.Exists(stripped))
                    {
                        result.Add(Path.GetFullPath(stripped));
                    }
                }
                else if (basePath.EndsWith(".part.segments", StringComparison.OrdinalIgnoreCase))
                {
                    var stripped = basePath.Substring(0, basePath.Length - 14);
                    if (File.Exists(stripped))
                    {
                        result.Add(Path.GetFullPath(stripped));
                    }
                    var strippedPart = basePath.Substring(0, basePath.Length - 9);
                    if (File.Exists(strippedPart))
                    {
                        result.Add(Path.GetFullPath(strippedPart));
                    }
                }
            }
            catch { }
        }

        return result.ToList();
    }

    public static long GetTotalFilesSizeOnDisk(IEnumerable<string> filePaths)
    {
        long total = 0;
        foreach (var p in filePaths)
        {
            try
            {
                var fi = new FileInfo(p);
                if (fi.Exists)
                {
                    total += fi.Length;
                }
            }
            catch { }
        }
        return total;
    }

    public static void DeletePackageFilesFromDisk(DownloadPackage package)
    {
        DeletePackageFilesFromDisk(package, null);
    }

    public static void DeletePackageFilesFromDisk(DownloadPackage package, IEnumerable<DownloadPackage>? allPackages)
    {
        // 1. Cancel active extraction if currently running
        if (ArchiveExtractionService.Instance.IsPackageExtracting(package.Id))
        {
            ArchiveExtractionService.Instance.CancelPackageExtraction(package.Id);
            try { System.Threading.Thread.Sleep(100); } catch { }
        }

        // 2. Self-heal in case of renaming or moved folders
        package.RefreshAndCheckExistsOnDisk();

        // 3. Delete dedicated SaveDirectory directly from disk if applicable
        bool saveDirDeleted = false;
        if (IsDedicatedPackageDirectory(package, package.SaveDirectory, allPackages))
        {
            try
            {
                ArchiveExtractionService.PurgeTemporaryFilesInDirectory(package.SaveDirectory);
                ArchiveExtractionService.StripReadOnlyAttributes(package.SaveDirectory);
                Directory.Delete(package.SaveDirectory, recursive: true);
                saveDirDeleted = !Directory.Exists(package.SaveDirectory);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[MainViewModel] Could not delete dedicated package directory '{package.SaveDirectory}': {ex.Message}");
            }
        }

        // 4. If SaveDirectory was NOT dedicated or its directory deletion didn't remove everything:
        if (!saveDirDeleted)
        {
            var files = GetPackageFilesOnDisk(package, allPackages);
            foreach (var file in files)
            {
                try
                {
                    ArchiveExtractionService.DeleteOrMoveToTemp(file);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"[MainViewModel] Could not delete package file {file}: {ex.Message}");
                }
            }

            // Check if matching package subfolder exists in SaveDirectory
            if (!string.IsNullOrWhiteSpace(package.SaveDirectory) && Directory.Exists(package.SaveDirectory))
            {
                var safeName = PackageGrouper.MakeSafeDirectoryName(package.Name);
                var subfolderCandidates = new[]
                {
                    Path.Combine(package.SaveDirectory, package.Name),
                    Path.Combine(package.SaveDirectory, safeName)
                }.Distinct(StringComparer.OrdinalIgnoreCase);

                foreach (var subfolder in subfolderCandidates)
                {
                    if (Directory.Exists(subfolder) && !IsProtectedDirectory(subfolder) && IsDedicatedPackageDirectory(package, subfolder, allPackages))
                    {
                        try
                        {
                            ArchiveExtractionService.PurgeTemporaryFilesInDirectory(subfolder);
                            ArchiveExtractionService.StripReadOnlyAttributes(subfolder);
                            Directory.Delete(subfolder, recursive: true);
                        }
                        catch { }
                    }
                }
            }

            // Clean up package save directory if it's now completely empty and not protected
            try
            {
                var dir = package.SaveDirectory;
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) && !IsProtectedDirectory(dir))
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[MainViewModel] Could not delete empty package directory: {ex.Message}");
            }
        }

        // 5. Delete separate dedicated ExtractionDirectory directly if applicable
        if (!string.IsNullOrWhiteSpace(package.ExtractionDirectory) &&
            Directory.Exists(package.ExtractionDirectory) &&
            !IsProtectedDirectory(package.ExtractionDirectory) &&
            IsDedicatedPackageDirectory(package, package.ExtractionDirectory, allPackages))
        {
            try
            {
                ArchiveExtractionService.PurgeTemporaryFilesInDirectory(package.ExtractionDirectory);
                ArchiveExtractionService.StripReadOnlyAttributes(package.ExtractionDirectory);
                Directory.Delete(package.ExtractionDirectory, recursive: true);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[MainViewModel] Could not delete extraction directory '{package.ExtractionDirectory}': {ex.Message}");
            }
        }
    }

    public static void DeleteItemFilesFromDisk(DownloadItem item, string? fallbackDir = null)
    {
        var files = GetItemFilesOnDisk(item, fallbackDir);
        foreach (var file in files)
        {
            try
            {
                ArchiveExtractionService.DeleteOrMoveToTemp(file);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[MainViewModel] Could not delete item file {file}: {ex.Message}");
            }
        }
    }

    public static bool IsProtectedDirectory(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
                return true;

            var defaultDir = SettingsService.Instance?.Settings?.DefaultDownloadDirectory;
            if (!string.IsNullOrWhiteSpace(defaultDir))
            {
                var fullDefault = Path.GetFullPath(defaultDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(fullPath, fullDefault, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            var specialFolders = new[]
            {
                Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolder.DesktopDirectory,
                Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.Windows,
                Environment.SpecialFolder.System
            };

            foreach (var sf in specialFolders)
            {
                var folderPath = Environment.GetFolderPath(sf);
                if (!string.IsNullOrWhiteSpace(folderPath))
                {
                    var fullSpecial = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (string.Equals(fullPath, fullSpecial, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                var userDownloads = Path.Combine(userProfile, "Downloads").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(fullPath, Path.GetFullPath(userDownloads), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }

        return false;
    }

    #endregion

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    public void DeleteSelected()
    {
        if (SelectedMainTab != AppMainTab.Downloads) return;
        var itemToDelete = SelectedItem ?? Packages.SelectMany(p => p.Items).FirstOrDefault(i => i.IsSelected);
        if (itemToDelete != null)
        {
            RemoveItem(itemToDelete);
            return;
        }

        var packageToDelete = SelectedPackage ?? Packages.FirstOrDefault(p => p.IsSelected);
        if (packageToDelete != null)
        {
            RemovePackage(packageToDelete);
        }
    }
}
