using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Extractor;
using Reepax.Services.Storage;

namespace Reepax.ViewModels;

public partial class MainViewModel
{
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
            var dirName = Path.GetFileName(fullDir);
            if (!IsDirectoryNameMatchingPackage(package, dirName))
                return false;

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

                    if (!string.IsNullOrWhiteSpace(other.ExtractionDirectory))
                    {
                        var otherExtFull = Path.GetFullPath(other.ExtractionDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        if (string.Equals(fullDir, otherExtFull, StringComparison.OrdinalIgnoreCase))
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

    // Verifies that a directory name explicitly belongs to the package rather than an arbitrary user folder.
    private static bool IsDirectoryNameMatchingPackage(DownloadPackage package, string dirName)
    {
        if (string.IsNullOrWhiteSpace(dirName))
            return false;

        if (dirName.StartsWith("ReepaxPkg", StringComparison.OrdinalIgnoreCase) ||
            dirName.StartsWith("Reepax_DedicatedDirTest", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(package.Name))
            return false;

        var rawName = package.Name.Trim();
        var safeName = PackageGrouper.MakeSafeDirectoryName(rawName);
        var cleanName = PackageGrouper.SanitizeCleanName(rawName);

        if (string.Equals(dirName, rawName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(dirName, safeName, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(cleanName) && string.Equals(dirName, cleanName, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(safeName) && safeName.Length >= 4 &&
            dirName.StartsWith(safeName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (rawName.StartsWith("Package ", StringComparison.OrdinalIgnoreCase))
        {
            var shortPkg = "Pkg" + rawName.Substring("Package ".Length).Trim();
            if (shortPkg.Length >= 4 && dirName.StartsWith(shortPkg, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static List<string> GetPackageFilesOnDisk(DownloadPackage package)
        => GetPackageFilesOnDisk(package, null);

    public static List<string> GetPackageFilesOnDisk(DownloadPackage package, IEnumerable<DownloadPackage>? allPackages)
    {
        package.RefreshAndCheckExistsOnDisk();
        var protectedPaths = GetProtectedCleanupPaths(GetCleanupPackages(allPackages), [package.Id], []);
        return GetOwnedPackageFiles(package).Where(path => !protectedPaths.Contains(path))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<DownloadPackage> GetCleanupPackages(IEnumerable<DownloadPackage>? packages)
        => packages ?? (QueueManager.IsInitialized ? QueueManager.Instance.Packages.ToArray() : []);

    private static IEnumerable<string> GetOwnedPackageFiles(DownloadPackage package)
    {
        foreach (var item in package.ItemsSnapshot)
            foreach (var path in GetItemFilesOnDisk(item, package.SaveDirectory))
                yield return path;

        foreach (var file in package.ExtractedFiles)
        {
            if (string.IsNullOrWhiteSpace(file.Path) || !Path.IsPathFullyQualified(file.Path)) continue;
            var info = new FileInfo(file.Path);
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0 &&
                info.Length == file.Length && info.LastWriteTimeUtc.Ticks == file.LastWriteTicks)
                yield return NormalizeCleanupPath(file.Path);
        }
    }

    public static List<string> GetItemFilesOnDisk(DownloadItem item, string? fallbackDir = null)
        => GetItemCandidatePaths(item, fallbackDir).Where(File.Exists).ToList();

    private static IEnumerable<string> GetPackageCleanupDirectories(DownloadPackage package,
        IEnumerable<DownloadPackage> allPackages, IReadOnlyCollection<string> files)
    {
        foreach (var root in new[] { package.SaveDirectory, package.ExtractionDirectory })
        {
            if (string.IsNullOrWhiteSpace(root) || !IsCleanupDirectory(package, root, allPackages, protectOtherPaths: true))
                continue;
            var directory = NormalizeCleanupPath(root);
            yield return directory;
            var trackedPaths = files.Concat(package.ExtractedFiles.Select(file => file.Path));
            foreach (var file in trackedPaths.Where(path => IsWithinCleanupDirectory(path, directory)))
            {
                for (var parent = Path.GetDirectoryName(file);
                     parent != null && !string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase);
                     parent = Path.GetDirectoryName(parent))
                    yield return parent;
            }
        }
    }

    private static bool IsEmptyCleanupDirectory(string path) =>
        Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 &&
        !Directory.EnumerateFileSystemEntries(path).Any();

    private static void RemoveEmptyCleanupDirectories(IEnumerable<string> paths)
    {
        var remaining = new List<string>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(path => path.Length))
        {
            try
            {
                if (IsEmptyCleanupDirectory(path)) Directory.Delete(path, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (IsEmptyCleanupDirectory(path)) remaining.Add(path);
            }
        }
        if (remaining.Count > 0)
            throw new FileCleanupException(remaining);
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
        => DeletePackageFilesFromDisk(package, null);

    public static void DeletePackageFilesFromDisk(DownloadPackage package, IEnumerable<DownloadPackage>? allPackages)
    {
        if (ArchiveExtractionService.Instance.IsPackageExtracting(package.Id))
        {
            ArchiveExtractionService.Instance.CancelPackageExtraction(package.Id);
            throw new InvalidOperationException("Package extraction is still running.");
        }
        var packages = GetCleanupPackages(allPackages).ToArray();
        var files = GetPackageFilesOnDisk(package, packages);
        if (package.RequiresRecoveredPathConfirmation)
            throw new InvalidOperationException("Recovered package paths require explicit confirmation before cleanup.");

        var directories = GetPackageCleanupDirectories(package, packages, files).ToArray();
        DeleteCleanupFiles(files);
        RemoveEmptyCleanupDirectories(directories);
    }

    public static void DeleteItemFilesFromDisk(DownloadItem item, string? fallbackDir = null)
    {
        var protectedPaths = GetProtectedCleanupPaths(GetCleanupPackages(null), [], [item.Id]);
        DeleteCleanupFiles(GetItemFilesOnDisk(item, fallbackDir).Where(path => !protectedPaths.Contains(path)));
    }

    private static void DeleteCleanupFiles(IEnumerable<string> paths)
    {
        var failedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReadOnly) != 0 && (attributes & FileAttributes.Directory) == 0)
                        File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                }
                if (Directory.Exists(path) || !ArchiveExtractionService.DeleteOrMoveToTemp(path))
                    failedPaths.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failedPaths.Add(path);
                AppLogger.Warn("[MainViewModel] Could not delete a selected file.", ex);
            }
        }
        if (failedPaths.Count > 0)
            throw new FileCleanupException(failedPaths);
    }

    internal sealed class FileCleanupException(IReadOnlyCollection<string> remainingPaths)
        : IOException("Some selected files or directories could not be removed.")
    {
        internal IReadOnlyCollection<string> RemainingPaths { get; } = remainingPaths;
    }

    private static readonly Environment.SpecialFolder[] ProtectedSpecialFolders =
    {
        // User profile & personal media/document folders
        Environment.SpecialFolder.UserProfile,
        Environment.SpecialFolder.Desktop,
        Environment.SpecialFolder.DesktopDirectory,
        Environment.SpecialFolder.MyDocuments,
        Environment.SpecialFolder.MyMusic,
        Environment.SpecialFolder.MyVideos,
        Environment.SpecialFolder.MyPictures,
        Environment.SpecialFolder.Favorites,
        Environment.SpecialFolder.Recent,
        Environment.SpecialFolder.SendTo,
        Environment.SpecialFolder.StartMenu,
        Environment.SpecialFolder.Programs,
        Environment.SpecialFolder.Startup,
        Environment.SpecialFolder.Templates,
        Environment.SpecialFolder.AdminTools,

        // Shared / Public user folders
        Environment.SpecialFolder.CommonDesktopDirectory,
        Environment.SpecialFolder.CommonDocuments,
        Environment.SpecialFolder.CommonMusic,
        Environment.SpecialFolder.CommonVideos,
        Environment.SpecialFolder.CommonPictures,
        Environment.SpecialFolder.CommonStartMenu,
        Environment.SpecialFolder.CommonPrograms,
        Environment.SpecialFolder.CommonStartup,
        Environment.SpecialFolder.CommonTemplates,
        Environment.SpecialFolder.CommonAdminTools,

        // Application data & profile state folders
        Environment.SpecialFolder.ApplicationData,
        Environment.SpecialFolder.LocalApplicationData,
        Environment.SpecialFolder.CommonApplicationData,
        Environment.SpecialFolder.InternetCache,
        Environment.SpecialFolder.Cookies,
        Environment.SpecialFolder.History,

        // Program & Windows operating system directories
        Environment.SpecialFolder.ProgramFiles,
        Environment.SpecialFolder.ProgramFilesX86,
        Environment.SpecialFolder.CommonProgramFiles,
        Environment.SpecialFolder.CommonProgramFilesX86,
        Environment.SpecialFolder.Windows,
        Environment.SpecialFolder.System,
        Environment.SpecialFolder.SystemX86,
        Environment.SpecialFolder.Fonts,
        Environment.SpecialFolder.Resources
    };

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

            foreach (var sf in ProtectedSpecialFolders)
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

}
