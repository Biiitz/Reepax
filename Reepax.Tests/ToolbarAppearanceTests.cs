using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Reepax.Services;
using Xunit;

namespace Reepax.Tests;

public partial class DialogUiTests
{
    [Theory]
    [InlineData(ThemeCatalog.DefaultId)]
    [InlineData(ThemeCatalog.MacOSId)]
    public void ToolbarBorders_RenderIdenticallyForEnabledAndDisabledButtons(string theme)
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            string previous = ThemeService.Instance.CurrentTheme.Id;
            try
            {
                ThemeService.Instance.SelectTheme(theme, save: false);
                var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Reepax", "MainWindow.xaml"));
                XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
                var markup = new XElement(document.Descendants().Single(element => (string?)element.Attribute(x + "Key") == "ToolbarSquareButtonStyle"));
                foreach (var element in markup.DescendantsAndSelf())
                    foreach (var attribute in element.Attributes().Where(attribute => attribute.Name.NamespaceName.StartsWith("clr-namespace:")).ToArray())
                    {
                        element.SetAttributeValue(XName.Get(attribute.Name.LocalName, attribute.Name.NamespaceName + ";assembly=Reepax"), attribute.Value);
                        attribute.Remove();
                    }
                foreach (var declaration in document.Root!.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
                    markup.SetAttributeValue(declaration.Name, declaration.Value.StartsWith("clr-namespace:")
                        ? declaration.Value + ";assembly=Reepax" : declaration.Value);
                var style = (Style)XamlReader.Parse(markup.ToString());
                var row = new StackPanel { Orientation = Orientation.Horizontal, Background = (Brush)Application.Current.FindResource("BgHeaderBrush") };
                foreach (bool enabled in new[] { true, false })
                    row.Children.Add(new Button
                    {
                        Style = style, IsEnabled = enabled, BorderThickness = new Thickness(1, 0, 1, 0),
                        Content = new Border { Width = 12, Height = 12, Background = (Brush)Application.Current.FindResource("TextPrimaryBrush") }
                    });
                row.Measure(new Size(76, 34));
                row.Arrange(new Rect(0, 0, 76, 34));
                row.UpdateLayout();
                var bitmap = new RenderTargetBitmap(76, 34, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(row);
                var pixels = new byte[76 * 34 * 4];
                bitmap.CopyPixels(pixels, 76 * 4, 0);
                byte[] Pixel(int px, int py) => pixels.AsSpan((py * 76 + px) * 4, 4).ToArray();
                for (int y = 2; y < 32; y++)
                {
                    Assert.Equal(Pixel(0, y), Pixel(37, y));
                    Assert.Equal(Pixel(0, y), Pixel(38, y));
                    Assert.Equal(Pixel(0, y), Pixel(75, y));
                }
                Assert.False(Pixel(19, 17).SequenceEqual(Pixel(57, 17)), "Disabled content must remain visibly dimmed.");
            }
            finally { ThemeService.Instance.SelectTheme(previous, save: false); }
        });
    }
}
