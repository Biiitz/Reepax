using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Reepax.Helpers;

internal sealed class LogoOutlineArt
{
    private static readonly Lazy<LogoOutlineArt> Shared = new(() => new LogoOutlineArt(
        new BitmapImage(new Uri("pack://application:,,,/Reepax;component/Assets/R.png"))));
    internal static LogoOutlineArt Default => Shared.Value;
    internal BitmapSource Image { get; }
    internal Geometry Outline { get; }

    internal LogoOutlineArt(BitmapSource source)
    {
        Image = source.CloneCurrentValue();
        Image.Freeze();
        var bitmap = new FormatConvertedBitmap(Image, PixelFormats.Bgra32, null, 0);
        int width = bitmap.PixelWidth;
        int height = bitmap.PixelHeight;
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        var runs = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var context = runs.Open())
        {
            for (int row = 0; row < height; row++)
            {
                int column = 0;
                while (column < width)
                {
                    // Exclude the bitmap's translucent shadow from the technical outline.
                    while (column < width && pixels[(row * width + column) * 4 + 3] < 200) column++;
                    int start = column;
                    while (column < width && pixels[(row * width + column) * 4 + 3] >= 200) column++;
                    if (start == column) continue;
                    context.BeginFigure(new Point(start, row), isFilled: true, isClosed: true);
                    context.LineTo(new Point(column, row), isStroked: true, isSmoothJoin: false);
                    context.LineTo(new Point(column, row + 1), isStroked: true, isSmoothJoin: false);
                    context.LineTo(new Point(start, row + 1), isStroked: true, isSmoothJoin: false);
                }
            }
        }
        Outline = runs.GetOutlinedPathGeometry();
        Outline.Freeze();
    }
}
