using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Reepax.Services;
using Reepax.Services.Localization;
using Reepax.Services.Settings;
using Reepax.ViewModels;
using Reepax.Views;

namespace Reepax.Tests;

public partial class DialogUiTests
{
    [Theory]
    [InlineData("en", ThemeCatalog.DefaultId)]
    [InlineData("de", ThemeCatalog.DefaultId)]
    [InlineData("en", ThemeCatalog.MacOSId)]
    [InlineData("de", ThemeCatalog.MacOSId)]
    public void SettingsSearch_IndexesAllCategoriesAndNavigatesWithoutChangingSettings(string language, string theme)
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var model = new MainViewModel();
            var previousLanguage = model.SelectedLanguage;
            var previousTheme = ThemeService.Instance.CurrentTheme.Id;
            var previousCategory = model.SelectedSettingsCategory;
            bool previousExpanded = model.IsArchivePasswordsExpanded;
            string previousInput = model.NewArchivePasswordInput;
            var shutdownMode = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var host = new Window { Width = 1000, Height = 720, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                model.SelectedLanguage = language;
                ThemeService.Instance.SelectTheme(theme, save: false);
                model.NewArchivePasswordInput = "ExamplePrivateSearchToken";
                model.SelectedSettingsCategory = SettingsCategory.General;
                var view = new SettingsView { DataContext = model };
                host.Content = view;
                host.Show();
                RenderSettings(view, "SettingsSearch-Initial-" + language + "-" + theme, 1000);
                var box = Assert.IsType<TextBox>(view.FindName("SettingsSearchBox"));
                var results = Assert.IsType<ListBox>(view.FindName("SettingsSearchResultList"));
                var content = Assert.IsType<ScrollViewer>(view.FindName("SettingsContentScrollViewer"));
                var searchBar = Assert.IsType<Border>(view.FindName("SettingsSearchBar"));
                var general = Assert.IsType<StackPanel>(view.FindName("CategoryGeneral"));
                var appearance = general.Children.OfType<Border>().First();
                Assert.Equal(appearance.ActualWidth, searchBar.ActualWidth, 3);
                Assert.Equal(appearance.TranslatePoint(new Point(), view).X, searchBar.TranslatePoint(new Point(), view).X, 3);
                Assert.Equal(16, appearance.TranslatePoint(new Point(), view).Y - searchBar.TranslatePoint(new Point(0, searchBar.ActualHeight), view).Y, 3);
                foreach (string name in new[] { "CategoryGeneral", "CategoryDownloadConnections", "CategoryShortcuts" })
                    Assert.Empty(Assert.IsType<StackPanel>(view.FindName(name)).Children.OfType<TextBlock>());
                Assert.Contains(view.SettingsSearchEntries, entry => entry.Key == "Overview_Show");
                Assert.All(new[] { "Settings_SpeedLimit_Title", "Settings_Card_ArchivePasswords_Title", "Settings_Theme_Title", "About_Links_Releases" },
                    key => Assert.Contains(view.SettingsSearchEntries, entry => entry.Key == key));
                Assert.All(model.Shortcuts, shortcut => Assert.Contains(view.SettingsSearchEntries, entry => entry.Key == shortcut.TitleKey));
                var realIndex = new SettingsSearchIndex(view.SettingsSearchEntries);
                foreach (var shortcut in model.Shortcuts)
                {
                    Assert.Contains(realIndex.Search(Strings_en.Map[shortcut.TitleKey]), entry => entry.Key == shortcut.TitleKey);
                    Assert.Contains(realIndex.Search(Strings_de.Map[shortcut.TitleKey]), entry => entry.Key == shortcut.TitleKey);
                }
                Assert.Equal("Settings_Theme_Title", Assert.Single(realIndex.Search("White Mode")).Key);
                Assert.Equal("Settings_Theme_Title", Assert.Single(realIndex.Search("Dark Mode")).Key);
                Assert.Contains(realIndex.Search("PC herunterfahren"), entry => entry.Key == "Settings_PostDownload_Title");
                var sound = Services.Audio.SoundCatalog.Default.Sounds.First();
                Assert.Contains(realIndex.Search(sound.Text), entry => entry.Key == "Settings_Sound_Completion_Title");
                Assert.Contains(realIndex.Search(sound.Text), entry => entry.Key == "Settings_Sound_Error_Title");
                Assert.All(view.SettingsSearchEntries.SelectMany(entry => entry.SearchKeys), key => Assert.True(Strings_en.Map.ContainsKey(key)));
                double speed = model.SpeedLimitMBps;
                bool extract = model.AutoExtractArchives;
                box.Text = "ExamplePrivateSearchToken";
                Assert.Empty(results.Items);
                Assert.Equal(Loc.Get("Settings_Search_NoResults"), Assert.IsType<TextBlock>(view.FindName("SettingsSearchStatus")).Text);
                foreach (string query in new[] { "t", "th", "the", "them", "theme", "them", "the", "th", "t", "hem", "eme" })
                {
                    box.Text = query;
                    Assert.Contains(results.Items.Cast<SettingsSearchEntry>(), entry => entry.Key == "Settings_Theme_Title");
                }
                box.Text = "thm";
                Assert.Empty(results.Items);
                box.Text = language == "en" ? "passwörter" : "protected archives";
                RenderSettings(view, "SettingsSearch-Results-" + language + "-" + theme, 1000);
                Assert.Equal(Visibility.Hidden, content.Visibility);
                Assert.Equal(appearance.ActualWidth, searchBar.ActualWidth, 3);
                var passwordEntry = Assert.IsType<SettingsSearchEntry>(results.Items[0]);
                Assert.Equal("Settings_Card_ArchivePasswords_Title", passwordEntry.Key);
                Assert.Equal(Loc.Get(passwordEntry.Key), passwordEntry.Title);
                Assert.Equal(((SolidColorBrush)view.FindResource("TextPrimaryBrush")).Color, ((SolidColorBrush)box.Foreground).Color);
                var row = Assert.IsType<ListBoxItem>(results.ItemContainerGenerator.ContainerFromIndex(0));
                var offsets = new List<double>();
                void TrackScroll(object sender, ScrollChangedEventArgs args)
                {
                    if (ReferenceEquals(args.OriginalSource, content) && args.VerticalChange != 0)
                        offsets.Add(content.VerticalOffset);
                }
                content.ScrollChanged += TrackScroll;
                row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = Mouse.PreviewMouseUpEvent,
                    Source = row
                });
                if (SystemParameters.ClientAreaAnimation)
                {
                    Assert.True(view.IsSettingsSearchNavigationActive);
                    Assert.True(Assert.IsType<Grid>(view.FindName("SettingsSearchResults")).HasAnimatedProperties);
                }
                WaitForSettingsSearchNavigation(view);
                content.ScrollChanged -= TrackScroll;
                view.UpdateLayout();
                Assert.Equal(string.Empty, box.Text);
                Assert.Equal(SettingsCategory.DownloadConnections, model.SelectedSettingsCategory);
                Assert.True(model.IsArchivePasswordsExpanded);
                Assert.Equal(Visibility.Visible, content.Visibility);
                Assert.True(content.VerticalOffset > 0);
                if (SystemParameters.ClientAreaAnimation)
                {
                    Assert.True(offsets.Distinct().Count() > 2, "Search navigation must publish intermediate scroll offsets.");
                    Assert.All(offsets.Zip(offsets.Skip(1)), pair => Assert.True(pair.Second >= pair.First));
                }
                Assert.Equal(speed, model.SpeedLimitMBps);
                Assert.Equal(extract, model.AutoExtractArchives);
                Assert.Equal("ExamplePrivateSearchToken", model.NewArchivePasswordInput);
                RenderSettings(view, "SettingsSearch-Arrived-" + language + "-" + theme, 1000);
                var passwordHeading = Descendants<TextBlock>(view).Single(text => text.Text == Loc.Get("Settings_Card_ArchivePasswords_Title"));
                double expectedOffset = Math.Clamp(content.VerticalOffset + passwordHeading.TranslatePoint(new Point(), content).Y - 16,
                    0, content.ScrollableHeight);
                Assert.Equal(expectedOffset, content.VerticalOffset, 2);
                double offset = content.VerticalOffset;
                box.Text = "sound";
                Assert.True(results.Items.Count >= 2);
                Assert.True(box.Focus());
                void Press(Key key) => box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(host)!, 0, key)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent,
                    Source = box
                });
                Press(Key.Down);
                Assert.Equal(1, results.SelectedIndex);
                Press(Key.Up);
                Assert.Equal(0, results.SelectedIndex);
                Press(Key.Escape);
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(string.Empty, box.Text);
                Assert.Equal(offset, content.VerticalOffset);
                box.Text = "start all";
                results.SelectedItem = results.Items.Cast<SettingsSearchEntry>().Single(entry => entry.Key == "Shortcut_StartAll_Title");
                Press(Key.Enter);
                WaitForSettingsSearchNavigation(view);
                Assert.Equal(SettingsCategory.Shortcuts, model.SelectedSettingsCategory);
                Assert.Equal(string.Empty, box.Text);
                box.Text = "clipboard";
                model.SelectedLanguage = language == "en" ? "de" : "en";
                Assert.Equal("Settings_ClipboardMonitor_Title", Assert.IsType<SettingsSearchEntry>(results.Items[0]).Key);
                Assert.Equal(Loc.Get("Settings_ClipboardMonitor_Title"), Assert.IsType<SettingsSearchEntry>(results.Items[0]).Title);
                Assert.Equal(Loc.Format("Settings_Search_Results", results.Items.Count), Assert.IsType<TextBlock>(view.FindName("SettingsSearchStatus")).Text);
                model.SelectedSettingsCategory = SettingsCategory.About;
                Assert.Equal(string.Empty, box.Text);
                Assert.Equal(Visibility.Visible, content.Visibility);
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(0, content.VerticalOffset);
                Assert.False(searchBar.IsVisible);
                Assert.False(box.IsVisible);
                model.SelectedSettingsCategory = SettingsCategory.General;
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.True(searchBar.IsVisible);
            }
            finally
            {
                host.Content = null;
                host.Close();
                model.NewArchivePasswordInput = previousInput;
                model.IsArchivePasswordsExpanded = previousExpanded;
                model.SelectedSettingsCategory = previousCategory;
                model.SelectedLanguage = previousLanguage;
                ThemeService.Instance.SelectTheme(previousTheme, save: false);
                Application.Current.ShutdownMode = shutdownMode;
            }
        });
    }

    [Fact]
    public void SettingsSearchNavigation_CancelsOnTypingScrollingCategoryChangesAndDetachment()
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var model = new MainViewModel();
            var previousCategory = model.SelectedSettingsCategory;
            bool previousExpanded = model.IsArchivePasswordsExpanded;
            var shutdownMode = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var host = new Window { Width = 1000, Height = 720, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                model.SelectedSettingsCategory = SettingsCategory.General;
                var view = new SettingsView { DataContext = model };
                host.Content = view;
                host.Show();
                RenderSettings(view, "SettingsSearch-Navigation", 1000);
                var box = (TextBox)view.FindName("SettingsSearchBox");
                var content = (ScrollViewer)view.FindName("SettingsContentScrollViewer");
                var results = (Grid)view.FindName("SettingsSearchResults");
                var passwords = view.SettingsSearchEntries.Single(entry => entry.Key == "Settings_Card_ArchivePasswords_Title");
                var theme = view.SettingsSearchEntries.Single(entry => entry.Key == "Settings_Theme_Title");

                box.Text = "passwords";
                view.OpenSettingsSearchResult(passwords);
                box.Text = "connections";
                Assert.False(view.IsSettingsSearchNavigationActive);
                Assert.Equal("connections", box.Text);
                Assert.False(results.HasAnimatedProperties);
                Assert.Equal(1, results.Opacity);

                view.OpenSettingsSearchResult(passwords);
                view.OpenSettingsSearchResult(theme);
                WaitForSettingsSearchNavigation(view);
                Assert.Equal(SettingsCategory.General, model.SelectedSettingsCategory);
                Assert.Equal(string.Empty, box.Text);
                var themeRow = (FrameworkElement)((FrameworkElement)view.FindName("ThemeSelector")).Parent;
                Assert.InRange(themeRow.TranslatePoint(new Point(), content).Y, 15, 17);

                if (SystemParameters.ClientAreaAnimation)
                {
                    box.Text = "passwords";
                    view.OpenSettingsSearchResult(passwords);
                    WaitForSettingsSearchState(() => view.IsSettingsSearchNavigationActive && content.IsVisible);
                    content.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120)
                    {
                        RoutedEvent = Mouse.PreviewMouseWheelEvent,
                        Source = content
                    });
                    Assert.False(view.IsSettingsSearchNavigationActive);
                    Assert.False(view.HasAnimatedProperties);
                    Assert.False(content.HasAnimatedProperties);
                    Assert.Equal(1, content.Opacity);
                    content.ScrollToVerticalOffset(20);
                    content.UpdateLayout();
                    Assert.Equal(20, content.VerticalOffset);
                }

                box.Text = "passwords";
                view.OpenSettingsSearchResult(passwords);
                model.SelectedSettingsCategory = SettingsCategory.About;
                Assert.False(view.IsSettingsSearchNavigationActive);
                Assert.Equal(1, content.Opacity);
                Assert.False(results.HasAnimatedProperties);
                Assert.Equal(SettingsCategory.About, model.SelectedSettingsCategory);

                model.SelectedSettingsCategory = SettingsCategory.General;
                box.Text = "passwords";
                view.OpenSettingsSearchResult(passwords);
                host.Content = null;
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.False(view.IsSettingsSearchNavigationActive);
                Assert.False(view.HasAnimatedProperties);
                Assert.False(content.HasAnimatedProperties);
                Assert.False(results.HasAnimatedProperties);
                Assert.Equal(1, content.Opacity);
                Assert.Equal(1, results.Opacity);
            }
            finally
            {
                host.Content = null;
                host.Close();
                model.SelectedSettingsCategory = previousCategory;
                model.IsArchivePasswordsExpanded = previousExpanded;
                Application.Current.ShutdownMode = shutdownMode;
            }
        });
    }

    private static void WaitForSettingsSearchNavigation(SettingsView view)
    {
        var content = (ScrollViewer)view.FindName("SettingsContentScrollViewer");
        WaitForSettingsSearchState(() => !view.IsSettingsSearchNavigationActive && !content.HasAnimatedProperties &&
            !content.RenderTransform.HasAnimatedProperties);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void WaitForSettingsSearchState(Func<bool> condition)
    {
        if (condition()) return;
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => frame.Continue = !condition() && deadline.Elapsed < TimeSpan.FromSeconds(3);
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.True(condition(), "The search navigation did not reach its expected state.");
    }
}
