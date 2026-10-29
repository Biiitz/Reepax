using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Reepax.Services.Extractor;
using Reepax.Services.SystemIntegration;

namespace Reepax.ViewModels;

public partial class MainViewModel
{
    internal Action<IReadOnlyCollection<string>> RecyclePaths { get; set; } = RecycleBinService.Move;

    private sealed partial class CleanupSnapshot
    {
        internal void Recycle(Action<IReadOnlyCollection<string>> recycle)
        {
            EnsureRecoveredPathsConfirmed();
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var package in Packages.Where(package => _packageIds.Contains(package.Id)))
            {
                var candidates = new List<string?> { package.SaveDirectory, package.ExtractionDirectory };
                if (!string.IsNullOrWhiteSpace(package.SaveDirectory))
                {
                    candidates.Add(Path.Combine(package.SaveDirectory, PackageGrouper.MakeSafeDirectoryName(package.Name)));
                }
                foreach (var candidate in candidates)
                {
                    if (IsCleanupDirectory(package, candidate, Packages, true))
                        directories.Add(NormalizeCleanupPath(candidate!));
                }
            }

            var roots = directories.Where(directory => !directories.Any(other =>
                !string.Equals(directory, other, StringComparison.OrdinalIgnoreCase) &&
                IsWithinCleanupDirectory(directory, other))).ToArray();
            var paths = roots.Concat(Files.Where(path => !roots.Any(directory =>
                IsWithinCleanupDirectory(path, directory)))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length > 0)
                recycle(paths);
            if (paths.Any(path => File.Exists(path) || Directory.Exists(path)))
                throw new IOException("Some selected paths could not be recycled.");
        }
    }
}
