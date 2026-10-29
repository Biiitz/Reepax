using System.Linq;

namespace Reepax.Models;

public partial class DownloadPackage
{
    internal DownloadPackage CreateDiskCleanupSnapshot()
    {
        var snapshot = new DownloadPackage(false)
        {
            Id = Id,
            Name = Name,
            SaveDirectory = SaveDirectory,
            ExtractionDirectory = ExtractionDirectory,
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
