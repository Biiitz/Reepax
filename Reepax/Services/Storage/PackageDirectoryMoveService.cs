using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Extractor;

namespace Reepax.Services.Storage;

internal sealed class PackageDirectoryMoveService
{
    private readonly bool _allowHardLinks;
    private readonly Action<string>? _fileCopied;

    internal PackageDirectoryMoveService(bool allowHardLinks = true, Action<string>? fileCopied = null)
    {
        _allowHardLinks = allowHardLinks;
        _fileCopied = fileCopied;
    }

    internal sealed record FileEntry(string Source, string RelativePath, RejectedDownloadFiles.Identity Identity,
        bool IsExtractionOutput = false);
    internal sealed record Plan(DownloadPackage Package, string? Source, string Target, bool CleanSourceDirectory,
        Dictionary<DownloadItem, string> ItemPaths, string? ExtractionDirectory, FileEntry[] Files, string[] Directories,
        ExtractedPackageFile[] ExtractedFiles, bool UseExistingEmptyDestination)
    {
        internal void ApplyPaths()
        {
            Package.SaveDirectory = Target;
            foreach (var entry in ItemPaths) entry.Key.SaveFilePath = entry.Value;
            Package.ExtractionDirectory = ExtractionDirectory;
            Package.ExtractedFiles = ExtractedFiles;
            DownloadPersistenceService.Instance.RequestSave();
        }
    }

    private sealed record ProtectedCopy(RelocationFileAccess.SourceLease Source, byte[]? Hash, string RelativePath) : IDisposable
    {
        public void Dispose() => Source.Dispose();
    }

    internal static Plan[] Prepare(DownloadPackage[] packages, DownloadPackage[] allPackages, string destination,
        bool useExactDestination = false)
    {
        var root = Path.GetFullPath(destination);
        EnsurePlainPath(root);
        allPackages = IncludeClippedPackages(packages.Concat(allPackages));
        var plans = packages.Select(package => PreparePackage(package, allPackages, root, useExactDestination)).ToArray();
        if (plans.Select(plan => plan.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plans.Length)
            throw new IOException("Package destinations overlap.");
        foreach (var plan in plans)
        {
            foreach (var other in plans)
            {
                if (other.CleanSourceDirectory && other.Files.Length > 0 && other.Source != null && !Same(plan.Target, plan.Source) &&
                    (Within(plan.Target, other.Source) || Within(other.Source, plan.Target)))
                    throw new IOException("Source and destination directories overlap.");
            }
        }
        return plans;
    }

    private static DownloadPackage[] IncludeClippedPackages(IEnumerable<DownloadPackage> packages)
    {
        var visited = new HashSet<DownloadPackage>();
        var pending = new Stack<DownloadPackage>(packages);
        while (pending.TryPop(out var package))
        {
            if (!visited.Add(package)) continue;
            foreach (var child in package.ClippedPackages.ToArray()) pending.Push(child);
        }
        return visited.ToArray();
    }

    private static Plan PreparePackage(DownloadPackage package, DownloadPackage[] allPackages, string root, bool useExactDestination)
    {
        if (package.RequiresRecoveredPathConfirmation)
            throw new IOException("The recovered source directory requires confirmation.");
        string target = root;
        if (!useExactDestination)
        {
            string name = PackageGrouper.MakeSafeDirectoryName(package.Name);
            if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || Path.GetFileName(name) != name)
                throw new IOException("Invalid package directory name.");
            target = Path.GetFullPath(Path.Combine(root, name));
        }
        var source = string.IsNullOrWhiteSpace(package.SaveDirectory) ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(package.SaveDirectory));
        EnsurePlainPath(target);
        if (source != null) EnsurePlainPath(source);
        bool same = Same(source, target);
        var otherPackages = allPackages.Where(other => other != package).ToArray();
        bool useEmptyDestination = useExactDestination && !same && Directory.Exists(target) &&
            !Directory.EnumerateFileSystemEntries(target).Any();
        if (!same && (otherPackages.Any(other => ReferencesDirectory(other, target, includeAncestors: false)) ||
            (Path.Exists(target) && !useEmptyDestination)))
            throw new IOException("The destination already exists.");

        var otherFiles = PackageImportTargets.GetOccupiedPaths(otherPackages);
        otherFiles.UnionWith(otherPackages.SelectMany(other => other.ExtractedFiles)
            .Where(file => !string.IsNullOrWhiteSpace(file.Path) && Path.IsPathFullyQualified(file.Path))
            .Select(file => Path.GetFullPath(file.Path)));
        var itemPaths = new Dictionary<DownloadItem, string>();
        var files = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var itemSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in package.ItemsSnapshot)
        {
            if (string.IsNullOrWhiteSpace(item.FileName) || Path.GetFileName(item.FileName) != item.FileName ||
                item.FileName is "." or ".." || item.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new IOException("Invalid download file name.");
            var original = ViewModels.MainViewModel.GetItemCandidatePaths(item, source).FirstOrDefault();
            string relative = original != null && source != null && Within(original, source)
                ? Path.GetRelativePath(source, original) : item.FileName;
            itemPaths.Add(item, same && original != null ? original : Path.Combine(target, relative));
            foreach (var candidate in PackageImportTargets.ReservedPaths(Path.Combine(target, relative)))
                if (!destinations.Add(candidate)) throw new IOException("Download item destinations overlap.");
            if (original == null) continue;
            foreach (var path in PackageImportTargets.ReservedPaths(original))
            {
                if (!itemSources.Add(path)) throw new IOException("Download source paths overlap.");
                if (File.Exists(path))
                    AddFile(path, relative + path[original.Length..]);
            }
        }

        var extraction = package.ExtractionDirectory;
        var directories = new List<string>();
        var outputs = package.ExtractedFiles.ToArray();
        if (source != null)
        {
            for (int index = 0; index < outputs.Length; index++)
            {
                var output = outputs[index];
                if (string.IsNullOrWhiteSpace(output.Path) || !Path.IsPathFullyQualified(output.Path) || !Within(output.Path, source)) continue;
                var info = new FileInfo(output.Path);
                if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    info.Length != output.Length || info.LastWriteTimeUtc.Ticks != output.LastWriteTicks) continue;
                string relative = Path.GetRelativePath(source, info.FullName);
                if (itemSources.Contains(info.FullName) ||
                    !destinations.Add(Path.Combine(target, relative)))
                    throw new IOException("An extraction output overlaps a download path.");
                AddFile(info.FullName, relative, output);
                if (!same) outputs[index] = output with { Path = Path.Combine(target, relative) };
            }

            if (!string.IsNullOrWhiteSpace(extraction) && Within(extraction, source) &&
                !otherPackages.Any(other => ReferencesDirectory(other, extraction, includeAncestors: false)))
            {
                string relative = Path.GetRelativePath(source, extraction);
                bool empty = Directory.Exists(extraction) && !Directory.EnumerateFileSystemEntries(extraction).Any();
                if (empty && relative != ".")
                {
                    EnsurePlainPath(extraction);
                    directories.Add(relative);
                }
                if (!same && (relative == "." || empty ||
                    files.Values.Any(file => file.IsExtractionOutput && Within(file.Source, extraction))))
                    extraction = Path.GetFullPath(Path.Combine(target, relative));
            }
        }

        bool cleanSource = source != null && Directory.Exists(source) &&
            ViewModels.MainViewModel.IsDedicatedPackageDirectory(package, source, allPackages) &&
            !otherPackages.Any(other => ReferencesDirectory(other, source)) &&
            ContainsOnlyPlannedEntries(source, files.Keys, directories);
        return new Plan(package, source, target, cleanSource, itemPaths, extraction, files.Values.ToArray(),
            directories.ToArray(), outputs, useEmptyDestination);

        void AddFile(string path, string relative, ExtractedPackageFile? output = null)
        {
            EnsurePlainPath(path);
            if (otherFiles.Contains(path))
                throw new IOException("A source file is shared by another package.");
            var identity = RejectedDownloadFiles.CaptureIdentity(path)
                ?? throw new IOException("The source file could not be identified.");
            if (output != null && (identity.Length != (ulong)output.Length ||
                DateTime.FromFileTimeUtc(identity.Written).Ticks != output.LastWriteTicks))
                throw new IOException("An extraction output changed after relocation was planned.");
            if (!files.TryAdd(path, new FileEntry(path, relative, identity, output != null)))
                throw new IOException("Package source files overlap.");
        }
    }

    internal async Task<bool> ExecuteAsync(Plan plan, Action<string, string>? moveDirectory = null,
        bool moveEmptyDirectory = false)
    {
        if (Same(plan.Source, plan.Target) || (plan.Files.Length == 0 && !moveEmptyDirectory)) return true;
        EnsurePlainPath(Path.GetDirectoryName(plan.Target)!);
        Directory.CreateDirectory(Path.GetDirectoryName(plan.Target)!);
        var stage = Path.Combine(Path.GetDirectoryName(plan.Target)!, ".download-move-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var copies = new List<ProtectedCopy>();
        var publishedFiles = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var directory in plan.Directories) Directory.CreateDirectory(Path.Combine(stage, directory));
            foreach (var file in plan.Files)
            {
                var output = Path.Combine(stage, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                bool tryLink = _allowHardLinks && Same(Path.GetPathRoot(file.Source), Path.GetPathRoot(output));
                var staged = await StageFileAsync(file, output, tryLink).ConfigureAwait(false);
                copies.Add(staged);
                if (staged.Hash != null) _fileCopied?.Invoke(file.Source);
            }
            PublishDirectory(stage, plan, moveDirectory ?? Directory.Move);
            foreach (var copy in copies)
            {
                string publishedPath = Path.Combine(plan.Target, copy.RelativePath);
                if (copy.Hash == null)
                {
                    if (!RejectedDownloadFiles.MatchesOpenFile(publishedPath, copy.Source.Stream.SafeFileHandle))
                        throw new IOException("The linked destination changed before relocation completed.");
                    continue;
                }
                var published = new FileStream(publishedPath, FileMode.Open,
                    FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
                publishedFiles.Add(copy.RelativePath, published);
                // Directory publication requires closed staging files; recheck under the final write/delete lock.
                var publishedHash = await SHA256.HashDataAsync(published).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(copy.Hash, publishedHash))
                    throw new IOException("The verified destination changed before relocation completed.");
            }
            var rejectedItems = plan.ItemPaths.Where(entry => entry.Key.RejectedFiles != null).ToArray();
            if (rejectedItems.Length > 0)
            {
                var sources = copies.ToDictionary(copy => copy.RelativePath, StringComparer.OrdinalIgnoreCase);
                foreach (var entry in rejectedItems)
                {
                    var rejected = entry.Key.RejectedFiles!;
                    void Rebind(string suffix, bool checkpoint)
                    {
                        var relative = Path.GetRelativePath(plan.Target, entry.Value + suffix);
                        if (publishedFiles.TryGetValue(relative, out var published))
                            rejected.RebindVerifiedCopy(sources[relative].Source.Stream.SafeFileHandle,
                                published.SafeFileHandle, checkpoint);
                    }
                    // Only verified copies of the original rejected objects inherit cleanup permission.
                    Rebind(rejected.Published ? "" : ".part", checkpoint: false);
                    if (!rejected.Published) Rebind(".part.segments", checkpoint: true);
                }
            }
            bool cleaned = true;
            foreach (var copy in copies) cleaned &= copy.Source.TryDelete();
            foreach (var copy in copies) copy.Source.Dispose();
            if (plan.CleanSourceDirectory && plan.Source != null)
            {
                var directories = GetPlannedDirectories(plan.Source, plan.Files.Select(file => file.Source), plan.Directories);
                foreach (string directory in directories.OrderByDescending(path => path.Length))
                    cleaned &= TryDeleteEmptyDirectory(directory);
            }
            return cleaned;
        }
        finally
        {
            foreach (var published in publishedFiles.Values) published.Dispose();
            foreach (var copy in copies) copy.Dispose();
            if (Directory.Exists(stage)) DeleteOwnedTree(stage);
        }
    }

    private static void PublishDirectory(string source, Plan plan, Action<string, string> moveDirectory)
    {
        // Nonrecursive deletion also rejects files added after the empty-target check.
        if (plan.UseExistingEmptyDestination)
        {
            EnsurePlainPath(plan.Target);
            Directory.Delete(plan.Target, recursive: false);
        }
        try { moveDirectory(source, plan.Target); }
        catch
        {
            if (plan.UseExistingEmptyDestination && !Path.Exists(plan.Target)) Directory.CreateDirectory(plan.Target);
            throw;
        }
    }

    private async Task<ProtectedCopy> StageFileAsync(FileEntry file, string destination, bool tryLink)
    {
        EnsurePlainPath(file.Source);
        var lease = RelocationFileAccess.OpenSource(file.Source);
        try
        {
            var lastWrite = File.GetLastWriteTimeUtc(lease.Stream.SafeFileHandle);
            if (RejectedDownloadFiles.ReadIdentity(lease.Stream.SafeFileHandle) != file.Identity)
                throw new IOException("The source file changed after relocation was planned.");
            if (tryLink && lease.CanDelete && (File.GetAttributes(file.Source) & FileAttributes.ReadOnly) == 0 &&
                RelocationFileAccess.TryCreateHardLink(file.Source, destination))
            {
                if (!RejectedDownloadFiles.MatchesOpenFile(destination, lease.Stream.SafeFileHandle))
                    throw new IOException("The source changed while its destination was linked.");
                return new ProtectedCopy(lease, null, file.RelativePath);
            }
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = await lease.Stream.ReadAsync(buffer).ConfigureAwait(false)) != 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await stream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                }
                await stream.FlushAsync().ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            await using var verified = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var copiedHash = await SHA256.HashDataAsync(verified).ConfigureAwait(false);
            var sourceHash = hash.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(sourceHash, copiedHash))
                throw new IOException("The copied download failed verification.");
            File.SetLastWriteTimeUtc(destination, lastWrite);
            return new ProtectedCopy(lease, sourceHash, file.RelativePath);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static HashSet<string> GetPlannedDirectories(string source, IEnumerable<string> files, IEnumerable<string> directories)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { source };
        foreach (string directory in directories.Select(relative => Path.Combine(source, relative))
            .Concat(files.Select(Path.GetDirectoryName).OfType<string>()))
        {
            for (string? path = directory; path != null && Within(path, source) && result.Add(path);
                path = Path.GetDirectoryName(path)) { }
        }
        return result;
    }

    private static bool ContainsOnlyPlannedEntries(string source, IEnumerable<string> paths, IEnumerable<string> relativeDirectories)
    {
        var files = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plannedDirectories = GetPlannedDirectories(source, files, relativeDirectories);
        foreach (string directory in plannedDirectories)
        {
            if (!Directory.Exists(directory)) continue;
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                if (!files.Contains(entry) && !plannedDirectories.Contains(entry)) return false;
        }
        return true;
    }

    internal static bool CanRenamePackageDirectory(DownloadPackage package, string directory)
    {
        if (package.RequiresRecoveredPathConfirmation || ViewModels.MainViewModel.IsProtectedDirectory(directory))
            return false;
        var packages = GetRenamePackages(package);
        if (packages.Any(other => other != package && ReferencesDirectory(other, directory, includeAncestors: false)))
            return false;
        var plan = PreparePackage(package, packages, directory, useExactDestination: true);
        if (Directory.Exists(directory) && plan.Files.Length == 0 && plan.Directories.Length == 0)
            return false;
        return plan.ItemPaths.Values.All(path => Within(path, directory)) &&
            ContainsOnlyPlannedEntries(plan.Source!, plan.Files.Select(file => file.Source), plan.Directories);
    }

    internal static DownloadPackage[] GetRenamePackages(DownloadPackage package) =>
        IncludeClippedPackages(QueueManager.IsInitialized ? QueueManager.Instance.Packages.ToArray().Append(package) : [package]);

    private static bool ReferencesDirectory(DownloadPackage package, string directory, bool includeAncestors = true) =>
        (!string.IsNullOrWhiteSpace(package.SaveDirectory) &&
            (Within(package.SaveDirectory, directory) || (includeAncestors && Within(directory, package.SaveDirectory)))) ||
        (!string.IsNullOrWhiteSpace(package.ExtractionDirectory) && Within(package.ExtractionDirectory, directory)) ||
        package.ExtractedFiles.Any(file => !string.IsNullOrWhiteSpace(file.Path) &&
            Path.IsPathFullyQualified(file.Path) && Within(file.Path, directory)) ||
        package.ItemsSnapshot.Any(item => ViewModels.MainViewModel.GetItemCandidatePaths(item, package.SaveDirectory)
            .Any(path => Within(path, directory)));

    private static void EnsurePlainPath(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked directories and files cannot be relocated.");
        }
    }

    private static void DeleteOwnedTree(string directory)
    {
        EnsurePlainPath(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            EnsurePlainPath(entry);
            if (Directory.Exists(entry)) DeleteOwnedTree(entry);
            else File.Delete(entry);
        }
        Directory.Delete(directory);
    }

    private static bool TryDeleteEmptyDirectory(string directory)
    {
        try
        {
            EnsurePlainPath(directory);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: false);
            return true;
        }
        catch (Exception) { return false; }
    }

    private static bool Same(string? first, string? second) => first != null && second != null &&
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);

    private static bool Within(string path, string directory)
    {
        var prefix = Path.GetFullPath(directory);
        if (!Path.EndsInDirectorySeparator(prefix)) prefix += Path.DirectorySeparatorChar;
        return Same(path, directory) || Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
