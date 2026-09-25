using System;
using System.Windows.Media;

namespace Reepax.Helpers;

public static class ColorHelper
{
    public static Color FromHsv(double hue, double saturation, double value)
    {
        hue = Math.Clamp(hue, 0, 360);
        if (hue >= 360) hue = 0;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);

        int hi = Convert.ToInt32(Math.Floor(hue / 60.0)) % 6;
        double f = hue / 60.0 - Math.Floor(hue / 60.0);

        double v = value * 255.0;
        byte vByte = (byte)Math.Clamp(v, 0, 255);
        byte pByte = (byte)Math.Clamp(v * (1.0 - saturation), 0, 255);
        byte qByte = (byte)Math.Clamp(v * (1.0 - f * saturation), 0, 255);
        byte tByte = (byte)Math.Clamp(v * (1.0 - (1.0 - f) * saturation), 0, 255);

        return hi switch
        {
            0 => Color.FromRgb(vByte, tByte, pByte),
            1 => Color.FromRgb(qByte, vByte, pByte),
            2 => Color.FromRgb(pByte, vByte, tByte),
            3 => Color.FromRgb(pByte, qByte, vByte),
            4 => Color.FromRgb(tByte, pByte, vByte),
            _ => Color.FromRgb(vByte, pByte, qByte)
        };
    }

    public static void ToHsv(Color color, out double hue, out double saturation, out double value)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        hue = 0;
        if (delta > 0.00001)
        {
            if (Math.Abs(max - r) < 0.00001)
                hue = (g - b) / delta % 6;
            else if (Math.Abs(max - g) < 0.00001)
                hue = (b - r) / delta + 2;
            else
                hue = (r - g) / delta + 4;

            hue *= 60;
            if (hue < 0) hue += 360;
        }

        saturation = max < 0.00001 ? 0 : delta / max;
        value = max;
    }

    public static string ToHex(Color color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    public static bool TryParseHex(string hex, out Color color)
    {
        color = Colors.Transparent;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        hex = hex.Trim();
        if (!hex.StartsWith("#")) hex = "#" + hex;
        if (hex.Length != 7 && hex.Length != 9) return false;

        try
        {
            color = (Color)ColorConverter.ConvertFromString(hex);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
