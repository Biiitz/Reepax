using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Reepax.Helpers;
using Xunit;

namespace Reepax.Tests;

public partial class DialogUiTests
{
    [Fact]
    public void TechnicalLogo_OutlinePreservesHolesAndExcludesTranslucentShadows()
    {
        RunInSta(() =>
        {
            var pixels = new byte[9 * 7 * 4];
            for (int row = 1; row < 6; row++)
            for (int column = 1; column < 8; column++)
            {
                int offset = (row * 9 + column) * 4;
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 255;
                pixels[offset + 3] = column == 7 ? (byte)84 : (byte)255;
            }
            pixels[(3 * 9 + 3) * 4 + 3] = 0;
            var source = BitmapSource.Create(9, 7, 96, 96, PixelFormats.Bgra32, null, pixels, 9 * 4);
            var art = new LogoOutlineArt(source);
            Assert.True(art.Image.IsFrozen);
            Assert.True(art.Outline.IsFrozen);
            Assert.True(art.Outline.FillContains(new Point(2.5, 2.5)));
            Assert.False(art.Outline.FillContains(new Point(3.5, 3.5)));
            Assert.False(art.Outline.FillContains(new Point(7.5, 3.5)));
            Assert.False(art.Outline.FillContains(new Point(0.5, 0.5)));
            Assert.Same(LogoOutlineArt.Default, LogoOutlineArt.Default);
        });
    }

    [Fact]
    public void TechnicalLogo_DrawsIntroAndDownwardShineInsideTheLogo()
    {
        RunInSta(() =>
        {
            var logo = new TechnicalLogo();
            byte[] hidden = DrawTechnicalLogo(logo, 0, 0);
            byte[] intro = DrawTechnicalLogo(logo, 0.3, 0);
            byte[] resting = DrawTechnicalLogo(logo, 1, 0);
            byte[] shining = DrawTechnicalLogo(logo, 1, 0.5);
            Assert.All(hidden, value => Assert.Equal(0, value));
            Assert.Contains(intro, value => value > 0);
            Assert.False(intro.SequenceEqual(resting));
            Assert.False(resting.SequenceEqual(shining));
            Assert.InRange(resting.Where((_, index) => index % 4 == 3).Max(), 1, 72);
            long TotalAlpha(byte[] image) => image.Where((_, index) => index % 4 == 3).Sum(value => (long)value);
            Assert.True(TotalAlpha(intro) < TotalAlpha(resting));
            Assert.True(TotalAlpha(DrawTechnicalLogo(logo, 0.6, 0)) > TotalAlpha(intro));
            Assert.True(TotalAlpha(DrawTechnicalLogo(logo, 1, 0.5)) > TotalAlpha(resting));
            var occupied = Enumerable.Range(0, 280 * 280).Where(pixel => resting[pixel * 4 + 3] > 0).ToArray();
            int left = occupied.Min(pixel => pixel % 280) - 2;
            int right = occupied.Max(pixel => pixel % 280) + 2;
            int top = occupied.Min(pixel => pixel / 280) - 2;
            int bottom = occupied.Max(pixel => pixel / 280) + 2;
            foreach (var image in new[] { intro, shining, DrawTechnicalLogo(logo, 1, 0.25), DrawTechnicalLogo(logo, 1, 0.75) })
            for (int pixel = 0; pixel < 280 * 280; pixel++)
            {
                if (image[pixel * 4 + 3] == 0) continue;
                Assert.InRange(pixel % 280, left, right);
                Assert.InRange(pixel / 280, top, bottom);
            }
            Assert.False(ControlMotion.GetPressFeedback(logo));
            Assert.False(logo.IsAnimating);
            double HighlightCenter(byte[] frame)
            {
                long weighted = 0;
                long total = 0;
                for (int pixel = 0; pixel < 280 * 280; pixel++)
                {
                    int weight = Math.Max(0, frame[pixel * 4 + 3] - resting[pixel * 4 + 3]);
                    weighted += weight * (pixel / 280);
                    total += weight;
                }
                Assert.True(total > 0);
                return (double)weighted / total;
            }
            Assert.True(HighlightCenter(DrawTechnicalLogo(logo, 1, 0.25)) <
                HighlightCenter(DrawTechnicalLogo(logo, 1, 0.75)));
        });
    }

    [Fact]
    public void TechnicalLogo_ShineDelayVariesWithinTheRequestedRange()
    {
        var delays = Enumerable.Range(0, 64).Select(_ => TechnicalLogo.CreateShineDelay()).ToArray();
        Assert.All(delays, delay => Assert.InRange(delay.TotalSeconds, 7, 16));
        Assert.True(delays.Distinct().Count() > 1);
    }

    [Fact]
    public void TechnicalLogo_IgnoresHoverAndPausesWhenHidden()
    {
        RunInSta(() => WithAsciiWindow((window, _) =>
        {
            var logo = new TechnicalLogo { Width = 280, Height = 280, IsActive = true };
            window.Content = logo;
            PumpAscii(TimeSpan.FromMilliseconds(90));
            Assert.Equal(SystemParameters.ClientAreaAnimation, logo.IsAnimating);
            logo.Visibility = Visibility.Collapsed;
            double pausedIntro = logo.IntroProgress;
            PumpAscii(TimeSpan.FromMilliseconds(90));
            Assert.Equal(pausedIntro, logo.IntroProgress);
            Assert.False(logo.IsAnimating);
            logo.Visibility = Visibility.Visible;
            logo.EnableAnimation = false;
            Assert.Equal(1, logo.IntroProgress);
            Assert.False(logo.IsAnimating);
            logo.EnableAnimation = true;
            RaiseLogoPointer(logo, entering: true);
            PumpAscii(TimeSpan.FromMilliseconds(130));
            Assert.Equal(0, logo.ShineStarts);
            Assert.False(logo.IsAnimating);
            window.WindowState = WindowState.Minimized;
            Assert.False(logo.IsAnimating);
            window.WindowState = WindowState.Normal;
            PumpAscii(TimeSpan.FromMilliseconds(1100));
            Assert.Equal(0, logo.ShineStarts);
            Assert.False(logo.IsAnimating);
            RaiseLogoPointer(logo, entering: false);
            PumpAscii(TimeSpan.FromMilliseconds(1100));
            Assert.Equal(0, logo.ShineStarts);
            Assert.False(logo.IsAnimating);
            RaiseLogoPointer(logo, entering: true);
            window.Content = null;
            PumpAscii(TimeSpan.FromMilliseconds(30));
            Assert.False(logo.IsAnimating);
        }));
    }

    [Fact]
    public void TechnicalLogo_ShinesPeriodicallyAndPausesItsScheduleWhenHidden()
    {
        RunInSta(() => WithAsciiWindow((window, _) =>
        {
            TimeSpan firstDelay = TimeSpan.FromMilliseconds(100);
            TimeSpan repeatDelay = TimeSpan.FromSeconds(12);
            int scheduled = 0;
            var logo = new TechnicalLogo(() => ++scheduled == 1 ? firstDelay : repeatDelay)
            {
                Width = 280, Height = 280, IsActive = true, EnableAnimation = false
            };
            window.Content = logo;
            window.UpdateLayout();
            PumpAscii(TimeSpan.FromMilliseconds(40));
            logo.EnableAnimation = true;
            Assert.Equal(SystemParameters.ClientAreaAnimation, logo.IsShineScheduled);
            Assert.False(logo.IsAnimating);
            if (SystemParameters.ClientAreaAnimation)
            {
                Assert.Equal(firstDelay, logo.NextShineDelay);
                WaitForAnimationState(() => logo.ShineStarts > 0);
                Assert.Equal(1, logo.ShineStarts);
                WaitForAnimationState(() => !logo.IsAnimating);
                Assert.False(logo.IsAnimating);
                Assert.True(logo.IsShineScheduled);
                Assert.Equal(repeatDelay, logo.NextShineDelay);
                logo.BeginShine();
                WaitForAnimationState(() => logo.ShineProgress > 0.01);
                Assert.InRange(logo.ShineProgress, 0.01, 0.99);
            }
            logo.Visibility = Visibility.Collapsed;
            Assert.False(logo.IsShineScheduled);
            Assert.False(logo.IsAnimating);
            int starts = logo.ShineStarts;
            double shine = logo.ShineProgress;
            PumpAscii(TimeSpan.FromMilliseconds(120));
            Assert.Equal(starts, logo.ShineStarts);
            Assert.Equal(shine, logo.ShineProgress);
            logo.Visibility = Visibility.Visible;
            logo.EnableAnimation = false;
            Assert.False(logo.IsShineScheduled);
            Assert.False(logo.IsAnimating);
            logo.BeginShine();
            Assert.Equal(starts, logo.ShineStarts);
            logo.EnableAnimation = true;
            window.WindowState = WindowState.Minimized;
            Assert.False(logo.IsShineScheduled);
            Assert.False(logo.IsAnimating);
            window.WindowState = WindowState.Normal;
            Assert.Equal(SystemParameters.ClientAreaAnimation, logo.IsShineScheduled);
            logo.IsActive = false;
            Assert.False(logo.IsShineScheduled);
            logo.IsActive = true;
            window.Content = null;
            PumpAscii(TimeSpan.FromMilliseconds(30));
            Assert.False(logo.IsShineScheduled);
            Assert.False(logo.IsAnimating);
        }));
    }

    [Fact]
    public void TechnicalLogo_IsDecorativeAndReleasesInputWhenPackagesAppear()
    {
        RunInSta(() => WithAsciiWindow((window, _) =>
        {
            var model = new LogoPresentationProbe();
            var presentation = LoadEmptyDownloadsPresentation();
            var startup = new Grid { Visibility = Visibility.Visible };
            presentation.RegisterName("StartupLoadingOverlay", startup);
            presentation.DataContext = model;
            window.Content = presentation;
            PumpAscii(TimeSpan.FromMilliseconds(50));
            var logo = Assert.Single(presentation.Children.OfType<TechnicalLogo>());
            var background = Assert.Single(presentation.Children.OfType<AsciiHorizonBackground>());
            Assert.False(logo.IsVisible);
            Assert.False(logo.IsAnimating);
            Assert.False(background.IsVisible);
            Assert.False(background.IsAnimating);
            Assert.Equal(SystemParameters.ClientAreaAnimation ? 0 : 1, background.IntroProgress);
            startup.Visibility = Visibility.Collapsed;
            PumpAscii(TimeSpan.FromMilliseconds(50));
            Assert.True(logo.IsVisible);
            Assert.True(background.IsVisible);
            Assert.Equal(new Thickness(0), presentation.Margin);
            Assert.Equal(presentation.ActualWidth, background.ActualWidth, 3);
            Assert.Equal(presentation.ActualWidth / 2,
                logo.TranslatePoint(new Point(logo.ActualWidth / 2, 0), presentation).X, 3);
            if (SystemParameters.ClientAreaAnimation) Assert.InRange(background.IntroProgress, 0, 0.99);
            logo.EnableAnimation = false;
            Assert.False(logo.Focusable);
            Assert.False(logo.IsHitTestVisible);
            Assert.Null(UIElementAutomationPeer.CreatePeerForElement(logo));
            Assert.Null(presentation.InputHitTest(new Point(1, 1)));
            Assert.Null(logo.InputHitTest(new Point(5, 5)));
            Assert.Null(logo.InputHitTest(new Point(140, 140)));
            double intro = logo.IntroProgress;
            logo.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = Mouse.MouseDownEvent });
            PumpAscii(TimeSpan.FromMilliseconds(30));
            Assert.Equal(intro, logo.IntroProgress);
            Assert.Equal(Transform.Identity.Value, logo.RenderTransform.Value);
            model.IsDownloadsEmpty = false;
            PumpAscii(TimeSpan.FromMilliseconds(20));
            Assert.False(presentation.IsHitTestVisible);
            Assert.False(logo.IsEnabled);
            Assert.False(logo.IsAnimating);
            Assert.False(logo.IsShineScheduled);
        }));
    }

    private static byte[] DrawTechnicalLogo(TechnicalLogo logo, double intro, double shine = 0)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) logo.DrawLogo(context, new Size(280, 280), intro, shine);
        var bitmap = new RenderTargetBitmap(280, 280, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[280 * 280 * 4];
        bitmap.CopyPixels(pixels, 280 * 4, 0);
        return pixels;
    }

    private static void RaiseLogoPointer(TechnicalLogo logo, bool entering) => logo.RaiseEvent(
        new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = entering ? Mouse.MouseEnterEvent : Mouse.MouseLeaveEvent });

    private static Grid LoadEmptyDownloadsPresentation()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Reepax", "MainWindow.xaml");
        var document = XDocument.Load(path);
        XNamespace names = "http://schemas.microsoft.com/winfx/2006/xaml";
        var fragment = new XElement(document.Descendants().Single(element => (string?)element.Attribute(names + "Name") == "EmptyDownloadsPresentation"));
        foreach (var element in fragment.DescendantsAndSelf())
        {
            if (element.Name.NamespaceName.StartsWith("clr-namespace:"))
                element.Name = XName.Get(element.Name.LocalName, element.Name.NamespaceName + ";assembly=Reepax");
            foreach (var attribute in element.Attributes().Where(attribute => attribute.Name.NamespaceName.StartsWith("clr-namespace:")).ToArray())
            {
                element.SetAttributeValue(XName.Get(attribute.Name.LocalName, attribute.Name.NamespaceName + ";assembly=Reepax"), attribute.Value);
                attribute.Remove();
            }
        }
        foreach (var attribute in document.Root!.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            fragment.SetAttributeValue(attribute.Name, attribute.Value.StartsWith("clr-namespace:") ? attribute.Value + ";assembly=Reepax" : attribute.Value);
        return (Grid)XamlReader.Parse(fragment.ToString());
    }

    private sealed class LogoPresentationProbe : ObservableObject
    {
        private bool _isDownloadsEmpty = true;
        public bool IsDownloadsEmpty { get => _isDownloadsEmpty; set => SetProperty(ref _isDownloadsEmpty, value); }
    }
}
