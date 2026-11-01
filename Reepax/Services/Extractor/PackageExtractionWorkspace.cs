using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

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

    internal HashSet<string> Publish(IEnumerable<string> protectedSources, CancellationToken cancellationToken)
    {
        var protectedPaths = protectedSources.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var publishedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var workspace in _workspaces.Values)
        foreach (var path in Directory.EnumerateFiles(workspace.OutputDirectory, "*", SearchOption.AllDirectories))
        {
            string destination = Path.GetFullPath(Path.Combine(workspace.TargetDirectory, Path.GetRelativePath(workspace.OutputDirectory, path)));
            if (protectedPaths.Contains(destination))
                throw new IOException("An extracted file would overwrite a download source.");
            publishedPaths.Add(destination);
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
        return publishedPaths;
    }

    private static bool IsWithin(string path, string root) =>
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        foreach (var workspace in _workspaces.Values.Reverse()) workspace.Dispose();
    }
}
