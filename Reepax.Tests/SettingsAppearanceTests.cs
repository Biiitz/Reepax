using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using Reepax.Helpers;
using Reepax.Services;
using Reepax.ViewModels;
using Reepax.Views;
using Xunit;

namespace Reepax.Tests;

public partial class DialogUiTests
{
    [Theory]
    [InlineData("#FFFFFF", true)]
    [InlineData("#FFFF00", true)]
    [InlineData("#000000", false)]
    [InlineData("#1E293B", false)]
    public void AccentGlyphs_KeepContrastAndUpdateExistingCheckboxes(string hex, bool darkGlyph)
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            string original = ThemeService.Instance.CurrentAccentColorHex;
            var shutdownMode = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Window? host = null;
            try
            {
                var checkbox = new CheckBox { IsChecked = true };
                host = new Window { Content = checkbox, Width = 100, Height = 60 };
                checkbox.Style = (Style)Application.Current.FindResource(typeof(CheckBox));
                checkbox.ApplyTemplate();
                ThemeService.Instance.SetAccentColor(hex, save: false);
                host.Measure(new Size(100, 60));
                host.Arrange(new Rect(0, 0, 100, 60));
                PumpSettingsAnimations();
                var glyph = Assert.IsType<System.Windows.Shapes.Path>(checkbox.Template.FindName("CheckGlyph", checkbox));
                var dash = Assert.IsType<Border>(checkbox.Template.FindName("IndeterminateGlyph", checkbox));
                Color expected = darkGlyph ? Colors.Black : Colors.White;
                Assert.Equal(expected, Assert.IsType<SolidColorBrush>(glyph.Stroke).Color);
                Assert.Equal(expected, Assert.IsType<SolidColorBrush>(dash.Background).Color);
                Assert.Equal(expected, ColorHelper.GetContrastForeground((Color)ColorConverter.ConvertFromString(hex)));
            }
            finally
            {
                if (host != null) { host.Content = null; host.Close(); }
                ThemeService.Instance.SetAccentColor(original, save: false);
                Application.Current.ShutdownMode = shutdownMode;
            }
        });
    }

    [Fact]
    public void SettingsSurface_PreservesLanguageSelectionAndCategoryContent()
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var vm = new MainViewModel();
            string previousLanguage = vm.SelectedLanguage;
            string previousAccent = ThemeService.Instance.CurrentAccentColorHex;
            bool previousPasswordsExpanded = vm.IsArchivePasswordsExpanded;
            var shutdownMode = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Window? host = null;
            try
            {
                var view = new SettingsView { DataContext = vm };
                host = new Window { Content = view, Width = 900, Height = 680 };
                RenderSettings(view, "General");
                var languageSelector = Assert.IsType<SingleChoiceDropdown>(view.FindName("LanguageSelector"));
                Assert.Equal(2, languageSelector.Options.Count);
                Assert.Null(languageSelector.FocusVisualStyle);
                var expanders = Descendants<AnimatedExpansionDecorator>(view).ToArray();
                Assert.Equal(2, expanders.Length);
                Assert.All(expanders, expander => Assert.True(expander.EnableAnimation));
                var palette = Assert.IsType<Border>(view.FindName("ColorPaletteContainer"));
                Assert.Equal(new Thickness(0), palette.BorderThickness);
                Assert.Null(palette.Background);
                vm.ToggleColorPaletteCommand.Execute(null);
                RenderSettings(view, "PaletteExpanded");
                Assert.True(expanders[0].IsExpanded);
                vm.ToggleColorPaletteCommand.Execute(null);
                languageSelector.SetCurrentValue(SingleChoiceDropdown.SelectedValueProperty, "de");
                Assert.Equal("de", vm.SelectedLanguage);
                languageSelector.SetCurrentValue(SingleChoiceDropdown.SelectedValueProperty, "en");
                Assert.Equal("en", vm.SelectedLanguage);
                foreach (SettingsCategory category in Enum.GetValues<SettingsCategory>().Where(category => category.ToString() is "General" or "DownloadConnections" or "Shortcuts" or "About"))
                {
                    vm.SelectedSettingsCategory = category;
                    RenderSettings(view, category.ToString());
                    Assert.True(view.DesiredSize.Width > 0);
                    Assert.NotEmpty(Descendants<Button>(view));
                }
                vm.SelectedSettingsCategory = SettingsCategory.DownloadConnections;
                vm.IsArchivePasswordsExpanded = true;
                RenderSettings(view, "PasswordsExpanded");
                expanders[1].BringIntoView();
                RenderSettings(view, "PasswordsExpanded");
                Assert.True(expanders[1].IsExpanded);
                Assert.Equal(Visibility.Visible, expanders[1].Child.Visibility);
                ThemeService.Instance.SetAccentColor("#FFFFFF", save: false);
                vm.SelectedSettingsCategory = SettingsCategory.General;
                RenderSettings(view, "GeneralWhite");
                Assert.Equal("en", languageSelector.SelectedValue);
                languageSelector.DropdownPopup.IsOpen = true;
                PumpSettingsAnimations();
                Assert.All(Descendants<System.Windows.Shapes.Path>(languageSelector.DropdownPopup.Child),
                    glyph => Assert.Equal(Application.Current.FindResource("TextPrimaryBrush"), glyph.Fill));
                languageSelector.CloseDropdown();
            }
            finally
            {
                if (host != null) { host.Content = null; host.Close(); }
                vm.SelectedLanguage = previousLanguage;
                vm.IsArchivePasswordsExpanded = previousPasswordsExpanded;
                ThemeService.Instance.SetAccentColor(previousAccent, save: false);
                Application.Current.ShutdownMode = shutdownMode;
            }
        });
    }

    [Fact]
    public void Chevron_ReversesWithoutReplacingTheExistingTransform()
    {
        RunInSta(() =>
        {
            var original = new TranslateTransform(3, 2);
            var icon = new Border { RenderTransform = original };
            ControlMotion.SetIsExpanded(icon, true);
            var group = Assert.IsType<TransformGroup>(icon.RenderTransform);
            Assert.Same(original, group.Children[0]);
            Assert.Equal(180, Assert.IsType<RotateTransform>(group.Children[1]).Angle);
            ControlMotion.SetIsExpanded(icon, false);
            Assert.Equal(0, Assert.IsType<RotateTransform>(group.Children[1]).Angle);
        });
    }

    private static void EnsureSettingsTheme()
    {
        var app = Application.Current!;
        if (!app.Resources.Contains("EqualsToVisConverter"))
        {
            string project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Reepax"));
            var application = XDocument.Load(Path.Combine(project, "App.xaml"));
            var resources = new XElement(application.Descendants().First(element => element.Name.LocalName == "ResourceDictionary"));
            foreach (var element in resources.DescendantsAndSelf())
            {
                if (element.Name.NamespaceName.StartsWith("clr-namespace:") && !element.Name.NamespaceName.Contains(";assembly="))
                    element.Name = XName.Get(element.Name.LocalName, element.Name.NamespaceName + ";assembly=Reepax");
                if (element.Attribute("Source") is { } source)
                    source.Value = "pack://application:,,,/Reepax;component/" + source.Value;
            }
            foreach (var attribute in application.Root!.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
                resources.SetAttributeValue(attribute.Name, attribute.Value.StartsWith("clr-namespace:") ? attribute.Value + ";assembly=Reepax" : attribute.Value);
            app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(resources.ToString()));
        }
        if (!app.Resources.Contains("ModernButtonStyle"))
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Reepax;component/Themes/DarkTheme.xaml") });
    }

    [Fact]
    public void ButtonFeedback_PreservesPositionAndReleasesAnimationsOnUnload()
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var position = new TranslateTransform(12, 3);
            var button = new Button { Style = (Style)Application.Current.FindResource("ModernButtonStyle"), RenderTransform = position };
            button.ApplyTemplate();
            var frame = Assert.IsType<Border>(VisualTreeHelper.GetChild(button, 0));
            var surface = Assert.IsAssignableFrom<FrameworkElement>(frame.Child);
            Transform original = surface.RenderTransform;
            Transform frameTransform = frame.RenderTransform;
            button.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var field = typeof(ButtonBase).GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Single(candidate => candidate.FieldType == typeof(DependencyPropertyKey) && candidate.Name.Contains("IsPressed", StringComparison.Ordinal));
            var pressedKey = (DependencyPropertyKey)field.GetValue(null)!;
            button.SetValue(pressedKey, true);
            var group = Assert.IsType<TransformGroup>(surface.RenderTransform);
            var offset = Assert.IsType<TranslateTransform>(group.Children[1]);
            PumpSettingsAnimations();
            Assert.Equal(1, offset.Y, precision: 3);
            Assert.Same(frameTransform, frame.RenderTransform);
            button.SetValue(pressedKey, false);
            button.SetValue(pressedKey, true);
            button.SetValue(pressedKey, false);
            PumpSettingsAnimations();
            Assert.Equal(0, offset.Y, precision: 3);
            Assert.False(offset.HasAnimatedProperties);
            button.SetValue(pressedKey, true);
            button.IsEnabled = false;
            Assert.Equal(0, offset.Y);
            Assert.False(offset.HasAnimatedProperties);
            Assert.Same(position, button.RenderTransform);
            button.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Assert.Same(original, surface.RenderTransform);
            button.SetValue(pressedKey, true);
            Assert.Same(original, surface.RenderTransform);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Expansion_RapidReversalSettlesAndUnloadingReleasesAnimation(bool expanded)
    {
        RunInSta(() =>
        {
            var shutdownMode = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var original = new TranslateTransform(2, 3);
            var icon = new Border { Width = 8, Height = 8 };
            var child = new Border { Width = 180, Height = 140, RenderTransform = original, Child = icon };
            var expander = new AnimatedExpansionDecorator { Child = child, IsExpanded = false, EnableAnimation = true };
            BindingOperations.SetBinding(icon, ControlMotion.IsExpandedProperty, new Binding(nameof(expander.IsExpanded)) { Source = expander });
            var host = new Window { Content = expander, Width = 220, Height = 200, Left = -10000, Top = -10000, ShowInTaskbar = false };
            try
            {
                host.Show();
                PumpSettingsAnimations();
                expander.IsExpanded = true;
                PumpSettingsAnimations(45);
                double progress = expander.ExpansionProgress;
                var rotation = Assert.IsType<RotateTransform>(Assert.IsType<TransformGroup>(icon.RenderTransform).Children[1]);
                double angle = rotation.Angle;
                Assert.InRange(progress, 0.0, 1.0);
                expander.IsExpanded = false;
                if (SystemParameters.ClientAreaAnimation)
                {
                    Assert.Equal(progress, expander.ExpansionProgress, precision: 3);
                    Assert.Equal(angle, rotation.Angle, precision: 3);
                }
                Assert.False(child.IsHitTestVisible);
                expander.IsExpanded = true;
                expander.IsExpanded = expanded;
                PumpSettingsAnimations();
                Assert.Equal(expanded ? 1.0 : 0.0, expander.ExpansionProgress);
                Assert.Equal(expanded ? Visibility.Visible : Visibility.Collapsed, child.Visibility);
                Assert.Equal(expanded, child.IsHitTestVisible);
                Assert.False(expander.HasAnimatedProperties);
                Assert.Equal(expanded ? 180 : 0, rotation.Angle);
                Assert.False(rotation.HasAnimatedProperties);
                Assert.Same(original, Assert.IsType<TransformGroup>(child.RenderTransform).Children[0]);
                expander.IsExpanded = !expanded;
                host.Content = null;
                PumpSettingsAnimations();
                Assert.False(expander.HasAnimatedProperties);
                Assert.False(rotation.HasAnimatedProperties);
                Assert.Equal(expander.IsExpanded ? 1.0 : 0.0, expander.ExpansionProgress);
                expander.Child = null;
                Assert.Same(original, child.RenderTransform);
            }
            finally
            {
                host.Content = null;
                host.Close();
                Application.Current.ShutdownMode = shutdownMode;
            }
        });
    }

    private static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    [Fact]
    public void StartupSurface_RendersLocalizedStepsWithoutChangingProgressLayout()
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            string source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Reepax", "MainWindow.xaml"));
            var document = XDocument.Load(source);
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var overlayMarkup = new XElement(document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "StartupLoadingOverlay"));
            foreach (var attribute in document.Root!.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
                overlayMarkup.SetAttributeValue(attribute.Name, attribute.Value.StartsWith("clr-namespace:") ? attribute.Value + ";assembly=Reepax" : attribute.Value);
            var overlay = (Grid)XamlReader.Parse(overlayMarkup.ToString());
            var progress = (ScaleTransform)overlay.FindName("StartupProgressScale");
            var fill = (Border)overlay.FindName("StartupProgressFill");
            var detail = (TextBlock)overlay.FindName("StartupStatusDetail");
            string language = Reepax.Services.Localization.LocalizationService.Instance.CurrentLanguage;
            try
            {
                foreach (string selectedLanguage in new[] { "de", "en" })
                {
                    Reepax.Services.Localization.LocalizationService.Instance.CurrentLanguage = selectedLanguage;
                    detail.Text = Reepax.Services.Localization.Loc.Get("Startup_Step_PreheatingComponents");
                    progress.ScaleX = 0.55;
                    RenderSettings(overlay, "Startup" + selectedLanguage);
                    Assert.Equal(300, fill.ActualWidth);
                    progress.ScaleX = 1;
                    overlay.UpdateLayout();
                    Assert.Equal(300, fill.ActualWidth);
                    Assert.True(detail.ActualWidth <= 300);
                }
            }
            finally
            {
                Reepax.Services.Localization.LocalizationService.Instance.CurrentLanguage = language;
            }
        });
    }

    private static void RenderSettings(FrameworkElement view, string name, int width = 900)
    {
        PumpSettingsAnimations();
        view.Measure(new Size(width, 680));
        view.Arrange(new Rect(0, 0, width, 680));
        view.UpdateLayout();
        PumpSettingsAnimations();
        string? directory = Environment.GetEnvironmentVariable("REEPAX_UI_PREVIEW_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var image = new RenderTargetBitmap(width, 680, 96, 96, PixelFormats.Pbgra32);
        image.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(output);
    }

    private static void PumpSettingsAnimations(int milliseconds = 280)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
