using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Reepax.Helpers;

internal sealed class AsciiGlyphAtlas
{
    internal const string Characters = "01";
    internal const double CellSize = 20;
    internal const double ImageSize = 34;
    internal ImageSource[] Sharp { get; } = new ImageSource[Characters.Length];
    internal ImageSource[] Soft { get; } = new ImageSource[Characters.Length];
    internal ImageSource[] Glow { get; } = new ImageSource[Characters.Length];

    internal AsciiGlyphAtlas(double pixelsPerDip, Color? glyphColor = null)
    {
        var color = glyphColor ?? Colors.White;
        var foreground = new SolidColorBrush(color);
        foreground.Freeze();
        int size = Math.Max(1, (int)Math.Ceiling(ImageSize * pixelsPerDip));
        for (int index = 0; index < Characters.Length; index++)
        {
            var text = new FormattedText(Characters[index].ToString(), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Consolas"), 19, foreground, pixelsPerDip);
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
                context.DrawText(text, new Point((ImageSize - text.WidthIncludingTrailingWhitespace) / 2, (ImageSize - text.Height) / 2));
            var bitmap = new RenderTargetBitmap(size, size, 96 * pixelsPerDip, 96 * pixelsPerDip, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            Sharp[index] = bitmap;
            var pixels = new byte[size * size * 4];
            bitmap.CopyPixels(pixels, size * 4, 0);
            Soft[index] = Blur(pixels, size, Math.Max(1, (int)Math.Round(pixelsPerDip)), color);
            Glow[index] = Blur(pixels, size, Math.Max(2, (int)Math.Round(2 * pixelsPerDip)), color);
        }
    }

    private static BitmapSource Blur(byte[] source, int size, int radius, Color color)
    {
        var output = new byte[source.Length];
        var horizontal = new int[size * size];
        int diameter = radius * 2 + 1;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int sum = 0;
            for (int offset = -radius; offset <= radius; offset++)
            {
                int sample = x + offset;
                if (sample >= 0 && sample < size) sum += source[(y * size + sample) * 4 + 3];
            }
            horizontal[y * size + x] = sum;
        }
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int sum = 0;
            for (int offset = -radius; offset <= radius; offset++)
            {
                int sample = y + offset;
                if (sample >= 0 && sample < size) sum += horizontal[sample * size + x];
            }
            byte alpha = (byte)(sum / (diameter * diameter));
            int position = (y * size + x) * 4;
            // Cached soft edges retain premultiplied alpha for either palette.
            output[position] = (byte)(color.B * alpha / 255);
            output[position + 1] = (byte)(color.G * alpha / 255);
            output[position + 2] = (byte)(color.R * alpha / 255);
            output[position + 3] = alpha;
        }
        var image = BitmapSource.Create(size, size, 96 * size / ImageSize, 96 * size / ImageSize,
            PixelFormats.Pbgra32, null, output, size * 4);
        image.Freeze();
        return image;
    }
}
