using System.IO;
using System.Linq;

namespace Reepax.Models;

public sealed record ExtractedPackageFile(string Path, long Length, long LastWriteTicks)
{
    internal static ExtractedPackageFile Capture(string filePath, string? destination = null)
    {
        var info = new FileInfo(filePath);
        return new(destination ?? info.FullName, info.Length, info.LastWriteTimeUtc.Ticks);
    }
}

public partial class DownloadPackage
{
    internal ExtractedPackageFile[] ExtractedFiles { get; set; } = [];

    internal void RecordExtractedFiles(ExtractedPackageFile[] files)
    {
        if (files.Length == 0) return;
        ExtractedFiles = files.Concat(ExtractedFiles)
            .DistinctBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal void RelocateExtractionPaths(string source, string destination)
    {
        string Relocate(string path)
        {
            if (!Path.IsPathFullyQualified(path)) return path;
            var relative = Path.GetRelativePath(source, path);
            return Path.IsPathRooted(relative) || relative == ".." ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                ? path : Path.GetFullPath(Path.Combine(destination, relative));
        }

        if (!string.IsNullOrWhiteSpace(ExtractionDirectory))
            ExtractionDirectory = Relocate(ExtractionDirectory);
        ExtractedFiles = ExtractedFiles.Select(file => file with { Path = Relocate(file.Path) }).ToArray();
    }

    internal DownloadPackage CreateDiskCleanupSnapshot()
    {
        var snapshot = new DownloadPackage(false)
        {
            Id = Id,
            Name = Name,
            SaveDirectory = SaveDirectory,
            RequiresRecoveredPathConfirmation = RequiresRecoveredPathConfirmation,
            ExtractionDirectory = ExtractionDirectory,
            ExtractedFiles = ExtractedFiles,
            ParentPackageId = ParentPackageId,
            IsExtracting = Services.Extractor.ArchiveExtractionService.Instance.IsPackageExtracting(Id)
        };
        foreach (var item in Items)
        {
            snapshot.Items.Add(new DownloadItem
            {
                Id = item.Id,
                FileName = item.FileName,
                SaveFilePath = item.SaveFilePath
            });
        }
        foreach (var child in ClippedPackages)
            snapshot.ClippedPackages.Add(new DownloadPackage(false) { Id = child.Id });
        snapshot._itemsSnapshot = snapshot.Items.ToArray();
        return snapshot;
    }
}
