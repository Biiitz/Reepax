using Reepax.Models;
using Reepax.Services.Extractor;
using Reepax.Services.Localization;

namespace Reepax.ViewModels;

public partial class MainViewModel
{
    private PackageDoubleClickAction _selectedPackageDoubleClickAction;
    internal event Action<DownloadPackage, PackageDoubleClickAction>? PackageDoubleClickRequested;

    public PackageDoubleClickAction SelectedPackageDoubleClickAction
    {
        get => _selectedPackageDoubleClickAction;
        set
        {
            value = NormalizePackageDoubleClickAction(value);
            if (!SetProperty(ref _selectedPackageDoubleClickAction, value)) return;
            OnPropertyChanged(nameof(SelectedPackageDoubleClickActionDisplayName));
            _settingsService.Settings.PackageDoubleClickAction = value;
            _settingsService.RequestSaveSettings();
        }
    }

    public string SelectedPackageDoubleClickActionDisplayName => Loc.Get(SelectedPackageDoubleClickAction switch
    {
        PackageDoubleClickAction.ChangeDownloadPath => "Menu_ChangeDownloadPath",
        PackageDoubleClickAction.CreateInstallFolder => "Menu_CreateGameInstallFolder",
        PackageDoubleClickAction.EditPackage => "Menu_EditPackage",
        PackageDoubleClickAction.AddLinks => "Menu_AddLinksToPackage",
        PackageDoubleClickAction.ExportPackage => "Menu_ExportPackage",
        PackageDoubleClickAction.TogglePause => "Settings_PackageDoubleClick_TogglePause",
        _ => "Settings_PackageDoubleClick_Off"
    });

    private static PackageDoubleClickAction NormalizePackageDoubleClickAction(PackageDoubleClickAction action) =>
        Enum.IsDefined(action) ? action : PackageDoubleClickAction.None;

    internal bool CanRunPackageDoubleClick(DownloadPackage package, PackageDoubleClickAction action)
    {
        if (IsBusy || !Packages.Contains(package) || package.IsEditing ||
            package.ItemsSnapshot.Any(item => item.IsUnavailableForProcessing)) return false;
        return action switch
        {
            PackageDoubleClickAction.ChangeDownloadPath => CanChangeDownloadPaths(new[] { package }),
            PackageDoubleClickAction.EditPackage => package.CanEditPackage,
            PackageDoubleClickAction.ExportPackage => package.Status != DownloadStatus.Completed,
            PackageDoubleClickAction.TogglePause => ArchiveExtractionService.Instance.IsPackageExtracting(package.Id) ||
                (package.Status != DownloadStatus.Completed && !package.CheckIsFullyCompleted()),
            PackageDoubleClickAction.CreateInstallFolder or PackageDoubleClickAction.AddLinks => true,
            _ => false
        };
    }

    internal bool TryRequestPackageDoubleClick(DownloadPackage package)
    {
        var action = SelectedPackageDoubleClickAction;
        if (!CanRunPackageDoubleClick(package, action) || PackageDoubleClickRequested == null) return false;
        PackageDoubleClickRequested.Invoke(package, action);
        return true;
    }
}
