using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Collections.ObjectModel;
using System.Windows.Shapes;
using System.Windows.Media;
using Reepax.Helpers;
using Reepax.Models;
using Reepax.Services;
using Reepax.ViewModels;
using Reepax.Views;
using Xunit;

namespace Reepax.Tests;

public partial class DialogUiTests
{
    [Theory]
    [InlineData("en", ThemeCatalog.DefaultId)]
    [InlineData("de", ThemeCatalog.MacOSId)]
    public void SoundSelectors_ShareAllOptionsAndScrollAfterSevenRows(string language, string theme)
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var model = new MainViewModel();
            var previous = (model.SelectedLanguage, model.SelectedThemeId, model.EnableCompletionSound, model.EnableErrorSound,
                model.SelectedCompletionSound, model.SelectedErrorSound);
            model.SelectedLanguage = language;
            model.SelectedThemeId = theme;
            model.EnableCompletionSound = model.EnableErrorSound = true;
            var view = new SettingsView { DataContext = model };
            var host = new Window { Content = view, Width = 1100, Height = 750, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false };
            var shutdown = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                host.Show();
                PumpSettingsAnimations();
                foreach (string name in new[] { "CompletionSoundButton", "ErrorSoundButton" })
                {
                    var selector = (SingleChoiceDropdown)view.FindName(name);
                    Assert.Equal(7, selector.MaxVisibleItems);
                    selector.DropdownPopup.IsOpen = true;
                    PumpSettingsAnimations();
                    var scroll = Assert.Single(Descendants<ScrollViewer>(selector.DropdownPopup.Child));
                    var rows = Descendants<Button>(scroll).Where(button => button.CommandParameter is string).ToArray();
                    Assert.Equal(model.SoundOptions.Count, rows.Length);
                    Assert.True(rows.Length > 7);
                    Assert.Equal(rows.Take(7).Sum(row => row.ActualHeight + row.Margin.Top + row.Margin.Bottom), scroll.ViewportHeight, 1);
                    Assert.True(scroll.ScrollableHeight > 0);
                    scroll.ScrollToBottom();
                    PumpSettingsAnimations();
                    Assert.Equal(scroll.ScrollableHeight, scroll.VerticalOffset, 1);
                    selector.CloseDropdown();
                }
            }
            finally
            {
                host.Close();
                model.SelectedLanguage = previous.SelectedLanguage;
                model.SelectedThemeId = previous.SelectedThemeId;
                model.EnableCompletionSound = previous.EnableCompletionSound;
                model.EnableErrorSound = previous.EnableErrorSound;
                model.SelectedCompletionSound = previous.SelectedCompletionSound;
                model.SelectedErrorSound = previous.SelectedErrorSound;
                Application.Current.ShutdownMode = shutdown;
            }
        });
    }

    [Theory]
    [InlineData("en", ThemeCatalog.DefaultId)]
    [InlineData("de", ThemeCatalog.MacOSId)]
    public void SharedChoiceDropdown_FitsSelectedTextAndKeepsIndependentSelections(string language, string theme)
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var model = new MainViewModel();
            var previous = (model.SelectedLanguage, model.SelectedThemeId, model.SelectedPackageDoubleClickAction);
            var shutdown = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var view = new SettingsView { DataContext = model };
            var host = new Window { Content = view, Width = 1100, Height = 750, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false };
            try
            {
                model.SelectedLanguage = language;
                model.SelectedThemeId = theme;
                model.SelectedPackageDoubleClickAction = PackageDoubleClickAction.None;
                host.Show();
                PumpSettingsAnimations();
                var selector = Assert.IsType<SingleChoiceDropdown>(view.FindName("PackageDoubleClickSelector"));
                var languageChoice = Assert.IsType<SingleChoiceDropdown>(view.FindName("LanguageSelector"));
                var themeChoice = Assert.IsType<SingleChoiceDropdown>(view.FindName("ThemeSelector"));
                foreach (string name in new[] { "CompletionSoundButton", "ErrorSoundButton", "PostDownloadActionSelector", "CompletionSoundTriggerSelector" })
                    Assert.IsType<SingleChoiceDropdown>(view.FindName(name));
                double shortWidth = selector.ActualWidth;
                Assert.InRange(shortWidth, 30, 130);
                int commits = 0;
                selector.SelectionCommitted += (_, _) => commits++;
                selector.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                PumpSettingsAnimations();
                Assert.True(selector.DropdownPopup.IsOpen);
                Assert.True(((FrameworkElement)selector.DropdownPopup.Child).ActualWidth > shortWidth);
                var option = Descendants<Button>(selector.DropdownPopup.Child)
                    .Single(button => Equals(button.CommandParameter, PackageDoubleClickAction.CreateInstallFolder));
                option.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                PumpSettingsAnimations();
                Assert.Equal(1, commits);
                Assert.Equal(PackageDoubleClickAction.CreateInstallFolder, model.SelectedPackageDoubleClickAction);
                Assert.True(selector.ActualWidth > shortWidth);
                Assert.False(selector.DropdownPopup.IsOpen);
                Assert.Equal(language, languageChoice.SelectedValue);
                Assert.Equal(theme, themeChoice.SelectedValue);
                Assert.False(languageChoice.DropdownPopup.IsOpen);
                Assert.False(themeChoice.DropdownPopup.IsOpen);
                model.SelectedLanguage = language == "en" ? "de" : "en";
                PumpSettingsAnimations();
                Assert.Contains(Descendants<TextBlock>(selector), text => text.Text == model.SelectedPackageDoubleClickActionDisplayName);
                Assert.Equal(1, commits);
                selector.DropdownPopup.IsOpen = true;
                model.SelectedThemeId = theme == ThemeCatalog.DefaultId ? ThemeCatalog.MacOSId : ThemeCatalog.DefaultId;
                PumpSettingsAnimations();
                Assert.False(selector.DropdownPopup.IsOpen);
                selector.DropdownPopup.IsOpen = true;
                PumpSettingsAnimations();
                var chrome = (Border)selector.DropdownPopup.Child;
                Assert.True(selector.DropdownPopup.IsOpen);
                Assert.Equal(((SolidColorBrush)Application.Current.FindResource("BgCardBrush")).Color, ((SolidColorBrush)chrome.Background).Color);
                Assert.All(Descendants<Path>(chrome), check => Assert.Equal(((SolidColorBrush)Application.Current.FindResource("TextPrimaryBrush")).Color, ((SolidColorBrush)check.Fill).Color));
                selector.CloseDropdown();
                var source = new ObservableCollection<ChoiceOption> { new() { Text = "First", Value = 1 } };
                themeChoice.ItemsSource = source;
                themeChoice.DisplayMemberPath = nameof(ChoiceOption.Text);
                themeChoice.SelectedValuePath = nameof(ChoiceOption.Value);
                themeChoice.DropdownPopup.IsOpen = true;
                PumpSettingsAnimations();
                source.Add(new ChoiceOption { Text = "Second", Value = 2 });
                PumpSettingsAnimations();
                Assert.Equal(2, Descendants<Button>(themeChoice.DropdownPopup.Child).Count());
                source.RemoveAt(0);
                PumpSettingsAnimations();
                Assert.Single(Descendants<Button>(themeChoice.DropdownPopup.Child));
                themeChoice.CloseDropdown();
            }
            finally
            {
                host.Close();
                model.SelectedPackageDoubleClickAction = previous.SelectedPackageDoubleClickAction;
                model.SelectedLanguage = previous.SelectedLanguage;
                model.SelectedThemeId = previous.SelectedThemeId;
                Application.Current.ShutdownMode = shutdown;
            }
        });
    }
}
