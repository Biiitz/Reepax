using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Reepax.Helpers;
using Reepax.Services.Localization;
using Reepax.ViewModels;
using Reepax.Views;
using Xunit;

namespace Reepax.Tests;

public partial class DialogUiTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void SettingsBranches_KeepAllCategoriesAccessibleAndLabelsVisible(string language)
    {
        RunInSta(() => WithAsciiWindow((window, _) =>
        {
            string previousLanguage = LocalizationService.Instance.CurrentLanguage;
            var resources = LoadBranchedSettingsResources();
            Application.Current.Resources.MergedDictionaries.Add(resources);
            SettingsView? view = null;
            try
            {
                LocalizationService.Instance.CurrentLanguage = language;
                var model = new MainViewModel();
                view = new SettingsView { DataContext = model };
                window.Width = 1000;
                window.Height = 700;
                window.Content = view;
                window.UpdateLayout();
                PumpAscii(TimeSpan.FromMilliseconds(70));
                var rail = (BranchedNavigationRail)view.FindName("CategoryBranchRail");
                Assert.Equal(4, rail.BranchCount);
                Assert.Equal(0, rail.ActiveIndex);
                var categories = new[]
                {
                    ("GeneralCategoryButton", SettingsCategory.General, "Settings_Category_General"),
                    ("DownloadsCategoryButton", SettingsCategory.DownloadConnections, "Settings_Category_Downloads"),
                    ("ShortcutsCategoryButton", SettingsCategory.Shortcuts, "Settings_Category_Shortcuts"),
                    ("AboutCategoryButton", SettingsCategory.About, "Settings_Category_About")
                };
                for (int index = 0; index < categories.Length; index++)
                {
                    var (name, category, key) = categories[index];
                    var button = (Button)view.FindName(name);
                    Assert.True(button.IsVisible);
                    Assert.True(button.Focusable);
                    Assert.Equal(0, ((SolidColorBrush)button.Background).Color.A);
                    Assert.True(ControlMotion.GetPressFeedback(button));
                    var peer = new ButtonAutomationPeer(button);
                    Assert.Equal(Loc.Get(key), peer.GetName());
                    ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
                    WaitForSettingsBranch(rail, button, index);
                    Assert.Equal(category, model.SelectedSettingsCategory);
                    Assert.Equal(index, rail.ActiveIndex);
                    Assert.Equal(button.TranslatePoint(new Point(0, button.ActualHeight / 2), rail).Y, rail.CurrentCenter);
                    Assert.Equal(20, rail.BranchReach);
                    Assert.False(rail.HasAnimatedProperties);
                    var label = Assert.Single(((Grid)button.Content).Children.OfType<TextBlock>());
                    var text = new FormattedText(label.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                        new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch),
                        label.FontSize, Brushes.White, VisualTreeHelper.GetDpi(label).PixelsPerDip);
                    Assert.True(label.ActualWidth + 1 >= text.WidthIncludingTrailingWhitespace, "The category label must fit without clipping.");
                }
                model.SelectedSettingsCategory = SettingsCategory.Notifications;
                WaitForSettingsBranch(rail, (Button)view.FindName("GeneralCategoryButton"), 0);
                Assert.Equal(0, rail.ActiveIndex);
                view.Visibility = Visibility.Collapsed;
                model.SelectedSettingsCategory = SettingsCategory.Shortcuts;
                PumpAscii(TimeSpan.FromMilliseconds(30));
                Assert.False(rail.HasAnimatedProperties);
                view.Visibility = Visibility.Visible;
                WaitForSettingsBranch(rail, (Button)view.FindName("ShortcutsCategoryButton"), 2);
                Assert.Equal(2, rail.ActiveIndex);
                Assert.Equal(110, rail.CurrentCenter);
                Assert.Equal(20, rail.BranchReach);
            }
            finally
            {
                window.Content = null;
                if (view != null) view.DataContext = null;
                LocalizationService.Instance.CurrentLanguage = previousLanguage;
                Application.Current.Resources.MergedDictionaries.Remove(resources);
            }
        }));
    }

    private static void WaitForSettingsBranch(BranchedNavigationRail rail, Button button, int index)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        bool Settled() => rail.ActiveIndex == index && !rail.HasAnimatedProperties && rail.BranchReach == 20 &&
            rail.CurrentCenter == button.TranslatePoint(new Point(0, button.ActualHeight / 2), rail).Y;
        // Offscreen render clocks may start after the dispatcher processes the selection.
        while (!Settled() && timer.Elapsed < TimeSpan.FromSeconds(5))
            PumpAscii(TimeSpan.FromMilliseconds(20));
        Assert.True(Settled(), "The navigation branch must finish at the selected category.");
    }

    [Fact]
    public void SettingsBranches_KeepAContinuousTrunkWithoutADanglingFinalStroke()
    {
        RunInSta(() => WithAsciiWindow((window, _) =>
        {
            var rail = new BranchedNavigationRail
            {
                Width = 36, Height = 176, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                TrackBrush = Brushes.White, ActiveBrush = Brushes.Transparent
            };
            window.Content = rail;
            window.UpdateLayout();
            rail.UpdateBranches(new double[] { 22, 66, 110, 154 }, 0, animate: false);
            PumpAscii(TimeSpan.FromMilliseconds(30));
            var bitmap = new RenderTargetBitmap(36, 176, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(rail);
            var pixels = new byte[36 * 176 * 4];
            bitmap.CopyPixels(pixels, 36 * 4, 0);
            int Alpha(int x, int y) => pixels[(y * 36 + x) * 4 + 3];
            Assert.True(Alpha(12, 40) > 0);
            Assert.Equal(Alpha(12, 40), Alpha(12, 84));
            Assert.Equal(Alpha(12, 40), Alpha(12, 128));
            Assert.True(Alpha(24, 153) > 0);
            Assert.True(Alpha(12, 153) > 0);
            Assert.Equal(0, Alpha(12, 155));
            for (int y = 4; y < 154; y++)
                Assert.True(Alpha(12, y) > 0, "The trunk must connect every branch without gaps.");
        }));
    }

    [Fact]
    public void SettingsBranches_MoveContinuouslyInBothDirectionsAndSettleOnDetachment()
    {
        RunInSta(() => WithAsciiWindow((window, _) =>
        {
            var rail = new BranchedNavigationRail
            {
                Width = 36, Height = 176, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                TrackBrush = Brushes.Gray, ActiveBrush = Brushes.Blue
            };
            window.Content = rail;
            window.UpdateLayout();
            PumpAscii(TimeSpan.FromMilliseconds(30));
            double[] centers = { 22, 66, 110, 154 };
            rail.UpdateBranches(centers, 0, animate: false);
            Assert.Equal(4, rail.BranchCount);
            Assert.False(rail.IsHitTestVisible);
            Assert.False(rail.Focusable);
            Assert.Equal(0, VisualTreeHelper.GetChildrenCount(rail));
            rail.UpdateBranches(centers, 3, animate: true);
            if (SystemParameters.ClientAreaAnimation) Assert.Equal(22, rail.CurrentCenter);
            PumpAscii(TimeSpan.FromMilliseconds(100));
            if (SystemParameters.ClientAreaAnimation)
            {
                Assert.InRange(rail.CurrentCenter, 22.001, 153.999);
                Assert.InRange(rail.BranchReach, 0, 0.001);
            }
            PumpAscii(TimeSpan.FromMilliseconds(160));
            Assert.Equal(154, rail.CurrentCenter);
            rail.UpdateBranches(centers, 1, animate: true);
            if (SystemParameters.ClientAreaAnimation) Assert.Equal(154, rail.CurrentCenter);
            PumpAscii(TimeSpan.FromMilliseconds(100));
            if (SystemParameters.ClientAreaAnimation) Assert.InRange(rail.CurrentCenter, 66.001, 153.999);
            double currentCenter = rail.CurrentCenter;
            double currentReach = rail.BranchReach;
            rail.UpdateBranches(centers, 3, animate: true);
            if (SystemParameters.ClientAreaAnimation)
            {
                Assert.Equal(currentCenter, rail.CurrentCenter);
                Assert.Equal(currentReach, rail.BranchReach);
            }
            PumpAscii(TimeSpan.FromMilliseconds(260));
            Assert.Equal(3, rail.ActiveIndex);
            Assert.Equal(154, rail.CurrentCenter);
            Assert.Equal(20, rail.BranchReach);
            Assert.False(rail.HasAnimatedProperties);
            static byte[] Render(BranchedNavigationRail element)
            {
                element.UpdateLayout();
                var bitmap = new RenderTargetBitmap(36, 176, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(element);
                var pixels = new byte[36 * 176 * 4];
                bitmap.CopyPixels(pixels, 36 * 4, 0);
                return pixels;
            }
            byte[] blue = Render(rail);
            Assert.Contains(Enumerable.Range(0, blue.Length / 4), pixel => blue[pixel * 4] > blue[pixel * 4 + 2]);
            for (int y = 5; y < 148; y++)
            {
                int offset = (y * 36 + 12) * 4;
                Assert.True(blue[offset] > blue[offset + 2], "The active route must remain connected to the top of the trunk.");
            }
            rail.ActiveBrush = Brushes.Red;
            PumpAscii(TimeSpan.FromMilliseconds(30));
            Assert.False(blue.SequenceEqual(Render(rail)));
            double[] resizedCenters = { 26, 70, 114, 158 };
            rail.UpdateBranches(resizedCenters, 3, animate: false);
            Assert.Equal(158, rail.CurrentCenter);
            Assert.False(rail.HasAnimatedProperties);
            rail.UpdateBranches(resizedCenters, 0, animate: true);
            window.Content = null;
            PumpAscii(TimeSpan.FromMilliseconds(30));
            Assert.False(rail.HasAnimatedProperties);
            Assert.Equal(26, rail.CurrentCenter);
            Assert.Equal(20, rail.BranchReach);
            Assert.Equal(0, rail.ActiveIndex);
        }));
    }

    private static ResourceDictionary LoadBranchedSettingsResources()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Reepax", "App.xaml");
        var document = XDocument.Load(path);
        var resources = new XElement(document.Descendants().First(element => element.Name.LocalName == "ResourceDictionary"));
        foreach (var element in resources.DescendantsAndSelf())
        {
            if (element.Name.NamespaceName.StartsWith("clr-namespace:") && !element.Name.NamespaceName.Contains(";assembly="))
                element.Name = XName.Get(element.Name.LocalName, element.Name.NamespaceName + ";assembly=Reepax");
            if (element.Attribute("Source") is { } source)
                source.Value = "pack://application:,,,/Reepax;component/" + source.Value;
        }
        foreach (var attribute in document.Root!.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            resources.SetAttributeValue(attribute.Name, attribute.Value.StartsWith("clr-namespace:")
                ? attribute.Value + ";assembly=Reepax" : attribute.Value);
        return (ResourceDictionary)XamlReader.Parse(resources.ToString());
    }
}
