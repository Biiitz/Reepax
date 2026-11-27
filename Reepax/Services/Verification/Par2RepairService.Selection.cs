using System.IO;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Extractor;
using Reepax.ViewModels;

namespace Reepax.Services.Verification;

public partial class Par2RepairService
{
    private sealed record PackagePar2Selection(string Path, FileStream? VerifiedIndex = null) : IDisposable
    {
        public void Dispose() => VerifiedIndex?.Dispose();
    }

    private PackagePar2Selection? SelectPackagePar2(DownloadPackage package, CancellationToken token)
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

        var loose = FindArchivePar2(package, listed.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase), token);
        if (loose != null) return loose;

        // A listed file still advertises an expected repair step while its download is pending.
        return enabled.Length > 0 ? new(enabled[0]) : null;
    }

    private PackagePar2Selection? FindArchivePar2(DownloadPackage package, HashSet<string> listedPaths, CancellationToken token)
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
                    var targets = metadata.GetTargets(directory);
                    if (!targets.All(target => extractor.IsArchiveFile(target) && archives.Any(archive =>
                        string.Equals(Path.GetDirectoryName(archive), Path.GetDirectoryName(target), StringComparison.OrdinalIgnoreCase) &&
                        extractor.ArePartOfSameArchive(archive, target))) ||
                        ConfirmRepairTargets(package, directory, targets) == null)
                        continue;

                    // Pin the associated index until native verification and any repair have finished.
                    var result = new PackagePar2Selection(candidate, stream);
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
}
