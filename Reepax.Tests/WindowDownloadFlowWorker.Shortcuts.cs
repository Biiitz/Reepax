using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.ViewModels;
using Reepax.Views;
using Xunit;

namespace Reepax.Tests;

internal static partial class WindowDownloadFlowWorker
{
    private static async Task CheckWindowShortcutsAsync(MainWindow window, MainViewModel model, string root)
    {
        await CheckFilteredDeletionAsync(window, model, root);
        string directory = Directory.CreateDirectory(Path.Combine(root, "example")).FullName;
        byte[] contents = [2, 4, 6, 8];
        var package = new DownloadPackage { Name = "Example package", SaveDirectory = directory, IsCustomName = true };
        foreach (string name in new[] { "first.bin", "second.bin" })
        {
            string path = Path.Combine(directory, name);
            File.WriteAllBytes(path, contents);
            package.Items.Add(new DownloadItem { FileName = name, SaveFilePath = path, Status = DownloadStatus.Paused });
        }
        model.Packages.Add(package);
        var first = package.Items[0];
        var second = package.Items[1];
        model.SelectItemFromPointer(first, controlPressed: false, wasSelected: false);
        model.SelectItemFromPointer(second, controlPressed: true, wasSelected: false);

        var list = Assert.IsAssignableFrom<UIElement>(window.FindName("DownloadRowsList"));
        var search = Assert.IsType<TextBox>(window.FindName("BottomSearchFilterBox"));
        var rename = model.Shortcuts.Single(shortcut => shortcut.Id == "RenameSelected");
        var delete = model.Shortcuts.Single(shortcut => shortcut.Id == "DeleteSelected");
        int confirmations = 0;
        ConfirmDialog.ShowDialogOverrideForTesting = () =>
        {
            confirmations++;
            return false;
        };
        try
        {
            await IdleAsync();
            Assert.True(Press(list, Key.F2));
            Assert.True(second.IsEditing);
            second.IsEditing = false;
            await IdleAsync();
            Assert.True(Press(list, Key.Delete));
            await model.WaitForRemovalAsync();
            Assert.Equal(1, confirmations);
            Assert.Equal(2, package.Items.Count);

            model.SelectedMainTab = AppMainTab.Settings;
            Assert.False(Press(window, Key.F2));
            Assert.False(Press(window, Key.Delete));
            model.StartRecordingShortcut(rename);
            Assert.True(Press(window, Key.F6));
            model.StartRecordingShortcut(delete);
            Assert.True(Press(window, Key.F7));
            Assert.True(Press(window, Key.Back));
            Assert.Equal(AppMainTab.Downloads, model.SelectedMainTab);

            Assert.False(Press(list, Key.F2));
            Assert.False(Press(list, Key.Delete));
            Assert.False(Press(list, Key.Back));
            Assert.False(second.IsEditing);
            Assert.Equal(1, confirmations);

            await IdleAsync();
            Assert.True(search.IsVisible);
            Assert.False(Press(search, Key.F6));
            Assert.False(Press(search, Key.F7));
            Assert.False(Press(search, Key.Back));
            Assert.False(second.IsEditing);
            Assert.Equal(1, confirmations);

            Assert.True(Press(list, Key.F6));
            Assert.True(second.IsEditing);
            second.IsEditing = false;
            await IdleAsync();
            Assert.True(Press(list, Key.F7));
            await model.WaitForRemovalAsync();
            Assert.Equal(2, confirmations);

            string path = Path.Combine(root, "shortcuts.json");
            var storage = new SettingsService(path);
            storage.Settings.CustomShortcuts = new Dictionary<string, string>(model.ShortcutManager.ExportCustomShortcuts());
            storage.SaveSettings();
            var reopened = new SettingsService(path);
            model.ShortcutManager.LoadCustomShortcuts(reopened.Settings.CustomShortcuts);
            Assert.False(Press(list, Key.F2));
            Assert.False(Press(list, Key.Delete));
            Assert.True(Press(list, Key.F6));
            Assert.True(second.IsEditing);
            second.IsEditing = false;
            await IdleAsync();

            model.StartRecordingShortcut(model.Shortcuts.Single(shortcut => shortcut.Id == "ToggleExpandCollapse"));
            Assert.True(Press(list, Key.F6));
            model.StartRecordingShortcut(model.Shortcuts.Single(shortcut => shortcut.Id == "ClearCompleted"));
            Assert.True(Press(list, Key.F7));
            storage.Settings.CustomShortcuts = new Dictionary<string, string>(model.ShortcutManager.ExportCustomShortcuts());
            storage.SaveSettings();
            reopened = new SettingsService(path);
            model.ShortcutManager.LoadCustomShortcuts(reopened.Settings.CustomShortcuts);
            Assert.Equal(Key.None, rename.Key);
            Assert.Equal(Key.None, delete.Key);
            Assert.False(Press(list, Key.F2));
            Assert.False(Press(list, Key.Delete));
            Assert.False(Press(list, Key.Back));
            Assert.False(second.IsEditing);
            Assert.Equal(2, confirmations);

            var collapse = model.Shortcuts.Single(shortcut => shortcut.Id == "ToggleExpandCollapse");
            model.StartRecordingShortcut(collapse);
            Assert.True(Press(list, Key.F2));
            Assert.Same(collapse, model.ShortcutManager.FindMatch(Key.F2, ModifierKeys.None));
            model.ResetShortcutCommand.Execute(rename);
            Assert.Equal(Key.None, collapse.Key);
            Assert.Equal(Key.None, delete.Key);
            Assert.True(Press(list, Key.F2));
            Assert.True(second.IsEditing);
            second.IsEditing = false;
            await IdleAsync();

            var menu = Assert.IsType<ContextMenu>(window.FindResource("ItemContextMenu"));
            var renameItem = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, Loc.Get("Menu_Rename")));
            renameItem.DataContext = second;
            renameItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(second.IsEditing);
            second.IsEditing = false;
            await IdleAsync();

            var toolbar = Descendants<Button>(window).Single(button => ReferenceEquals(button.Command, model.DeleteSelectedCommand));
            Invoke(toolbar);
            await UntilAsync(() => confirmations == 3);
            await model.WaitForRemovalAsync();

            Assert.False(model.CanGoBack);
            model.StartRecordingShortcut(delete);
            Assert.True(Press(list, Key.Back));
            Assert.True(Press(list, Key.Back));
            await model.WaitForRemovalAsync();
            Assert.Equal(4, confirmations);
            Assert.Equal(new[] { first, second }, package.Items);
            Assert.All(package.Items, item =>
            {
                Assert.True(item.IsSelected);
                Assert.Equal(contents, File.ReadAllBytes(item.SaveFilePath!));
            });

            string part = second.SaveFilePath + ".part";
            File.WriteAllBytes(part, contents);
            second.Status = DownloadStatus.Failed;
            second.ExpectedChecksum = new string('a', 32);
            second.RejectedFiles = RejectedDownloadFiles.Capture(second.SaveFilePath, published: false);
            second.SetDownloadFailure(new DownloadFailure { Kind = DownloadFailureKind.ChecksumMismatch });
            var retry = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, Loc.Get("Menu_RetryWithoutLegacyMd5")));
            menu.PlacementTarget = new Border { DataContext = second };
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            Assert.Equal(Visibility.Collapsed, retry.Visibility);

            second.ExpectedChecksumHasKnownSource = false;
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            Assert.Equal(Visibility.Visible, retry.Visibility);
            retry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(5, confirmations);
            Assert.Equal(new string('a', 32), second.ExpectedChecksum);
            Assert.Equal(DownloadStatus.Failed, second.Status);
            Assert.Equal(contents, File.ReadAllBytes(part));
            Assert.Equal(contents, File.ReadAllBytes(second.SaveFilePath!));
        }
        finally
        {
            ConfirmDialog.ShowDialogOverrideForTesting = null;
        }
    }

    private static async Task CheckFilteredDeletionAsync(MainWindow window, MainViewModel model, string root)
    {
        string path = Path.Combine(root, "hidden.bin");
        byte[] contents = [3, 7, 11];
        File.WriteAllBytes(path, contents);
        var package = new DownloadPackage { Name = "Hidden package", SaveDirectory = root };
        package.Items.Add(new DownloadItem { FileName = "hidden.bin", SaveFilePath = path, Status = DownloadStatus.Paused });
        model.Packages.Add(package);
        string shownPath = Path.Combine(root, "shown.bin");
        File.WriteAllBytes(shownPath, contents);
        var shown = new DownloadPackage { Name = "Shown package", SaveDirectory = root };
        var shownFile = new DownloadItem { FileName = "shown.bin", SaveFilePath = shownPath, Status = DownloadStatus.Paused };
        shown.Items.Add(shownFile);
        var list = Assert.IsAssignableFrom<ItemsControl>(window.FindName("DownloadRowsList"));
        var select = model.Shortcuts.Single(shortcut => shortcut.Id == "SelectAll");
        Assert.Equal("SelectAll", model.ShortcutManager.FindMatch(Key.A, ModifierKeys.Control)?.Id);
        int confirmations = 0;
        bool confirmed = false;
        model.CleanupConfirmationOverride = _ =>
        {
            confirmations++;
            return (confirmed, false, false);
        };
        try
        {
            select.Key = Key.F8;
            select.Modifiers = ModifierKeys.None;
            model.SearchFilterText = "No matching download";
            await IdleAsync();
            Assert.Empty(list.Items);
            Assert.True(Press(list, Key.F8));
            Assert.True(Press(list, Key.Delete));
            await model.WaitForRemovalAsync();
            Assert.Equal(0, confirmations);
            Assert.False(package.IsSelected);
            Assert.False(Assert.Single(package.Items).IsSelected);
            Assert.Same(package, Assert.Single(model.Packages));
            Assert.Equal(contents, File.ReadAllBytes(path));

            model.SearchFilterText = string.Empty;
            model.Packages.Add(shown);
            model.SelectPackageFromPointer(package, controlPressed: false, wasSelected: false);
            model.SelectItemFromPointer(shownFile, controlPressed: true, wasSelected: false);
            model.SearchFilterText = "Shown";
            shown.IsExpanded = false;
            await IdleAsync();
            var toolbar = Descendants<Button>(window).Single(button => ReferenceEquals(button.Command, model.DeleteSelectedCommand));
            Assert.False(toolbar.IsEnabled);
            Assert.True(Press(list, Key.Delete));
            await model.WaitForRemovalAsync();
            Assert.Equal(0, confirmations);

            shown.IsExpanded = true;
            await IdleAsync();
            Assert.True(toolbar.IsEnabled);
            ClickDeleteMenu("PackageContextMenu", "Menu_DeletePackage", package);
            await model.WaitForRemovalAsync();
            Assert.Equal(0, confirmations);

            Invoke(toolbar);
            await UntilAsync(() => confirmations == 1);
            await model.WaitForRemovalAsync();
            Assert.Equal(contents, File.ReadAllBytes(shownPath));

            confirmed = true;
            ClickDeleteMenu("ItemContextMenu", "Menu_DeleteItem", shownFile);
            await model.WaitForRemovalAsync();
            Assert.Equal(2, confirmations);
            Assert.Same(package, Assert.Single(model.Packages));
            Assert.True(package.IsSelected);
            Assert.Equal(contents, File.ReadAllBytes(path));
            Assert.Equal(contents, File.ReadAllBytes(shownPath));
        }
        finally
        {
            select.Key = select.DefaultKey;
            select.Modifiers = select.DefaultModifiers;
            model.CleanupConfirmationOverride = null;
            model.DeselectAll();
            model.Packages.Remove(package);
            model.Packages.Remove(shown);
            model.SearchFilterText = string.Empty;
        }

        void ClickDeleteMenu(string menuKey, string headerKey, object target)
        {
            var menu = Assert.IsType<ContextMenu>(window.FindResource(menuKey));
            var delete = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, Loc.Get(headerKey)));
            delete.DataContext = target;
            try { delete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); }
            finally { delete.ClearValue(FrameworkElement.DataContextProperty); }
        }
    }

    private static bool Press(UIElement target, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        target.RaiseEvent(args);
        return args.Handled;
    }
}
