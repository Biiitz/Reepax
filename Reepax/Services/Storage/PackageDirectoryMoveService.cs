using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Extractor;

namespace Reepax.Services.Storage;

internal sealed class PackageDirectoryMoveService
{
    private readonly bool _allowDirectoryRename;
    private readonly Action<string>? _fileCopied;

    internal PackageDirectoryMoveService(bool allowDirectoryRename = true, Action<string>? fileCopied = null)
    {
        _allowDirectoryRename = allowDirectoryRename;
        _fileCopied = fileCopied;
    }

    internal sealed record FileEntry(string Source, string RelativePath);
    internal sealed record Plan(DownloadPackage Package, string? Source, string Target, bool WholeDirectory,
        Dictionary<DownloadItem, string> ItemPaths, string? ExtractionDirectory, FileEntry[] Files, string[] Directories);

    private sealed record ProtectedCopy(RelocationFileAccess.SourceLease Source, byte[] Hash, string RelativePath) : IDisposable
    {
        public void Dispose() => Source.Dispose();
    }

    internal static Plan[] Prepare(DownloadPackage[] packages, DownloadPackage[] allPackages, string destination)
    {
        var root = Path.GetFullPath(destination);
        EnsurePlainPath(root);
        var plans = packages.Select(package => PreparePackage(package, allPackages, root)).ToArray();
        if (plans.Select(plan => plan.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plans.Length)
            throw new IOException("Package destinations overlap.");
        foreach (var plan in plans)
        {
            foreach (var other in plans)
            {
                if (other.WholeDirectory && other.Files.Length > 0 && other.Source != null && !Same(plan.Target, plan.Source) &&
                    (Within(plan.Target, other.Source) || Within(other.Source, plan.Target)))
                    throw new IOException("Source and destination directories overlap.");
            }
        }
        return plans;
    }

    private static Plan PreparePackage(DownloadPackage package, DownloadPackage[] allPackages, string root)
    {
        if (package.RequiresRecoveredPathConfirmation)
            throw new IOException("The recovered source directory requires confirmation.");
        var name = PackageGrouper.MakeSafeDirectoryName(package.Name);
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || Path.GetFileName(name) != name)
            throw new IOException("Invalid package directory name.");
        var target = Path.GetFullPath(Path.Combine(root, name));
        var source = string.IsNullOrWhiteSpace(package.SaveDirectory) ? null : Path.GetFullPath(package.SaveDirectory);
        EnsurePlainPath(target);
        if (source != null) EnsurePlainPath(source);
        if (!Same(source, target) && (Directory.Exists(target) || File.Exists(target)))
            throw new IOException("The destination already exists.");
        var itemPaths = new Dictionary<DownloadItem, string>();
        var originals = new Dictionary<DownloadItem, string>();
        foreach (var item in package.ItemsSnapshot)
        {
            if (string.IsNullOrWhiteSpace(item.FileName) || Path.GetFileName(item.FileName) != item.FileName ||
                item.FileName is "." or ".." || item.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new IOException("Invalid download file name.");
            var original = !string.IsNullOrWhiteSpace(item.SaveFilePath) ? Path.GetFullPath(item.SaveFilePath)
                : source != null ? Path.Combine(source, item.FileName) : null;
            if (original != null) originals.Add(item, original);
            itemPaths.Add(item, Path.Combine(target, item.FileName));
        }
        var whole = source != null && Directory.Exists(source) &&
            ViewModels.MainViewModel.IsDedicatedPackageDirectory(package, source, allPackages) &&
            originals.Values.All(path => Within(path, source)) &&
            !allPackages.Where(other => other != package).Any(other =>
                ReferencesDirectory(other, source));
        var files = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        var directories = new List<string>();
        if (whole)
        {
            AddDirectoryFiles(source!, source!, files, directories);
            foreach (var entry in originals)
                itemPaths[entry.Key] = Path.Combine(target, Path.GetRelativePath(source!, entry.Value));
        }
        else
        {
            foreach (var entry in originals)
            {
                foreach (var suffix in new[] { "", ".part", ".part.segments" })
                {
                    var path = entry.Value + suffix;
                    if (!File.Exists(path)) continue;
                    EnsurePlainPath(path);
                    if (allPackages.Where(other => other != package).Any(other => other.ItemsSnapshot.Any(item =>
                        Same(!string.IsNullOrWhiteSpace(item.SaveFilePath) ? item.SaveFilePath :
                            !string.IsNullOrWhiteSpace(other.SaveDirectory) ? Path.Combine(other.SaveDirectory, item.FileName) : null,
                            entry.Value))))
                        throw new IOException("A source file is shared by another package.");
                    files.TryAdd(path, new FileEntry(path, entry.Key.FileName + suffix));
                }
            }
        }
        if (files.Values.Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
            throw new IOException("Download file names overlap.");
        if (itemPaths.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != itemPaths.Count)
            throw new IOException("Download item destinations overlap.");
        if (Same(source, target))
            foreach (var entry in originals) itemPaths[entry.Key] = entry.Value;
        var extraction = package.ExtractionDirectory;
        if (whole && !string.IsNullOrWhiteSpace(extraction) && Within(extraction, source!))
            extraction = Path.Combine(target, Path.GetRelativePath(source!, extraction));
        return new Plan(package, source, target, whole, itemPaths, extraction, files.Values.ToArray(), directories.ToArray());
    }

    internal async Task<bool> ExecuteAsync(Plan plan)
    {
        if (Same(plan.Source, plan.Target) || plan.Files.Length == 0) return true;
        EnsurePlainPath(Path.GetDirectoryName(plan.Target)!);
        Directory.CreateDirectory(Path.GetDirectoryName(plan.Target)!);
        if (_allowDirectoryRename && plan.WholeDirectory && Same(Path.GetPathRoot(plan.Source!), Path.GetPathRoot(plan.Target)))
        {
            Directory.Move(plan.Source!, plan.Target);
            return true;
        }
        var stage = Path.Combine(Path.GetDirectoryName(plan.Target)!, ".download-move-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var copies = new List<ProtectedCopy>();
        var publishedFiles = new List<FileStream>();
        try
        {
            foreach (var directory in plan.Directories) Directory.CreateDirectory(Path.Combine(stage, directory));
            foreach (var file in plan.Files)
            {
                var output = Path.Combine(stage, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                copies.Add(await CopyVerifiedAsync(file.Source, output, file.RelativePath).ConfigureAwait(false));
                _fileCopied?.Invoke(file.Source);
            }
            Directory.Move(stage, plan.Target);
            foreach (var copy in copies)
            {
                var published = new FileStream(Path.Combine(plan.Target, copy.RelativePath), FileMode.Open,
                    FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
                publishedFiles.Add(published);
                // Directory publication requires closed staging files; recheck under the final write/delete lock.
                var publishedHash = await SHA256.HashDataAsync(published).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(copy.Hash, publishedHash))
                    throw new IOException("The verified destination changed before relocation completed.");
            }
            var rejectedItems = plan.ItemPaths.Where(entry => entry.Key.RejectedFiles != null).ToArray();
            if (rejectedItems.Length > 0)
            {
                var indices = copies.Select((copy, index) => (copy.RelativePath, index))
                    .ToDictionary(entry => entry.RelativePath, entry => entry.index, StringComparer.OrdinalIgnoreCase);
                foreach (var entry in rejectedItems)
                {
                    var rejected = entry.Key.RejectedFiles!;
                    void Rebind(string suffix, bool checkpoint)
                    {
                        var relative = Path.GetRelativePath(plan.Target, entry.Value + suffix);
                        if (indices.TryGetValue(relative, out int index))
                            rejected.RebindVerifiedCopy(copies[index].Source.Stream.SafeFileHandle,
                                publishedFiles[index].SafeFileHandle, checkpoint);
                    }
                    // Only verified copies of the original rejected objects inherit cleanup permission.
                    Rebind(rejected.Published ? "" : ".part", checkpoint: false);
                    if (!rejected.Published) Rebind(".part.segments", checkpoint: true);
                }
            }
            bool cleaned = true;
            foreach (var copy in copies) cleaned &= copy.Source.TryDelete();
            foreach (var copy in copies) copy.Source.Dispose();
            if (plan.WholeDirectory && plan.Source != null)
                cleaned &= DeleteEmptyDirectories(plan.Source);
            return cleaned;
        }
        finally
        {
            foreach (var published in publishedFiles) published.Dispose();
            foreach (var copy in copies) copy.Dispose();
            if (Directory.Exists(stage)) DeleteOwnedTree(stage);
        }
    }

    private static async Task<ProtectedCopy> CopyVerifiedAsync(string source, string destination, string relativePath)
    {
        EnsurePlainPath(source);
        var lease = RelocationFileAccess.OpenSource(source);
        try
        {
            var lastWrite = File.GetLastWriteTimeUtc(source);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = await lease.Stream.ReadAsync(buffer).ConfigureAwait(false)) != 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                }
                await output.FlushAsync().ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            await using var verified = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var copiedHash = await SHA256.HashDataAsync(verified).ConfigureAwait(false);
            var sourceHash = hash.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(sourceHash, copiedHash))
                throw new IOException("The copied download failed verification.");
            File.SetLastWriteTimeUtc(destination, lastWrite);
            return new ProtectedCopy(lease, sourceHash, relativePath);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static void AddDirectoryFiles(string directory, string root, Dictionary<string, FileEntry> files, List<string> directories)
    {
        EnsurePlainPath(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            EnsurePlainPath(entry);
            if (Directory.Exists(entry))
            {
                directories.Add(Path.GetRelativePath(root, entry));
                AddDirectoryFiles(entry, root, files, directories);
            }
            else files.Add(entry, new FileEntry(entry, Path.GetRelativePath(root, entry)));
        }
    }

    private static bool ReferencesDirectory(DownloadPackage package, string directory) =>
        (!string.IsNullOrWhiteSpace(package.SaveDirectory) &&
            (Within(package.SaveDirectory, directory) || Within(directory, package.SaveDirectory))) ||
        (!string.IsNullOrWhiteSpace(package.ExtractionDirectory) && Within(package.ExtractionDirectory, directory)) ||
        package.ItemsSnapshot.Any(item => !string.IsNullOrWhiteSpace(item.SaveFilePath) && Within(item.SaveFilePath, directory));

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

    private static bool DeleteEmptyDirectories(string directory)
    {
        try
        {
            EnsurePlainPath(directory);
            foreach (var child in Directory.EnumerateDirectories(directory)) DeleteEmptyDirectories(child);
            Directory.Delete(directory, recursive: false);
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
