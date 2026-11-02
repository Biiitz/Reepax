using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Reepax.Models;
using Reepax.Services.Localization;
using Reepax.Services.Storage;

namespace Reepax;

public partial class MainWindow
{
    private bool _isHandlingPackageDoubleClick;

    private void InitializePackageDoubleClick()
    {
        var model = ViewModel;
        model.PackageDoubleClickRequested += HandlePackageDoubleClick;
        Closed += (_, _) => model.PackageDoubleClickRequested -= HandlePackageDoubleClick;
    }

    internal static bool IsPackageDoubleClickGesture(int clickCount, ModifierKeys modifiers, bool editing) =>
        clickCount == 2 && modifiers == ModifierKeys.None && !editing;

    private bool TryHandlePackageDoubleClick(DownloadPackage package, MouseButtonEventArgs args)
    {
        if (_isHandlingPackageDoubleClick || !IsPackageDoubleClickGesture(args.ClickCount, Keyboard.Modifiers, package.IsEditing)) return false;
        if (!ViewModel.TryRequestPackageDoubleClick(package)) return false;
        args.Handled = true;
        _pressedPackage = null;
        _draggedPackage = null;
        _draggedPackages = null;
        _isDraggingPackage = false;
        _hasPackageDragged = false;
        ClearAllDropTargets();
        return true;
    }

    private async void HandlePackageDoubleClick(DownloadPackage package, PackageDoubleClickAction action)
    {
        if (_isHandlingPackageDoubleClick) return;
        _isHandlingPackageDoubleClick = true;
        try
        {
            // Finish the pointer event before opening a dialog or changing package state.
            await Dispatcher.Yield(DispatcherPriority.Input);
            if (!IsLoaded || !ViewModel.CanRunPackageDoubleClick(package, action)) return;
            switch (action)
            {
                case PackageDoubleClickAction.ChangeDownloadPath:
                    await ShowChangeDownloadPathDialogAsync(new[] { package });
                    break;
                case PackageDoubleClickAction.CreateInstallFolder:
                    ShowCreateInstallFolderDialog(package);
                    break;
                case PackageDoubleClickAction.EditPackage:
                    ShowEditPackageDialog(package);
                    break;
                case PackageDoubleClickAction.AddLinks:
                    ShowAddLinksDialog(targetPackage: package);
                    break;
                case PackageDoubleClickAction.ExportPackage:
                    PackageExportImportService.ExportWithDialog(package, this);
                    break;
                case PackageDoubleClickAction.TogglePause:
                    ViewModel.TogglePackagePause(package);
                    break;
            }
        }
        catch (Exception exception)
        {
            AppLogger.Error("[PackageAction] Could not complete the double-click action.", exception);
            ViewModel.StatusSummary = Loc.Get("Status_PackageDoubleClickFailed");
        }
        finally { _isHandlingPackageDoubleClick = false; }
    }
}
