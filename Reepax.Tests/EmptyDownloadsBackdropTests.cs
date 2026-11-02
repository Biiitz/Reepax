using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Reepax.Helpers;
using Reepax.Models;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

public partial class DialogUiTests
{
    [Fact]
    public void EmptyBackdrop_DrawsAnimatedAsciiWithBoundedWorkAndNoVisualChildren()
    {
        RunInSta(() =>
        {
            var field = new AsciiHorizonBackground { EnableAnimation = false };
            byte[] first = DrawAscii(field, new Size(960, 540), 0);
            byte[] later = DrawAscii(field, new Size(960, 540), 9);
            Assert.Contains(first, value => value != 0);
            Assert.False(first.SequenceEqual(later));
            Assert.InRange(field.CellCount, 500, 2000);
            Assert.Equal(0, VisualTreeHelper.GetChildrenCount(field));
            Assert.False(field.IsHitTestVisible);
            Assert.False(field.Focusable);
            Assert.False(field.IsAnimating);
            var clock = Stopwatch.StartNew();
            for (int index = 0; index < 30; index++)
            {
                var visual = new DrawingVisual();
                using var context = visual.RenderOpen();
                field.DrawField(context, new Size(7680, 4320), index / 30.0);
            }
            Assert.InRange(field.CellCount, 1, 2600);
            Console.WriteLine($"ASCII drawing: cells={field.CellCount}; 30 large-window drawing passes={clock.Elapsed.TotalMilliseconds:F2} ms.");
        });
    }

    [Fact]
    public void EmptyBackdrop_IntroRevealsBinaryBurstsThenSettlesIntoTheRegularField()
    {
        RunInSta(() =>
        {
            var field = new AsciiHorizonBackground { EnableAnimation = false };
            var size = new Size(480, 320);
            byte[] hidden = DrawAscii(field, size, 9, introProgress: 0);
            byte[] early = DrawAscii(field, size, 9, introProgress: 0.16);
            byte[] middle = DrawAscii(field, size, 9, introProgress: 0.42);
            byte[] changing = DrawAscii(field, size, 9, introProgress: 0.46);
            byte[] settled = DrawAscii(field, size, 9);
            Assert.All(hidden, value => Assert.Equal(0, value));
            Assert.Contains(early, value => value > 0);
            Assert.False(middle.SequenceEqual(changing));
            Assert.True(early.Count(value => value > 0) < settled.Count(value => value > 0));
            Assert.True(middle.Select(value => (long)value).Sum() < settled.Select(value => (long)value).Sum());
            Assert.Equal(settled, DrawAscii(field, size, 9, introProgress: 2));
        });
    }

    [Fact]
    public void EmptyBackdrop_CurtainLightsTopAndSidesWhileKeepingTheCenterClear()
    {
        RunInSta(() =>
        {
            var field = new AsciiHorizonBackground { EnableAnimation = false };
            foreach (var size in new[] { new Size(960, 540), new Size(480, 640) })
            {
                byte[] pixels = DrawAscii(field, size, 9);
                double MeanAlpha(double x, double y, double width, double height)
                {
                    long total = 0;
                    int count = 0;
                    for (int row = (int)(y * size.Height); row < (int)((y + height) * size.Height); row++)
                    for (int column = (int)(x * size.Width); column < (int)((x + width) * size.Width); column++)
                    {
                        total += pixels[(row * (int)size.Width + column) * 4 + 3];
                        count++;
                    }
                    return (double)total / count;
                }
                double center = MeanAlpha(0.35, 0.35, 0.3, 0.3);
                Assert.True(MeanAlpha(0.35, 0, 0.3, 0.12) > center + 0.5);
                Assert.True(MeanAlpha(0, 0.25, 0.12, 0.55) > center + 0.5);
                Assert.True(MeanAlpha(0.88, 0.25, 0.12, 0.55) > center + 0.5);
                Assert.InRange(MeanAlpha(0.35, 0.82, 0.3, 0.18), 0, 0.1);
            }
        });
    }

    [Fact]
    public void EmptyBackdrop_AtlasUsesFrozenImagesAndProducesSoftTransparentGlyphs()
    {
        RunInSta(() =>
        {
            var atlas = new AsciiGlyphAtlas(1.5);
            Assert.Equal("01", AsciiGlyphAtlas.Characters);
            Assert.Equal(AsciiGlyphAtlas.Characters.Length, atlas.Sharp.Length);
            Assert.All(atlas.Sharp.Concat(atlas.Soft).Concat(atlas.Glow), image => Assert.True(image.IsFrozen));
            static byte[] Pixels(ImageSource image)
            {
                var bitmap = Assert.IsAssignableFrom<BitmapSource>(image);
                var data = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(data, bitmap.PixelWidth * 4, 0);
                return data;
            }
            Assert.Contains(Pixels(atlas.Sharp[0]), value => value != 0);
            Assert.False(Pixels(atlas.Sharp[0]).SequenceEqual(Pixels(atlas.Sharp[1])));
            Assert.False(Pixels(atlas.Sharp[1]).SequenceEqual(Pixels(atlas.Soft[1])));
            Assert.False(Pixels(atlas.Soft[1]).SequenceEqual(Pixels(atlas.Glow[1])));
        });
    }

    [Fact]
    public void EmptyBackdrop_StaggersIndividualDigitsAndRemovesTheirGlowCompletely()
    {
        RunInSta(() =>
        {
            var field = new AsciiHorizonBackground { EnableAnimation = false };
            byte[] full = DrawAscii(field, new Size(480, 320), 9);
            byte[] fading = DrawAscii(field, new Size(480, 320), 9, 0.2);
            byte[] hidden = DrawAscii(field, new Size(480, 320), 9, 0);
            int removed = 0;
            int remaining = 0;
            for (int pixel = 3; pixel < full.Length; pixel += 4)
            {
                if (full[pixel] < 12) continue;
                if (fading[pixel] == 0) removed++;
                if (fading[pixel] > 0) remaining++;
                Assert.True(fading[pixel] <= full[pixel]);
            }
            Assert.True(removed > 100);
            Assert.True(remaining > 100);
            Assert.All(hidden, value => Assert.Equal(0, value));
        });
    }

    [Fact]
    public void EmptyBackdrop_HidingStartsFromTheDisplayedFrame()
    {
        RunInSta(() => WithAsciiWindow((window, field) =>
        {
            var presentation = new Grid();
            window.Content = presentation;
            presentation.Children.Add(field);
            FadeAnimation.SetDurationMilliseconds(presentation, 220);
            FadeAnimation.SetIsShown(presentation, true);
            field.IsActive = true;
            PumpAscii(TimeSpan.FromMilliseconds(1100));
            Assert.Equal(1, field.RevealProgress);
            Assert.Equal(1, presentation.Opacity);
            field.IsActive = false;
            FadeAnimation.SetIsShown(presentation, false);
            if (SystemParameters.ClientAreaAnimation)
            {
                Assert.Equal(1, field.RevealProgress);
                Assert.Equal(1, presentation.Opacity);
                Assert.Equal(Visibility.Visible, presentation.Visibility);
            }
            PumpAscii(TimeSpan.FromMilliseconds(300));
            Assert.Equal(0, field.RevealProgress);
            Assert.Equal(Visibility.Collapsed, presentation.Visibility);
        }));
    }

    [Fact]
    public void EmptyBackdrop_StopsWhenHiddenDisabledMinimizedOrDetached()
    {
        RunInSta(() => WithAsciiWindow((window, field) =>
        {
            field.IsActive = true;
            PumpAscii(TimeSpan.FromMilliseconds(90));
            Assert.Equal(SystemParameters.ClientAreaAnimation, field.IsAnimating);
            field.Visibility = Visibility.Collapsed;
            Assert.False(field.IsAnimating);
            double intro = field.IntroProgress;
            long ticks = field.AnimationTicks;
            PumpAscii(TimeSpan.FromMilliseconds(80));
            Assert.Equal(ticks, field.AnimationTicks);
            Assert.Equal(intro, field.IntroProgress);
            field.Visibility = Visibility.Visible;
            Assert.Equal(SystemParameters.ClientAreaAnimation, field.IsAnimating);
            field.EnableAnimation = false;
            Assert.False(field.IsAnimating);
            Assert.Equal(1, field.IntroProgress);
            field.EnableAnimation = true;
            window.WindowState = WindowState.Minimized;
            Assert.False(field.IsAnimating);
            window.WindowState = WindowState.Normal;
            Assert.Equal(SystemParameters.ClientAreaAnimation, field.IsAnimating);
            field.IsEnabled = false;
            Assert.False(field.IsAnimating);
            field.IsEnabled = true;
            field.IsActive = false;
            Assert.False(field.IsAnimating);
            field.IsActive = true;
            if (SystemParameters.ClientAreaAnimation) Assert.InRange(field.IntroProgress, 0, 0.05);
            window.Content = null;
            PumpAscii(TimeSpan.FromMilliseconds(30));
            Assert.False(field.IsAnimating);
        }));
    }

    [Fact]
    public void EmptyBackdrop_TracksRealPackagesAndFadesOutWithoutBlockingInput()
    {
        RunInSta(() => WithAsciiWindow((window, field) =>
        {
            var model = new MainViewModel();
            var previous = model.Packages.ToArray();
            string previousSearch = model.SearchFilterText;
            try
            {
                model.Packages.Clear();
                var presentation = new Grid { IsHitTestVisible = false };
                window.Content = presentation;
                presentation.Children.Add(field);
                presentation.DataContext = model;
                FadeAnimation.SetDurationMilliseconds(presentation, 220);
                BindingOperations.SetBinding(presentation, FadeAnimation.IsShownProperty, new Binding(nameof(MainViewModel.IsDownloadsEmpty)));
                BindingOperations.SetBinding(field, AsciiHorizonBackground.IsActiveProperty, new Binding(nameof(MainViewModel.IsDownloadsEmpty)));
                PumpAscii(TimeSpan.FromMilliseconds(40));
                Assert.True(field.IsActive);
                Assert.Equal(Visibility.Visible, presentation.Visibility);
                model.Packages.Add(new DownloadPackage { Name = "Archive", IsCustomName = true, AutoPar2Repair = false });
                PumpAscii(TimeSpan.FromMilliseconds(80));
                Assert.False(field.IsActive);
                Assert.False(field.IsAnimating);
                if (SystemParameters.ClientAreaAnimation)
                {
                    Assert.Equal(Visibility.Visible, presentation.Visibility);
                    Assert.InRange(presentation.Opacity, 0.001, 0.999);
                    Assert.InRange(field.RevealProgress, 0.001, 0.999);
                }
                PumpAscii(TimeSpan.FromMilliseconds(220));
                Assert.Equal(Visibility.Collapsed, presentation.Visibility);
                Assert.Equal(0, field.RevealProgress);
                model.SearchFilterText = "no matching package";
                Assert.False(field.IsActive);
                model.Packages.Clear();
                PumpAscii(TimeSpan.FromMilliseconds(400));
                Assert.True(field.IsActive);
                Assert.Equal(Visibility.Visible, presentation.Visibility);
                Assert.Equal(1, presentation.Opacity);
                Assert.Equal(1, field.RevealProgress);
                Assert.False(presentation.IsHitTestVisible);
                for (int change = 0; change < 4; change++)
                {
                    model.Packages.Add(new DownloadPackage { IsCustomName = true, AutoPar2Repair = false });
                    model.Packages.Clear();
                }
                PumpAscii(TimeSpan.FromMilliseconds(400));
                Assert.True(field.IsActive);
                Assert.Equal(Visibility.Visible, presentation.Visibility);
                Assert.Equal(1, presentation.Opacity);
            }
            finally
            {
                model.Packages.Clear();
                foreach (var package in previous) model.Packages.Add(package);
                model.SearchFilterText = previousSearch;
            }
        }));
    }

    private static byte[] DrawAscii(AsciiHorizonBackground field, Size size, double seconds, double revealProgress = 1, double introProgress = 1)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) field.DrawField(context, size, seconds, revealProgress, introProgress);
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[(int)(size.Width * size.Height * 4)];
        bitmap.CopyPixels(pixels, (int)size.Width * 4, 0);
        return pixels;
    }

    private static void WithAsciiWindow(Action<Window, AsciiHorizonBackground> action)
    {
        var app = Application.Current;
        var previousShutdown = app.ShutdownMode;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var field = new AsciiHorizonBackground();
        var window = new Window { Width = 400, Height = 300, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false, Content = field };
        try { window.Show(); PumpAscii(TimeSpan.FromMilliseconds(25)); action(window, field); }
        finally { window.Close(); app.ShutdownMode = previousShutdown; }
    }

    private static void PumpAscii(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
    }
}
