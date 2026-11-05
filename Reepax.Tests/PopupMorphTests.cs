using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Xml.Linq;
using Reepax.Helpers;
using Reepax.Models;
using Reepax.Services;
using Reepax.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace Reepax.Tests;

public partial class DialogUiTests
{
    private readonly ITestOutputHelper _popupOutput;
    public DialogUiTests(ITestOutputHelper output) => _popupOutput = output;

    [Theory]
    [InlineData(PlacementMode.Top)]
    [InlineData(PlacementMode.Bottom)]
    public void PopupMorph_AnimationNeverChangesPopupLayoutOrPlacement(PlacementMode placement)
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var shutdown = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var anchor = new Button { Content = "Example", Width = 140, Height = 32 };
            var host = new Window { Content = anchor, Width = 300, Height = 140, Left = 200, Top = 350,
                ShowActivated = false, ShowInTaskbar = false };
            var surface = new MorphingPopupBorder { Width = 220, Height = 180, Child = new TextBlock { Text = "Example options" },
                Style = (Style)Application.Current.FindResource("PopupSurfaceStyle") };
            var popup = new Popup { Child = surface, PlacementTarget = anchor, Placement = placement,
                AllowsTransparency = true, VerticalOffset = placement == PlacementMode.Top ? -10 : 4 };
            PopupMorph.SetIsEnabled(popup, true);
            int changes = 0;
            var margin = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(FrameworkElement.MarginProperty, typeof(MorphingPopupBorder));
            var offset = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Popup.VerticalOffsetProperty, typeof(Popup));
            EventHandler changed = (_, _) => changes++;
            var frames = new List<(double Time, Point Position, double Progress)>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            EventHandler rendered = (_, _) =>
            {
                if (surface.IsMorphing && surface.HasAnimatedProperties)
                    frames.Add((clock.Elapsed.TotalMilliseconds, surface.PointToScreen(new Point()), surface.Progress));
            };
            CompositionTarget.Rendering += rendered;
            margin.AddValueChanged(surface, changed);
            offset.AddValueChanged(popup, changed);
            try
            {
                host.Show();
                popup.IsOpen = true;
                PumpSettingsAnimations(500);
                Assert.Equal(0, changes);
                if (SystemParameters.ClientAreaAnimation)
                {
                    Assert.True(frames.Count >= 2, "The real WPF render loop must sample the running animation.");
                    Assert.All(frames, frame => Assert.InRange((frame.Position - frames[0].Position).Length, 0, 0.5));
                    Assert.All(frames, frame => Assert.InRange(frame.Progress, 0, 1));
                    Assert.All(frames.Zip(frames.Skip(1)), pair => Assert.True(pair.Second.Progress >= pair.First.Progress));
                    var gaps = frames.Zip(frames.Skip(1), (first, second) => second.Time - first.Time).OrderBy(gap => gap).ToArray();
                    _popupOutput.WriteLine($"{placement}: {frames.Count} frames; maximum frame gap {gaps[^1]:F2} ms; p95 {gaps[(int)((gaps.Length - 1) * 0.95)]:F2} ms; no placement changes.");
                }
                clock.Restart();
                popup.IsOpen = false;
                _popupOutput.WriteLine($"{placement}: logical close returned in {clock.Elapsed.TotalMilliseconds:F2} ms.");
                PumpSettingsAnimations(20);
                Assert.False(surface.IsMorphing);
                Assert.Equal(0, changes);
            }
            finally
            {
                margin.RemoveValueChanged(surface, changed);
                offset.RemoveValueChanged(popup, changed);
                CompositionTarget.Rendering -= rendered;
                popup.IsOpen = false;
                PopupMorph.SetIsEnabled(popup, false);
                host.Close();
                Application.Current.ShutdownMode = shutdown;
            }
        });
    }

    [Theory]
    [InlineData(PlacementMode.Bottom, false)]
    [InlineData(PlacementMode.Top, false)]
    [InlineData(PlacementMode.Bottom, true)]
    [InlineData(PlacementMode.Top, true)]
    public void PopupMorph_UsesActualAnchorAndClosesWithoutHoldingInput(PlacementMode placement, bool screenEdge)
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var shutdown = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var anchor = new Button { Content = "Example", Width = 140, Height = 32 };
            var host = new Window
            {
                Content = anchor, Width = 260, Height = 140,
                Left = SystemParameters.WorkArea.Left + 200,
                Top = screenEdge ? (placement == PlacementMode.Bottom ? SystemParameters.WorkArea.Bottom - 140 : SystemParameters.WorkArea.Top) : SystemParameters.WorkArea.Top + 350,
                ShowActivated = false, ShowInTaskbar = false
            };
            var content = new StackPanel();
            for (int index = 0; index < 5; index++) content.Children.Add(new Button { Content = $"Option {index}", Height = 28 });
            var surface = new MorphingPopupBorder { Width = 200, Child = content, Style = (Style)Application.Current.FindResource("PopupSurfaceStyle") };
            var popup = new Popup { Child = surface, PlacementTarget = anchor, Placement = placement,
                AllowsTransparency = true, StaysOpen = false, VerticalOffset = placement == PlacementMode.Top ? -10 : 4 };
            PopupMorph.SetIsEnabled(popup, true);
            var margin = surface.Margin;
            double offset = popup.VerticalOffset;
            try
            {
                host.Show();
                popup.IsOpen = true;
                PumpSettingsAnimations(90);
                Assert.Equal(PopupAnimation.Fade, popup.PopupAnimation);
                if (SystemParameters.ClientAreaAnimation)
                {
                    Assert.Equal((placement == PlacementMode.Top) != screenEdge, surface.OpensUpward);
                    Assert.InRange(surface.Progress, 0.001, 1);
                    Assert.Equal(anchor.ActualWidth, surface.Origin.Width, 2);
                    Assert.Equal(Math.Min(20, surface.ActualHeight), surface.Origin.Height, 2);
                }
                PumpSettingsAnimations(450);
                Assert.Equal(1, surface.Progress);
                Assert.False(surface.HasAnimatedProperties);
                Assert.Null(content.Clip);
                Assert.Equal(1, content.Opacity);
                if (SystemParameters.ClientAreaAnimation)
                {
                    Assert.InRange(surface.Origin.Left, 0, surface.ActualWidth - surface.Origin.Width);
                    Assert.InRange(surface.Origin.Top, 0, surface.ActualHeight - surface.Origin.Height);
                }
                SaveMorphFrames(surface, placement.ToString());
                popup.IsOpen = false;
                Assert.False(popup.IsOpen);
                PumpSettingsAnimations(20);
                Assert.False(surface.IsMorphing);
                Assert.Equal(margin, surface.Margin);
                Assert.Equal(offset, popup.VerticalOffset);
                popup.IsOpen = true;
                PumpSettingsAnimations(20);
                Assert.Equal(SystemParameters.ClientAreaAnimation, surface.IsMorphing);
                PumpSettingsAnimations(450);
                popup.IsOpen = false;
                PumpSettingsAnimations(260);
                Assert.False(surface.IsMorphing);
                popup.IsOpen = true;
                PumpSettingsAnimations(40);
                popup.IsOpen = false;
                popup.IsOpen = true;
                PumpSettingsAnimations(480);
                Assert.True(popup.IsOpen);
                Assert.Equal(1, surface.Progress);
                popup.IsOpen = false;
                PumpSettingsAnimations(260);
                Assert.Equal(margin, surface.Margin);
                Assert.Equal(offset, popup.VerticalOffset);
                Assert.False(surface.IsMorphing);
            }
            finally
            {
                popup.IsOpen = false;
                PopupMorph.SetIsEnabled(popup, false);
                host.Close();
                Application.Current.ShutdownMode = shutdown;
            }
        });
    }

    [Fact]
    public void PopupMorph_OutlineTravelsFromButtonToCardInBothDirections()
    {
        var size = new Size(200, 160);
        foreach (bool upward in new[] { false, true })
        {
            var origin = new Rect(60, upward ? 180 : -52, 140, 32);
            var start = MorphingPopupBorder.CreateOutline(size, origin, 0, new CornerRadius(6), upward);
            var middle = MorphingPopupBorder.CreateOutline(size, origin, 0.5, new CornerRadius(6), upward);
            var end = MorphingPopupBorder.CreateOutline(size, origin, 1, new CornerRadius(6), upward);
            Assert.Equal(origin, start.Bounds);
            Assert.True(middle.Bounds.Height > start.Bounds.Height);
            Assert.True(middle.Bounds.Height < end.Bounds.Height);
            Assert.Equal(new Rect(size), end.Bounds);
        }
    }

    [Fact]
    public void PopupMorph_ContentFadesDuringRevealWithoutSwitchingShadowOrTextCache()
    {
        RunInSta(() =>
        {
            var clip = new RectangleGeometry(new Rect(0, 0, 180, 140));
            var text = new TextBlock { Text = "Example options", Clip = clip, Opacity = 0.8 };
            var effect = new System.Windows.Media.Effects.DropShadowEffect();
            var surface = new MorphingPopupBorder { Width = 200, Height = 160, Child = text, Effect = effect,
                Origin = new Rect(80, 140, 120, 20), OpensUpward = true };
            surface.Measure(new Size(200, 160));
            surface.Arrange(new Rect(0, 0, 200, 160));
            surface.BeginMorph();
            surface.Progress = 0.1;
            Assert.Equal(0, text.Opacity);
            Assert.Null(text.CacheMode);
            Assert.Same(effect, surface.Effect);
            surface.Progress = 0.6;
            Assert.InRange(text.Opacity, 0.01, 0.79);
            surface.Progress = 1;
            Assert.Equal(0.8, text.Opacity);
            Assert.Same(clip, text.Clip);
            surface.EndMorph();
            Assert.Same(clip, text.Clip);
            Assert.Same(effect, surface.Effect);
            Assert.Null(text.CacheMode);
            Assert.False(surface.IsMorphing);
        });
    }

    [Fact]
    public void PopupMorph_ComboBoxDoesNotStackANativeSlideOverTheContour()
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var shutdown = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var combo = new ComboBox { Width = 160, VerticalAlignment = VerticalAlignment.Center };
            combo.Items.Add("First option");
            combo.Items.Add("Second option");
            var host = new Window { Content = combo, Width = 300, Height = 140, Left = 200, Top = 350,
                ShowActivated = false, ShowInTaskbar = false };
            Popup? popup = null;
            try
            {
                host.Show();
                popup = Assert.IsType<Popup>(combo.Template.FindName("Popup", combo));
                Assert.Equal(PopupAnimation.Fade, popup.PopupAnimation);
                Assert.True(PopupMorph.GetIsEnabled(popup));
                combo.IsDropDownOpen = true;
                PumpSettingsAnimations(400);
                var surface = Assert.Single(Descendants<MorphingPopupBorder>(popup.Child));
                Assert.False(surface.IsMorphing);
                Assert.False(surface.HasAnimatedProperties);
                Assert.NotNull(surface.Child);
                Assert.Null(surface.Child.Clip);
                Assert.Null(surface.Child.CacheMode);
                combo.SelectedIndex = 1;
                combo.IsDropDownOpen = false;
                PumpSettingsAnimations(260);
                Assert.Equal("Second option", combo.SelectedItem);
                Assert.False(popup.IsOpen);
            }
            finally
            {
                combo.IsDropDownOpen = false;
                if (popup != null) PopupMorph.SetIsEnabled(popup, false);
                host.Close();
                Application.Current.ShutdownMode = shutdown;
            }
        });
    }

    [Theory]
    [InlineData("en", ThemeCatalog.DefaultId)]
    [InlineData("de", ThemeCatalog.MacOSId)]
    public void PopupMorph_ProductionDownloadFilterPreservesSelectionAndEscape(string language, string theme)
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var model = new MainViewModel();
            var previous = (model.SelectedLanguage, model.SelectedThemeId, model.SelectedStatusFilter);
            var shutdown = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Reepax", "MainWindow.xaml"));
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var markup = new XElement(document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "StatusFilterButton"));
            foreach (var element in markup.DescendantsAndSelf())
            {
                if (element.Name.NamespaceName.StartsWith("clr-namespace:"))
                    element.Name = XName.Get(element.Name.LocalName, element.Name.NamespaceName + ";assembly=Reepax");
                foreach (var attribute in element.Attributes().Where(a => a.Name.NamespaceName.StartsWith("clr-namespace:")).ToArray())
                {
                    element.SetAttributeValue(XName.Get(attribute.Name.LocalName, attribute.Name.NamespaceName + ";assembly=Reepax"), attribute.Value);
                    attribute.Remove();
                }
            }
            foreach (var attribute in document.Root!.Attributes().Where(a => a.IsNamespaceDeclaration))
                markup.SetAttributeValue(attribute.Name, attribute.Value.StartsWith("clr-namespace:") ? attribute.Value + ";assembly=Reepax" : attribute.Value);
            var selector = (SingleChoiceDropdown)XamlReader.Parse(markup.ToString());
            selector.DataContext = model;
            var host = new Window { Content = selector, Width = 400, Height = 140,
                Left = 200, Top = 350, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                model.SelectedLanguage = language;
                model.SelectedThemeId = theme;
                model.SelectedStatusFilter = DownloadStatusFilter.All;
                host.Show();
                selector.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                PumpSettingsAnimations(480);
                Assert.True(selector.DropdownPopup.IsOpen);
                var surface = Assert.IsType<MorphingPopupBorder>(selector.DropdownPopup.Child);
                if (SystemParameters.ClientAreaAnimation) Assert.True(surface.OpensUpward);
                Assert.Equal(1, surface.Progress);
                SaveMorphFrames(surface, $"Filter-{language}-{theme}");
                var paused = Descendants<Button>(surface).Single(button => Equals(button.CommandParameter, DownloadStatusFilter.Paused));
                paused.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(DownloadStatusFilter.Paused, model.SelectedStatusFilter);
                Assert.False(selector.DropdownPopup.IsOpen);
                PumpSettingsAnimations(450);
                selector.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                PumpSettingsAnimations(100);
                Assert.True(selector.DropdownPopup.IsOpen);
                selector.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(selector), Environment.TickCount, Key.Escape)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Assert.False(selector.DropdownPopup.IsOpen);
                Assert.Equal(DownloadStatusFilter.Paused, model.SelectedStatusFilter);
                PumpSettingsAnimations(260);
                Assert.False(surface.IsMorphing);
            }
            finally
            {
                selector.CloseDropdown();
                PopupMorph.SetIsEnabled(selector.DropdownPopup, false);
                host.Close();
                model.SelectedLanguage = previous.SelectedLanguage;
                model.SelectedThemeId = previous.SelectedThemeId;
                model.SelectedStatusFilter = previous.SelectedStatusFilter;
                Application.Current.ShutdownMode = shutdown;
            }
        });
    }

    [Theory]
    [InlineData(ThemeCatalog.DefaultId, false)]
    [InlineData(ThemeCatalog.DefaultId, true)]
    [InlineData(ThemeCatalog.MacOSId, false)]
    [InlineData(ThemeCatalog.MacOSId, true)]
    public void PopupMorph_FinalFrameMatchesSettledSurface(string theme, bool upward)
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            string previous = ThemeService.Instance.CurrentTheme.Id;
            try
            {
                ThemeService.Instance.SelectTheme(theme, save: false);
                var surface = new MorphingPopupBorder
                {
                    Width = 220, Height = 160,
                    Style = (Style)Application.Current.FindResource("PopupSurfaceStyle"),
                    Background = (Brush)Application.Current.FindResource("BgCardBrush"),
                    BorderBrush = (Brush)Application.Current.FindResource("BorderDarkBrush"),
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                    Child = new TextBlock { Text = "Example options", Margin = new Thickness(12) },
                    Origin = new Rect(80, upward ? 140 : 0, 140, 20), OpensUpward = upward
                };
                var root = new Border { Padding = new Thickness(24), Child = surface };
                root.Measure(new Size(268, 208));
                root.Arrange(new Rect(0, 0, 268, 208));
                byte[] Capture()
                {
                    root.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(268, 208, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var pixels = new byte[268 * 208 * 4];
                    bitmap.CopyPixels(pixels, 268 * 4, 0);
                    return pixels;
                }
                surface.BeginMorph();
                surface.Progress = 1;
                byte[] finalFrame = Capture();
                surface.EndMorph();
                byte[] settled = Capture();
                Assert.Contains(settled.Where((_, index) => index % 4 == 3), alpha => alpha > 0);
                Assert.True(finalFrame.SequenceEqual(settled),
                    $"Finishing the animation changed {finalFrame.Zip(settled).Count(pair => pair.First != pair.Second)} rendered channels.");
            }
            finally { ThemeService.Instance.SelectTheme(previous, save: false); }
        });
    }

    private static void SaveMorphFrames(MorphingPopupBorder surface, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("REEPAX_POPUP_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var originalOrigin = surface.Origin;
        try
        {
            surface.BeginMorph();
            foreach (double progress in new[] { 0.0, 0.25, 0.6, 1.0 })
            {
                surface.Progress = progress;
                surface.UpdateLayout();
                var root = (FrameworkElement)PresentationSource.FromVisual(surface).RootVisual;
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(directory, $"{name}-{progress:F2}.png"));
                png.Save(output);
            }
        }
        finally { surface.EndMorph(); surface.Origin = originalOrigin; }
    }
}
