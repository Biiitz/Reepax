using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Reepax.Models;
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
        }
        finally
        {
            ConfirmDialog.ShowDialogOverrideForTesting = null;
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
