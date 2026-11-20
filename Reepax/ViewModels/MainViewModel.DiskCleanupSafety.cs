using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Reepax.Models;
using Reepax.Services.Storage;

namespace Reepax.ViewModels;

public partial class MainViewModel
{
    internal static bool IsCleanupDirectory(DownloadPackage package, string? path, IEnumerable<DownloadPackage>? packages, bool protectOtherPaths)
    {
        if (!IsDedicatedPackageDirectory(package, path, packages))
            return false;
        if (!protectOtherPaths || packages == null || path == null)
            return true;

        try
        {
            var directory = NormalizeCleanupPath(path);
            foreach (var other in packages.Where(other => other.Id != package.Id))
            {
                if (IsWithinCleanupDirectory(other.SaveDirectory, directory) ||
                    IsWithinCleanupDirectory(other.ExtractionDirectory, directory))
                    return false;
                if (other.IsExtracting && IsWithinCleanupDirectory(directory, other.ExtractionDirectory))
                    return false;
                if (other.ExtractedFiles.Any(file => IsWithinCleanupDirectory(file.Path, directory)) ||
                    other.Items.SelectMany(item => GetItemCandidatePaths(item, other.SaveDirectory))
                        .Any(candidate => IsWithinCleanupDirectory(candidate, directory)))
                    return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static HashSet<string> GetProtectedCleanupPaths(IEnumerable<DownloadPackage> packages,
        HashSet<Guid> packageIds, HashSet<Guid> itemIds)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            if (packageIds.Contains(package.Id)) continue;
            foreach (var file in package.ExtractedFiles)
                paths.Add(file.Path);
            foreach (var item in package.ItemsSnapshot)
                if (!itemIds.Contains(item.Id))
                    paths.UnionWith(GetItemCandidatePaths(item, package.SaveDirectory));
        }
        return paths;
    }

    internal static IEnumerable<string> GetItemCandidatePaths(DownloadItem item, string? fallbackDirectory)
    {
        var path = item.SaveFilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            if (string.IsNullOrWhiteSpace(item.FileName) || Path.GetFileName(item.FileName) != item.FileName ||
                item.FileName is "." or "..") yield break;
            path = item.FileName;
        }
        if (!Path.IsPathFullyQualified(path))
        {
            if (string.IsNullOrWhiteSpace(fallbackDirectory) || Path.IsPathRooted(path)) yield break;
            path = Path.Combine(fallbackDirectory, path);
        }

        string fullPath = NormalizeCleanupPath(path);
        // Older data may point at a working file while retaining the final name.
        foreach (var suffix in new[] { ".part.segments", ".part" })
        {
            if (fullPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFileName(fullPath[..^suffix.Length]), item.FileName, StringComparison.OrdinalIgnoreCase))
            {
                fullPath = fullPath[..^suffix.Length];
                break;
            }
        }
        foreach (var candidate in PackageImportTargets.ReservedPaths(fullPath))
            yield return candidate;
    }

    private static string NormalizeCleanupPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsWithinCleanupDirectory(string? path, string? directory)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory))
            return false;
        var fullPath = NormalizeCleanupPath(path);
        var fullDirectory = NormalizeCleanupPath(directory);
        return string.Equals(fullPath, fullDirectory, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(Path.EndsInDirectorySeparator(fullDirectory) ? fullDirectory : fullDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }
}
