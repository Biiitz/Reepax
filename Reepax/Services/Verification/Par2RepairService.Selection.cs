using System.IO;
using System.Security.Cryptography;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Extractor;
using Reepax.Services.Storage;
using Reepax.ViewModels;

namespace Reepax.Services.Verification;

public partial class Par2RepairService
{
    private sealed record PackagePar2Selection(string Path, FileStream? VerifiedIndex = null,
        IReadOnlyDictionary<string, (ulong Length, byte[] Md5)>? LooseTargets = null) : IDisposable
    {
        public void Dispose() => VerifiedIndex?.Dispose();
    }

    private PackagePar2Selection? SelectPackagePar2(DownloadPackage package, CancellationToken token,
        IReadOnlyList<IReadOnlyDictionary<string, (ulong Length, byte[] Md5)>>? staleLooseSets = null)
    {
        var listed = package.ItemsSnapshot
            .Where(item => IsPar2File(item.FileName) || IsPar2File(item.SaveFilePath))
            .Select(item => (Path: GetConfirmedPath(item, package.SaveDirectory) ?? item.FileName, item.IsEnabled))
            .ToArray();
        var enabled = listed.Where(item => item.IsEnabled)
            .OrderBy(item => _volPar2Pattern.IsMatch(item.Path))
            .Select(item => item.Path)
            .ToArray();
        string? existing = enabled.FirstOrDefault(path => IsPar2File(path) && File.Exists(path) && Path.IsPathFullyQualified(path));
        if (existing != null) return new(existing);

        var listedPaths = listed.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var loose = FindArchivePar2(package, listedPaths, staleLooseSets, token);
        if (loose != null) return loose;

        // A listed file still advertises an expected repair step while its download is pending.
        return enabled.Length > 0 ? new(enabled[0]) : null;
    }

    private PackagePar2Selection? FindArchivePar2(DownloadPackage package, HashSet<string> listedPaths,
        IReadOnlyList<IReadOnlyDictionary<string, (ulong Length, byte[] Md5)>>? staleLooseSets, CancellationToken token)
    {
        if (package.RequiresRecoveredPathConfirmation || string.IsNullOrWhiteSpace(package.SaveDirectory))
            return null;
        var extractor = ArchiveExtractionService.Instance;
        try
        {
            string directory = Path.GetFullPath(package.SaveDirectory);
            if (!Directory.Exists(directory)) return null;
            if (HasReparsePoint(directory)) return null;
            var candidates = Directory.GetFiles(directory, "*.par2", SearchOption.TopDirectoryOnly);
            if (candidates.Length == 0) return null;
            var packages = QueueManager.IsInitialized ? QueueManager.Instance.Packages.ToArray() : [];
            if (!MainViewModel.IsCleanupDirectory(package, directory, packages, protectOtherPaths: true))
                return null;
            var archives = extractor.GetPrimaryArchives(package).Select(Path.GetFullPath).ToArray();
            if (archives.Length == 0) return null;

            foreach (string candidate in candidates.OrderBy(path => _volPar2Pattern.IsMatch(path))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                if (listedPaths.Contains(candidate)) continue;
                FileStream? stream = null;
                try
                {
                    if (HasReparsePoint(candidate)) continue;
                    stream = File.OpenRead(candidate);
                    var metadata = new RepairMetadata();
                    metadata.Read(stream, token, requireValidPackets: true);
                    if (!metadata.IsComplete) continue;
                    var targetFiles = metadata.GetTargetFiles(directory);
                    if (staleLooseSets?.Any(stale => DescribesSameFiles(stale, targetFiles)) == true) continue;
                    var targets = targetFiles.Keys;
                    if (!targets.All(target => extractor.IsArchiveFile(target) && archives.Any(archive =>
                        string.Equals(Path.GetDirectoryName(archive), Path.GetDirectoryName(target), StringComparison.OrdinalIgnoreCase) &&
                        extractor.ArePartOfSameArchive(archive, target))) ||
                        ConfirmRepairTargets(package, directory, targets) == null)
                        continue;

                    // Pin the associated index until native verification and any repair have finished.
                    var result = new PackagePar2Selection(candidate, stream, targetFiles);
                    stream = null;
                    return result;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
                {
                    // A loose file that cannot establish its targets does not gate extraction.
                }
                finally
                {
                    stream?.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Directory discovery is optional when no PAR2 file is registered.
        }
        return null;
    }

    private static bool DescribesSameFiles(IReadOnlyDictionary<string, (ulong Length, byte[] Md5)> first,
        IReadOnlyDictionary<string, (ulong Length, byte[] Md5)> second) =>
        first.Count == second.Count && first.All(file => second.TryGetValue(file.Key, out var other) &&
            file.Value.Length == other.Length && file.Value.Md5.AsSpan().SequenceEqual(other.Md5));

    private static async Task<bool> IsStaleLooseSetAsync(DownloadPackage package, string directory,
        IReadOnlyDictionary<string, (ulong Length, byte[] Md5)> targets, CancellationToken token)
    {
        (string Path, string Checksum)[] expectedChecksums;
        using (await PackagePathOperationGate.Instance.EnterAsync(token))
        {
            var affected = await ConfirmRepairTargetsAsync(package, directory, targets.Keys.ToArray(), token);
            if (affected == null) return false;
            expectedChecksums = GetExpectedChecksums(affected);
        }

        // Only skip the set if none of its targets with a supplied checksum need repair.
        bool conflicts = false;
        foreach (var (path, checksum) in expectedChecksums)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (!await ChecksumVerificationService.Instance.MatchesExpectedChecksumAsync(path, checksum, token))
                    return false;
                if (!conflicts)
                {
                    var target = targets[path];
                    VerificationFileIo.LowerPriority(file);
                    conflicts = (ulong)file.Length != target.Length ||
                        !(await MD5.HashDataAsync(file, token)).AsSpan().SequenceEqual(target.Md5);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
        return conflicts;
    }
}
