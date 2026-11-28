using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Reepax.Models;
using Reepax.Services.Localization;

namespace Reepax.Services.Extractor;

internal sealed class PackageExtractionWorkspace : IDisposable
{
    private readonly string[] _roots;
    private readonly Dictionary<string, ArchiveExtractionWorkspace> _workspaces = new(StringComparer.OrdinalIgnoreCase);

    internal PackageExtractionWorkspace(IEnumerable<string> targets)
    {
        var directories = targets.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _roots = directories.Where(path => !directories.Any(other =>
            !string.Equals(path, other, StringComparison.OrdinalIgnoreCase) && IsWithin(path, other))).ToArray();
    }

    internal string GetStagingDirectory(string target)
    {
        target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        string root = _roots.First(root => string.Equals(root, target, StringComparison.OrdinalIgnoreCase) || IsWithin(target, root));
        if (!_workspaces.TryGetValue(root, out var workspace))
            _workspaces.Add(root, workspace = new ArchiveExtractionWorkspace(root));
        return Path.Combine(workspace.OutputDirectory, Path.GetRelativePath(root, target));
    }

    internal ExtractedPackageFile[] Publish(IEnumerable<string> reservedPaths, CancellationToken cancellationToken)
    {
        var protectedPaths = reservedPaths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var protectedParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in protectedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parent = Path.GetDirectoryName(path);
            while (parent != null && protectedParents.Add(parent))
                parent = Path.GetDirectoryName(parent);
        }

        var publishedFiles = new List<ExtractedPackageFile>();
        foreach (var workspace in _workspaces.Values)
        foreach (var entry in new DirectoryInfo(workspace.OutputDirectory).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(workspace.OutputDirectory, entry.FullName);
            string destination = Path.GetFullPath(Path.Combine(workspace.TargetDirectory, relative));
            if (protectedPaths.Contains(destination) || (entry is FileInfo && protectedParents.Contains(destination)))
                throw new IOException(Loc.Get("Extraction_TargetReserved"));
            if (entry is FileInfo file)
                publishedFiles.Add(new ExtractedPackageFile(destination, file.Length, file.LastWriteTimeUtc.Ticks));
        }
        try
        {
            foreach (var workspace in _workspaces.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                workspace.Publish(retainRollback: true, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            ArchiveExtractionWorkspace.CommitAll(_workspaces.Values);
        }
        catch
        {
            // A later destination failure must also restore earlier destinations.
            bool recoveryFailed = false;
            foreach (var workspace in _workspaces.Values.Reverse())
            {
                try { workspace.RollbackPublication(); }
                catch { recoveryFailed = true; }
            }
            if (recoveryFailed) throw new IOException("Extraction recovery files were retained.");
            throw;
        }
        return publishedFiles.ToArray();
    }

    private static bool IsWithin(string path, string root) =>
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        foreach (var workspace in _workspaces.Values.Reverse()) workspace.Dispose();
    }
}
