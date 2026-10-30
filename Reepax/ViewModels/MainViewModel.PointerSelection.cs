using System.Linq;
using Reepax.Models;

namespace Reepax.ViewModels;

public partial class MainViewModel
{
    private void ClearSelectedEntries(DownloadItem? keepItem = null, DownloadPackage? keepPackage = null)
    {
        foreach (var package in Packages)
        {
            package.IsSelected = ReferenceEquals(package, keepPackage);
            foreach (var item in package.Items)
                item.IsSelected = ReferenceEquals(item, keepItem);
        }
    }

    private int CountSelectedEntries() => Packages.Count(package => package.IsSelected) +
        Packages.Sum(package => package.Items.Count(item => item.IsSelected));

    internal void SelectItemFromPointer(DownloadItem item, bool controlPressed, bool wasSelected)
    {
        if (!Packages.Any(package => package.Items.Contains(item))) return;
        if (controlPressed)
        {
            // A selected row stays selected until a click finishes without dragging.
            if (wasSelected) return;
            item.IsSelected = true;
            SelectedItem = item;
        }
        else if (!wasSelected)
        {
            ClearSelectedEntries(keepItem: item);
            SelectedItem = item;
            SelectedPackage = null;
        }
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    internal void SelectPackageFromPointer(DownloadPackage package, bool controlPressed, bool wasSelected)
    {
        if (!Packages.Contains(package)) return;
        if (controlPressed)
        {
            if (wasSelected) return;
            package.IsSelected = true;
            SelectedPackage = package;
        }
        else if (!wasSelected)
        {
            ClearSelectedEntries(keepPackage: package);
            SelectedPackage = package;
            SelectedItem = null;
        }
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    internal void CompleteItemPointerSelection(DownloadItem item, bool controlPressed, bool wasSelected, bool wasDragged)
    {
        if (wasDragged || !wasSelected || !Packages.Any(package => package.Items.Contains(item))) return;
        if (controlPressed)
        {
            item.IsSelected = false;
            if (SelectedItem == item)
                SelectedItem = Packages.SelectMany(package => package.Items).FirstOrDefault(entry => entry.IsSelected);
            DeleteSelectedCommand.NotifyCanExecuteChanged();
            return;
        }
        bool keepItem = CountSelectedEntries() > 1;
        ClearSelectedEntries(keepItem: keepItem ? item : null);
        SelectedItem = keepItem ? item : null;
        SelectedPackage = null;
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    internal void CompletePackagePointerSelection(DownloadPackage package, bool controlPressed, bool wasSelected, bool wasDragged)
    {
        if (wasDragged || !wasSelected || !Packages.Contains(package)) return;
        if (controlPressed)
        {
            package.IsSelected = false;
            if (SelectedPackage == package)
                SelectedPackage = Packages.FirstOrDefault(entry => entry.IsSelected);
            DeleteSelectedCommand.NotifyCanExecuteChanged();
            return;
        }
        bool keepPackage = CountSelectedEntries() > 1;
        ClearSelectedEntries(keepPackage: keepPackage ? package : null);
        SelectedPackage = keepPackage ? package : null;
        SelectedItem = null;
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }
}
