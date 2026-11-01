using System;
using System.Collections.Generic;
using System.IO;
using Reepax.Models;
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
        foreach (var package in packages)
        foreach (var item in package.ItemsSnapshot)
        {
            if (!string.IsNullOrWhiteSpace(item.SaveFilePath))
            {
                string path = item.SaveFilePath;
                if (!Path.IsPathRooted(path) && !string.IsNullOrWhiteSpace(package.SaveDirectory))
                    path = Path.Combine(package.SaveDirectory, path);
                EnsureNoOverlap(paths, path);
            }
            if (!string.IsNullOrWhiteSpace(package.SaveDirectory) && !string.IsNullOrWhiteSpace(item.FileName))
                EnsureNoOverlap(paths, Path.Combine(package.SaveDirectory, item.FileName));
        }
    }

    private static void EnsureNoOverlap(HashSet<string> paths, string path)
    {
        foreach (string reserved in ReservedPaths(path))
            if (paths.Contains(reserved)) throw new InvalidDataException(Loc.Get("Import_OccupiedTargets"));
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
