using System.Windows.Media;
using Reepax.Helpers;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Reepax.Models;
using Reepax.Services;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.ViewModels;
using Reepax.Views;
using Xunit;

namespace Reepax.Tests;

public class PackageDoubleClickTests
{
    [Fact]
    public void Preference_DefaultsToOffAndRoundTripsEveryAction()
    {
        Assert.Equal(PackageDoubleClickAction.None, new AppSettings().PackageDoubleClickAction);
        Assert.Equal(PackageDoubleClickAction.None, JsonSerializer.Deserialize<AppSettings>("{}")!.PackageDoubleClickAction);
        foreach (var action in Enum.GetValues<PackageDoubleClickAction>())
        {
            var settings = new AppSettings { PackageDoubleClickAction = action };
            Assert.Equal(action, JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!.PackageDoubleClickAction);
        }
    }

    [Fact]
    public void ActionRequest_UsesOnlyTheClickedPackageAndPersistsTheSelection()
    {
        WithModel((model, first, second) =>
        {
            var requests = new List<(DownloadPackage Package, PackageDoubleClickAction Action)>();
            model.PackageDoubleClickRequested += (package, action) => requests.Add((package, action));
            Assert.False(model.TryRequestPackageDoubleClick(first));
            foreach (var action in Enum.GetValues<PackageDoubleClickAction>().Where(action => action != PackageDoubleClickAction.None))
            {
                model.SelectedPackageDoubleClickAction = action;
                Assert.Equal(action, SettingsService.Instance.Settings.PackageDoubleClickAction);
                Assert.True(model.TryRequestPackageDoubleClick(second));
                Assert.Same(second, requests[^1].Package);
                Assert.Equal(action, requests[^1].Action);
            }
            Assert.Equal(6, requests.Count);
            Assert.True(first.IsSelected);
            Assert.True(second.IsSelected);
            model.SelectedPackageDoubleClickAction = (PackageDoubleClickAction)999;
            Assert.Equal(PackageDoubleClickAction.None, model.SelectedPackageDoubleClickAction);
            Assert.False(model.TryRequestPackageDoubleClick(second));
        });
    }

    [Fact]
    public void ActionRequest_RejectsBusyEditingRemovedAndUnavailablePackages()
    {
        WithModel((model, first, _) =>
        {
            int requests = 0;
            model.PackageDoubleClickRequested += (_, _) => requests++;
            model.SelectedPackageDoubleClickAction = PackageDoubleClickAction.EditPackage;
            model.IsBusy = true;
            Assert.False(model.TryRequestPackageDoubleClick(first));
            model.IsBusy = false;
            first.IsEditing = true;
            Assert.False(model.TryRequestPackageDoubleClick(first));
            first.IsEditing = false;
            first.Status = DownloadStatus.Completed;
            Assert.False(model.TryRequestPackageDoubleClick(first));
            model.SelectedPackageDoubleClickAction = PackageDoubleClickAction.ExportPackage;
            Assert.False(model.TryRequestPackageDoubleClick(first));
            first.Status = DownloadStatus.Paused;
            first.Items[0].Status = DownloadStatus.Downloading;
            model.SelectedPackageDoubleClickAction = PackageDoubleClickAction.ChangeDownloadPath;
            Assert.False(model.TryRequestPackageDoubleClick(first));
            first.Items[0].Status = DownloadStatus.Paused;
            model.Packages.Remove(first);
            model.SelectedPackageDoubleClickAction = PackageDoubleClickAction.AddLinks;
            Assert.False(model.TryRequestPackageDoubleClick(first));
            Assert.Equal(0, requests);
        });
    }

    [Fact]
    public void Preference_UnknownSavedValueDisablesTheAction()
    {
        var settings = SettingsService.Instance.Settings;
        var previous = settings.PackageDoubleClickAction;
        try
        {
            settings.PackageDoubleClickAction = (PackageDoubleClickAction)999;
            Assert.Equal(PackageDoubleClickAction.None, new MainViewModel().SelectedPackageDoubleClickAction);
        }
        finally { settings.PackageDoubleClickAction = previous; }
    }

    [Fact]
    public void Gesture_RequiresAnUnmodifiedDoubleClickOutsideRenaming()
    {
        Assert.True(MainWindow.IsPackageDoubleClickGesture(2, ModifierKeys.None, editing: false));
        Assert.False(MainWindow.IsPackageDoubleClickGesture(1, ModifierKeys.None, editing: false));
        Assert.False(MainWindow.IsPackageDoubleClickGesture(3, ModifierKeys.None, editing: false));
        Assert.False(MainWindow.IsPackageDoubleClickGesture(2, ModifierKeys.None, editing: true));
        foreach (var modifiers in new[] { ModifierKeys.Control, ModifierKeys.Shift, ModifierKeys.Alt, ModifierKeys.Control | ModifierKeys.Shift })
            Assert.False(MainWindow.IsPackageDoubleClickGesture(2, modifiers, editing: false));
    }

    private static void WithModel(Action<MainViewModel, DownloadPackage, DownloadPackage> assertion)
    {
        var settings = SettingsService.Instance.Settings;
        var previousAction = settings.PackageDoubleClickAction;
        settings.PackageDoubleClickAction = PackageDoubleClickAction.None;
        var model = new MainViewModel();
        var previousPackages = model.Packages.ToArray();
        var first = new DownloadPackage { AutoPar2Repair = false, IsSelected = true };
        var second = new DownloadPackage { AutoPar2Repair = false, IsSelected = true };
        first.Items.Add(new DownloadItem { Status = DownloadStatus.Paused });
        second.Items.Add(new DownloadItem { Status = DownloadStatus.Paused });
        try
        {
            model.Packages.Clear();
            model.Packages.Add(first);
            model.Packages.Add(second);
            assertion(model, first, second);
        }
        finally
        {
            model.IsBusy = false;
            model.Packages.Clear();
            foreach (var package in previousPackages) model.Packages.Add(package);
            settings.PackageDoubleClickAction = previousAction;
        }
    }
}

public partial class DialogUiTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void PackageDoubleClickSetting_LocalizesAndSelectsEveryActionInBothModes(string language)
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var settings = SettingsService.Instance.Settings;
            var previousAction = settings.PackageDoubleClickAction;
            string previousLanguage = LocalizationService.Instance.CurrentLanguage;
            string previousTheme = ThemeService.Instance.CurrentTheme.Id;
            var shutdownMode = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            settings.PackageDoubleClickAction = PackageDoubleClickAction.None;
            var model = new MainViewModel();
            var view = new SettingsView { DataContext = model };
            var host = new Window { Content = view, Width = 1000, Height = 720,
                Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false };
            try
            {
                host.Show();
                LocalizationService.Instance.CurrentLanguage = language;
                foreach (string theme in new[] { ThemeCatalog.DefaultId, ThemeCatalog.MacOSId })
                {
                    ThemeService.Instance.SelectTheme(theme, save: false);
                    RenderSettings(view, $"PackageDoubleClick-{language}-{theme}", 1000);
                    var selector = Assert.IsType<SingleChoiceDropdown>(view.FindName("PackageDoubleClickSelector"));
                    var popup = ((SingleChoiceDropdown)view.FindName("PackageDoubleClickSelector")).DropdownPopup;
                    popup.IsOpen = true;
                    PumpSettingsAnimations();
                    var options = Descendants<Button>(popup.Child).ToArray();
                    Assert.Equal(7, options.Length);
                    Assert.Equal(Enum.GetValues<PackageDoubleClickAction>(), options.Select(item => (PackageDoubleClickAction)item.CommandParameter));
                    Assert.Equal(((Button)view.FindName("CompletionSoundButton")).Height, selector.Height);
                    Assert.Equal(((SolidColorBrush)((Border)((SingleChoiceDropdown)view.FindName("CompletionSoundButton")).DropdownPopup.Child).Background).Color, ((SolidColorBrush)((Border)popup.Child).Background).Color);
                    Assert.Equal(((SingleChoiceDropdown)view.FindName("CompletionSoundButton")).DropdownPopup.PopupAnimation, popup.PopupAnimation);
                    var info = Assert.IsType<Border>(view.FindName("PackageDoubleClickInfo"));
                    Assert.Equal(Loc.Get("Settings_PackageDoubleClick_Help"), info.ToolTip);
                    Assert.Contains(options.SelectMany(Descendants<TextBlock>), text => text.Text == (language == "de" ? "Aus" : "Off"));
                    foreach (var action in Enum.GetValues<PackageDoubleClickAction>())
                    {
                        options.Single(option => Equals(option.CommandParameter, action)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        Assert.Equal(action, model.SelectedPackageDoubleClickAction);
                        Assert.Equal(action, settings.PackageDoubleClickAction);
                        Assert.Contains(Descendants<TextBlock>(selector), text => text.Text == model.SelectedPackageDoubleClickActionDisplayName);
                    }
                }
            }
            finally
            {
                host.Content = null;
                ((SingleChoiceDropdown)view.FindName("PackageDoubleClickSelector")).DropdownPopup.IsOpen = false;
                host.Close();
                settings.PackageDoubleClickAction = previousAction;
                LocalizationService.Instance.CurrentLanguage = previousLanguage;
                ThemeService.Instance.SelectTheme(previousTheme, save: false);
                Application.Current.ShutdownMode = shutdownMode;
            }
        });
    }
}
