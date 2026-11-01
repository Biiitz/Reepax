using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Reepax.Models;
using Reepax.Services.Extractor;

namespace Reepax.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    public async Task RetryArchiveCleanupAsync(DownloadPackage? package)
    {
        package ??= SelectedPackage;
        if (package == null || !Packages.Contains(package) || !package.IsExtracted ||
            !package.HasPendingArchiveCleanup || package.IsExtracting ||
            ArchiveExtractionService.Instance.IsPackageExtracting(package.Id)) return;
        await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package, force: true);
        StatusSummary = package.StatusMessage;
    }
}
