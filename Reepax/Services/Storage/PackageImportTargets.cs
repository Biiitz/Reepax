using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Reepax.Models;
using Reepax.Services.Extractor;
using Reepax.Services.Localization;

namespace Reepax.Services.Storage;

internal static class PackageImportTargets
{
    private static readonly string[] ReservedSuffixes = { "", ".part", ".part.segments" };

    internal static HashSet<string> GetValidatedPaths(IEnumerable<DownloadItem> items)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            foreach (string path in ReservedPaths(item.SaveFilePath!))
            {
                // Final files must also be distinct from another download's working files.
                if (!paths.Add(path))
                    throw new InvalidDataException(Loc.Get("Import_OverlappingTargets"));
            }
        }
        return paths;
    }

    internal static void EnsurePackagesAvailable(HashSet<string> paths, IEnumerable<DownloadPackage> packages)
    {
        if (GetPackagePaths(packages).Any(paths.Contains))
            throw new InvalidDataException(Loc.Get("Import_OccupiedTargets"));
    }

    internal static bool HasOtherOwner(string path, DownloadItem item, IEnumerable<DownloadPackage> packages)
    {
        var targets = ReservedPaths(path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return GetPackagePaths(packages, except: item).Any(targets.Contains);
    }

    internal static HashSet<string> GetOccupiedPaths(IEnumerable<DownloadPackage> packages) =>
        GetPackagePaths(packages).ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal static string? AllocateNewTarget(string path, HashSet<string> occupied)
    {
        path = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(path)!;
        string name = Path.GetFileName(path);
        string extension = Path.GetExtension(name);
        string stem = Path.GetFileNameWithoutExtension(name);
        bool preserveName = ArchiveExtractionService.Instance.IsArchiveFile(name) ||
            extension.Equals(".par2", StringComparison.OrdinalIgnoreCase) ||
            (extension.Length == 4 && (extension[1] == 'z' || extension[1] == 'Z') &&
             char.IsAsciiDigit(extension[2]) && char.IsAsciiDigit(extension[3]));

        for (int i = 0; ; i++)
        {
            string candidate = i == 0 ? path : Path.Combine(directory, $"{stem}_{i}{extension}");
            var targets = ReservedPaths(candidate).ToArray();
            if (targets.All(target => !occupied.Contains(target) && !ReadAttributes(target).HasValue))
            {
                occupied.UnionWith(targets);
                return candidate;
            }
            if (preserveName)
                return null;
        }
    }

    private static IEnumerable<string> GetPackagePaths(IEnumerable<DownloadPackage> packages, DownloadItem? except = null)
    {
        foreach (var package in packages)
        foreach (var item in package.ItemsSnapshot)
        {
            if (except != null && item.Id == except.Id) continue;
            if (!string.IsNullOrWhiteSpace(item.SaveFilePath))
            {
                string path = item.SaveFilePath;
                if (!Path.IsPathRooted(path) && !string.IsNullOrWhiteSpace(package.SaveDirectory))
                    path = Path.Combine(package.SaveDirectory, path);
                foreach (string reserved in ReservedPaths(path)) yield return reserved;
            }
            if (!string.IsNullOrWhiteSpace(package.SaveDirectory) && !string.IsNullOrWhiteSpace(item.FileName))
            {
                string fallback = Path.Combine(package.SaveDirectory, item.FileName);
                if (!string.Equals(fallback, item.SaveFilePath, StringComparison.OrdinalIgnoreCase))
                    foreach (string reserved in ReservedPaths(fallback)) yield return reserved;
            }
        }
    }

    private static IEnumerable<string> ReservedPaths(string path)
    {
        string fullPath = Path.GetFullPath(path);
        foreach (string suffix in ReservedSuffixes) yield return fullPath + suffix;
    }

    internal static void EnsureDiskAvailable(HashSet<string> paths)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            for (string? directory = Path.GetDirectoryName(path); directory != null && directories.Add(directory);
                directory = Path.GetDirectoryName(directory))
            {
                var attributes = ReadAttributes(directory);
                if (attributes.HasValue && ((attributes.Value & FileAttributes.Directory) == 0 ||
                    (attributes.Value & FileAttributes.ReparsePoint) != 0))
                    throw new InvalidDataException(Loc.Get("Import_UnsafeDirectory"));
            }
            if (ReadAttributes(path).HasValue)
                throw new InvalidDataException(Loc.Get("Import_OccupiedTargets"));
        }
    }

    private static FileAttributes? ReadAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}
