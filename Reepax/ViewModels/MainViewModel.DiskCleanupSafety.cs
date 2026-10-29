using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Reepax.Models;

namespace Reepax.ViewModels;

public partial class MainViewModel
{
    private static bool IsCleanupDirectory(DownloadPackage package, string? path, IEnumerable<DownloadPackage>? packages, bool protectOtherPaths)
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
                if (other.Items.SelectMany(item => GetItemCandidatePaths(item, other.SaveDirectory))
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

    private static HashSet<string> GetProtectedItemPaths(IEnumerable<DownloadPackage>? packages, Guid? excludedPackageId, Guid? excludedItemId)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (packages == null)
            return paths;
        foreach (var package in packages)
        {
            if (package.Id == excludedPackageId)
                continue;
            foreach (var item in package.Items)
            {
                if (item.Id != excludedItemId)
                    paths.UnionWith(GetItemCandidatePaths(item, package.SaveDirectory));
            }
        }
        return paths;
    }

    private static IEnumerable<string> GetItemCandidatePaths(DownloadItem item, string? fallbackDirectory)
    {
        var bases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(item.SaveFilePath))
        {
            bases.Add(item.SaveFilePath);
            if (!Path.IsPathRooted(item.SaveFilePath) && !string.IsNullOrWhiteSpace(fallbackDirectory))
                bases.Add(Path.Combine(fallbackDirectory, item.SaveFilePath));
        }
        if (!string.IsNullOrWhiteSpace(fallbackDirectory) && !string.IsNullOrWhiteSpace(item.FileName))
            bases.Add(Path.Combine(fallbackDirectory, item.FileName));

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in bases)
        {
            foreach (var suffix in new[] { "", ".part", ".part.segments", ".segments", ".tmp", ".temp", ".reepax_tmp" })
                paths.Add(NormalizeCleanupPath(path + suffix));
            if (path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                paths.Add(NormalizeCleanupPath(path[..^5]));
            else if (path.EndsWith(".part.segments", StringComparison.OrdinalIgnoreCase))
            {
                paths.Add(NormalizeCleanupPath(path[..^14]));
                paths.Add(NormalizeCleanupPath(path[..^9]));
            }
        }
        return paths;
    }

    private static string NormalizeCleanupPath(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsWithinCleanupDirectory(string? path, string? directory)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory))
            return false;
        var fullPath = NormalizeCleanupPath(path);
        var fullDirectory = NormalizeCleanupPath(directory);
        return string.Equals(fullPath, fullDirectory, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
