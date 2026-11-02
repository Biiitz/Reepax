using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Reepax.Models;

namespace Reepax.Views;

public partial class SettingsView
{
    private long _packageActionClosedTimestamp;

    private void CloseSettingsDropdowns()
    {
        if (CompletionSoundPopup != null) CompletionSoundPopup.IsOpen = false;
        if (ErrorSoundPopup != null) ErrorSoundPopup.IsOpen = false;
        if (PackageDoubleClickPopup != null) PackageDoubleClickPopup.IsOpen = false;
    }

    private void PackageDoubleClickPopup_Closed(object? sender, EventArgs args) =>
        _packageActionClosedTimestamp = Environment.TickCount64;

    private void PackageDoubleClickSelector_PreviewMouseDown(object sender, MouseButtonEventArgs args)
    {
        if (!PackageDoubleClickPopup.IsOpen && Environment.TickCount64 - _packageActionClosedTimestamp >= 350) return;
        PackageDoubleClickPopup.IsOpen = false;
        _packageActionClosedTimestamp = Environment.TickCount64;
        args.Handled = true;
    }

    private void PackageDoubleClickSelector_Click(object sender, RoutedEventArgs args)
    {
        if (Environment.TickCount64 - _packageActionClosedTimestamp < 350) return;
        PackageDoubleClickPopup.IsOpen = !PackageDoubleClickPopup.IsOpen;
    }

    private void PackageDoubleClickOption_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { CommandParameter: PackageDoubleClickAction action } && ViewModel != null)
            ViewModel.SelectedPackageDoubleClickAction = action;
        PackageDoubleClickPopup.IsOpen = false;
        PackageDoubleClickSelector.Focus();
    }

    private void CyclePackageDoubleClickAction(bool forward)
    {
        if (ViewModel == null) return;
        var actions = Enum.GetValues<PackageDoubleClickAction>();
        int current = Array.IndexOf(actions, ViewModel.SelectedPackageDoubleClickAction);
        ViewModel.SelectedPackageDoubleClickAction = actions[(current + (forward ? 1 : -1) + actions.Length) % actions.Length];
    }

    private void PackageDoubleClickSelector_PreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key is Key.Down or Key.Up)
            CyclePackageDoubleClickAction(args.Key == Key.Down);
        else if (args.Key == Key.Escape && PackageDoubleClickPopup.IsOpen)
        {
            PackageDoubleClickPopup.IsOpen = false;
            PackageDoubleClickSelector.Focus();
        }
        else return;
        args.Handled = true;
    }

    private void PackageDoubleClickSelector_PreviewMouseWheel(object sender, MouseWheelEventArgs args)
    {
        if (args.Delta == 0) return;
        CyclePackageDoubleClickAction(args.Delta < 0);
        args.Handled = true;
    }
}
