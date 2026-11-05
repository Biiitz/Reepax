using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using Reepax.Helpers;
using Reepax.Models;
using Reepax.Services;
using Reepax.Views;
using Xunit;

namespace Reepax.Tests;

public partial class DialogUiTests
{
    [Theory]
    [InlineData(ThemeCatalog.DefaultId)]
    [InlineData(ThemeCatalog.MacOSId)]
    public void ButtonPress_UsesFooterMotionWithoutChangingColorsOrOpacity(string theme)
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            string previousTheme = ThemeService.Instance.CurrentTheme.Id;
            var shutdown = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var panel = new StackPanel
            {
                DataContext = new { SelectedStatusFilter = DownloadStatusFilter.All, SelectedStatusFilterText = "Example",
                    IsStatusFilterActive = false, ArchivePasswordsCount = 2, IsArchivePasswordsExpanded = false }
            };
            panel.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("pack://application:,,,/Reepax;component/Themes/SettingsControls.xaml") });
            var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Reepax", "MainWindow.xaml"));
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var footer = new XElement(document.Descendants().Single(node => (string?)node.Attribute(x + "Name") == "StatusFilterButton"));
            footer.SetAttributeValue("SelectedValue", "{Binding SelectedStatusFilter, Mode=OneWay}");
            DownloadListVirtualizationTests.SetNamespaces(footer, document.Root!);
            panel.Children.Add((Button)XamlReader.Parse(footer.ToString()));
            foreach (string key in new[] { "ModernButtonStyle", "PopupButtonStyle", "AccentButtonStyle", "RowActionButtonStyle",
                "SettingsActionButtonStyle", "AccentColorButtonStyle", "SoundDropdownButtonStyle", "SoundDropdownItemStyle",
                "ArchivePasswordToggleButtonStyle", "PasswordActionButtonStyle", "PasswordDeleteButtonStyle", "SettingsCategoryButtonStyle" })
                panel.Children.Add(new Button { Name = key, Content = "Example", Style = (Style)panel.FindResource(key) });
            var dialog = new ConfirmDialog("Example", "Example confirmation");
            panel.Children.Add(new Button { Name = "DangerButton", Content = "Example", Style = (Style)dialog.FindResource("DangerButtonStyle") });
            var host = new Window { Content = panel, Width = 900, Height = 720, Left = -10000, Top = -10000,
                ShowInTaskbar = false, ShowActivated = false };
            var keyField = typeof(ButtonBase).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(field => field.FieldType == typeof(DependencyPropertyKey) && field.Name.Contains("IsPressed", StringComparison.Ordinal));
            var pressed = (DependencyPropertyKey)keyField.GetValue(null)!;
            try
            {
                ThemeService.Instance.SelectTheme(theme, save: false);
                host.Show();
                PumpSettingsAnimations();
                var states = panel.Children.OfType<Button>().Select(button =>
                {
                    var frame = Assert.IsType<Border>(VisualTreeHelper.GetChild(button, 0));
                    var content = Assert.IsAssignableFrom<FrameworkElement>(frame.Child);
                    return (Button: button, Frame: frame, Content: content, Background: frame.Background, Border: frame.BorderBrush,
                        Opacity: frame.Opacity, ContentOpacity: content.Opacity, Transform: content.RenderTransform,
                        Bounds: frame.TransformToAncestor(panel).TransformBounds(new Rect(frame.RenderSize)));
                }).ToArray();
                foreach (var state in states) state.Button.SetValue(pressed, true);
                PumpSettingsAnimations(180);
                foreach (var state in states)
                {
                    Assert.True(ControlMotion.GetPressFeedback(state.Button), state.Button.Name);
                    Assert.Same(state.Background, state.Frame.Background);
                    Assert.Same(state.Border, state.Frame.BorderBrush);
                    Assert.Equal(state.Opacity, state.Frame.Opacity);
                    Assert.Equal(state.ContentOpacity, state.Content.Opacity);
                    Assert.Equal(state.Transform.Value.OffsetY + 1, state.Content.RenderTransform.Value.OffsetY, precision: 3);
                    Assert.Equal(state.Bounds, state.Frame.TransformToAncestor(panel).TransformBounds(new Rect(state.Frame.RenderSize)));
                }
                foreach (var state in states) state.Button.SetValue(pressed, false);
                PumpSettingsAnimations(180);
                foreach (var state in states)
                    Assert.Equal(state.Transform.Value.OffsetY, state.Content.RenderTransform.Value.OffsetY, precision: 3);
                host.Content = null;
                PumpSettingsAnimations(20);
                foreach (var state in states) Assert.Same(state.Transform, state.Content.RenderTransform);
            }
            finally
            {
                host.Close();
                dialog.Close();
                ThemeService.Instance.SelectTheme(previousTheme, save: false);
                Application.Current.ShutdownMode = shutdown;
            }
        });
    }
}
