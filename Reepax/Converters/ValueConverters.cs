using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Reepax.Models;
using Reepax.Services;
using Reepax.Services.Localization;

namespace Reepax.Converters;

public class BytesToHumanReadableConverter : IValueConverter
{
    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double len = bytes;
        while (len >= 1024 && order < suffixes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {suffixes[order]}";
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not long bytes || bytes <= 0)
        {
            if (parameter is string p && (p == "zero" || p == "allowZero"))
                return "0 B";
            return "--";
        }

        return FormatBytes(bytes);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class SpeedToHumanReadableConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double speed = 0;
        if (value is double d) speed = d;
        else if (value is float f) speed = f;
        else if (value is long l) speed = l;
        else if (value is int i) speed = i;
        else if (value != null && double.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed)) speed = parsed;
        else return "0 KB/s";

        if (double.IsNaN(speed) || double.IsInfinity(speed) || speed <= 0)
            return "0 KB/s";

        string[] suffixes = { "B/s", "KB/s", "MB/s", "GB/s", "TB/s" };
        int order = 0;
        double len = speed;
        while (len >= 1024 && order < suffixes.Length - 1)
        {
            order++;
            len /= 1024;
        }

        return $"{len.ToString("0.##", culture ?? CultureInfo.InvariantCulture)} {suffixes[order]}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class SecondsToEtaConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double seconds || seconds <= 0 || double.IsInfinity(seconds) || double.IsNaN(seconds) || seconds > 315360000)
            return "--:--";

        try
        {
            var time = TimeSpan.FromSeconds(seconds);
            if (time.TotalHours >= 1)
            {
                return $"{(int)time.TotalHours:D2}:{time.Minutes:D2}:{time.Seconds:D2}";
            }

            return $"{time.Minutes:D2}:{time.Seconds:D2}";
        }
        catch
        {
            return "--:--";
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class StatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DownloadStatus status)
        {
            SolidColorBrush brush = status switch
            {
                DownloadStatus.Completed => new SolidColorBrush(Color.FromRgb(16, 185, 129)),      // Emerald Green
                DownloadStatus.Downloading => new SolidColorBrush(Color.FromRgb(59, 130, 246)),    // Blue
                DownloadStatus.InBrowser or DownloadStatus.InBrowserSlot1 or DownloadStatus.InBrowserSlot2 or DownloadStatus.SolvingCaptcha 
                                           => new SolidColorBrush(Color.FromRgb(245, 158, 11)),   // Amber/Orange
                DownloadStatus.Paused => new SolidColorBrush(Color.FromRgb(156, 163, 175)),        // Gray
                DownloadStatus.Failed => new SolidColorBrush(Color.FromRgb(239, 68, 68)),          // Red
                DownloadStatus.Queued or DownloadStatus.WaitingForBrowser 
                                           => new SolidColorBrush(Color.FromRgb(209, 213, 219)),   // Light Gray
                _ => new SolidColorBrush(Color.FromRgb(160, 160, 160))
            };
            brush.Freeze();
            return brush;
        }

        return new SolidColorBrush(Color.FromRgb(160, 160, 160));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class FileExtensionIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return SystemIconService.GetIconForFile(value?.ToString());
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class PackageIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return SystemIconService.GetZipArchiveIcon();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class HosterImageConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string? input = null;
        if (value is DownloadItem item)
        {
            input = !string.IsNullOrWhiteSpace(item.HosterName) && !item.HosterName.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
                ? item.HosterName
                : (!string.IsNullOrWhiteSpace(item.OriginalUrl) ? item.OriginalUrl : item.DirectDownloadUrl);
        }
        else if (value is DownloadPackage package)
        {
            input = package.PrimaryHosterName;
        }
        else
        {
            input = (parameter != null && !string.IsNullOrWhiteSpace(parameter.ToString()))
                ? parameter.ToString()
                : value?.ToString();
        }

        return HosterIconService.GetIconForHoster(input);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class DoubleToGridLengthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isStar = parameter is string s && s.Equals("star", StringComparison.OrdinalIgnoreCase);

        if (value is double d)
        {
            if (d <= 0 || double.IsNaN(d) || double.IsInfinity(d)) return new GridLength(0);
            return isStar ? new GridLength(1, GridUnitType.Star) : new GridLength(d);
        }
        return isStar ? new GridLength(1, GridUnitType.Star) : new GridLength(100);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is GridLength gl)
        {
            return gl.Value;
        }
        return 100.0;
    }
}

public class StatusToPlayPauseGeometryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DownloadStatus status)
        {
            if (status == DownloadStatus.Completed)
            {
                return null;
            }

            if (status is DownloadStatus.Downloading or DownloadStatus.InBrowser or DownloadStatus.InBrowserSlot1 or DownloadStatus.InBrowserSlot2 or DownloadStatus.SolvingCaptcha)
            {
                return Application.Current?.TryFindResource("IconPause");
            }
        }
        return Application.Current?.TryFindResource("IconPlay");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class StatusToPlayPauseTooltipConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DownloadStatus status)
        {
            if (status == DownloadStatus.Completed)
            {
                return string.Empty;
            }

            if (status is DownloadStatus.Downloading or DownloadStatus.InBrowser or DownloadStatus.InBrowserSlot1 or DownloadStatus.InBrowserSlot2 or DownloadStatus.SolvingCaptcha)
            {
                return Loc.Get("Common_Pause");
            }
        }
        return Loc.Get("Common_ResumeStart");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class ItemActionMenuHeaderConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DownloadItem item)
        {
            if (item.Status == DownloadStatus.Completed)
            {
                return string.Empty;
            }

            var msg = item.StatusMessage ?? string.Empty;
            bool isBrowserClosed = msg == Loc.Get("Status_BrowserWindowClosed") ||
                                   msg.Contains("Browser window closed", StringComparison.OrdinalIgnoreCase) ||
                                   msg.Contains("Browser-Fenster geschlossen", StringComparison.OrdinalIgnoreCase);

            if (isBrowserClosed || item.Status == DownloadStatus.Failed)
            {
                return Loc.Get("Menu_Retry");
            }

            if (item.Status is DownloadStatus.Downloading or DownloadStatus.InBrowser or DownloadStatus.InBrowserSlot1 or DownloadStatus.InBrowserSlot2 or DownloadStatus.SolvingCaptcha)
            {
                return Loc.Get("Menu_Pause");
            }

            if (item.Status == DownloadStatus.Completed)
            {
                return Loc.Get("Menu_RestartDownload");
            }

            return Loc.Get("Menu_Resume");
        }

        return Loc.Get("Menu_Resume");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class ItemActionMenuGeometryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DownloadItem item)
        {
            if (item.Status == DownloadStatus.Completed)
            {
                return null;
            }

            var msg = item.StatusMessage ?? string.Empty;
            bool isBrowserClosed = msg == Loc.Get("Status_BrowserWindowClosed") ||
                                   msg.Contains("Browser window closed", StringComparison.OrdinalIgnoreCase) ||
                                   msg.Contains("Browser-Fenster geschlossen", StringComparison.OrdinalIgnoreCase);

            if (isBrowserClosed || item.Status == DownloadStatus.Failed || item.Status == DownloadStatus.Completed)
            {
                return Application.Current?.TryFindResource("IconRefresh");
            }

            if (item.Status is DownloadStatus.Downloading or DownloadStatus.InBrowser or DownloadStatus.InBrowserSlot1 or DownloadStatus.InBrowserSlot2 or DownloadStatus.SolvingCaptcha)
            {
                return Application.Current?.TryFindResource("IconPause");
            }

            return Application.Current?.TryFindResource("IconPlay");
        }

        return Application.Current?.TryFindResource("IconPlay");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (parameter is string paramStr && paramStr.StartsWith('!'))
        {
            var target = paramStr[1..];
            bool matches = string.Equals(value?.ToString(), target, StringComparison.OrdinalIgnoreCase);
            return matches ? Visibility.Collapsed : Visibility.Visible;
        }

        if (value == null && parameter == null) return Visibility.Visible;
        if (value != null && parameter != null)
        {
            var valStr = value.ToString()!;
            var paramString = parameter.ToString()!;
            if (paramString.Contains('|'))
            {
                var parts = paramString.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Any(p => string.Equals(valStr, p, StringComparison.OrdinalIgnoreCase)))
                {
                    return Visibility.Visible;
                }
            }
            else if (string.Equals(valStr, paramString, StringComparison.OrdinalIgnoreCase))
            {
                return Visibility.Visible;
            }
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class EqualsToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value != null && parameter != null && value.ToString() == parameter.ToString())
        {
            if (Application.Current?.TryFindResource("AccentBlueBrush") is Brush brush)
            {
                return brush;
            }
            if (Helpers.ColorHelper.TryParseHex(ThemeService.Instance.CurrentAccentColorHex, out var col))
            {
                return new SolidColorBrush(col);
            }
            return new SolidColorBrush(Color.FromRgb(59, 130, 246));
        }
        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && !b)
            return 0.45;
        return 1.0;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class BoolToActiveTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return (value is bool b && b) ? Loc.Get("Common_Enabled") : Loc.Get("Common_Disabled");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class BoolToActiveBackgroundConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isDark = ThemeService.Instance.IsDarkMode;
        SolidColorBrush brush;
        if (value is bool b && b)
        {
            brush = isDark
                ? new SolidColorBrush(Color.FromRgb(28, 39, 58))    // #1C273A Dark Navy
                : new SolidColorBrush(Color.FromRgb(219, 234, 254)); // #DBEAFE Light Soft Blue
        }
        else
        {
            brush = isDark
                ? new SolidColorBrush(Color.FromRgb(32, 32, 32))    // #202020 Dark Gray
                : new SolidColorBrush(Color.FromRgb(241, 245, 249)); // #F1F5F9 Slate-100 Light
        }
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class BoolToActiveBorderConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isDark = ThemeService.Instance.IsDarkMode;
        if (value is bool b && b)
        {
            if (Application.Current?.TryFindResource("AccentBlueBrush") is Brush brush)
            {
                return brush;
            }
            if (Helpers.ColorHelper.TryParseHex(ThemeService.Instance.CurrentAccentColorHex, out var col))
            {
                var customBrush = new SolidColorBrush(col);
                customBrush.Freeze();
                return customBrush;
            }
            var defaultAccent = new SolidColorBrush(Color.FromRgb(59, 130, 246));
            defaultAccent.Freeze();
            return defaultAccent;
        }

        var borderBrush = isDark
            ? new SolidColorBrush(Color.FromRgb(56, 56, 56))    // #383838 Border
            : new SolidColorBrush(Color.FromRgb(203, 213, 225)); // #CBD5E1 Slate-300 Light Border
        borderBrush.Freeze();
        return borderBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class BoolToActiveForegroundConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isDark = ThemeService.Instance.IsDarkMode;
        var brush = isDark
            ? new SolidColorBrush(Color.FromRgb(255, 255, 255)) // White text
            : new SolidColorBrush(Color.FromRgb(15, 23, 42));   // Dark text
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is bool b && !b;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is bool b && !b;
    }
}

public class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
            return b ? Visibility.Collapsed : Visibility.Visible;
        return Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Visibility v)
            return v != Visibility.Visible;
        return false;
    }
}

public class SpeedLimitDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double mbps = 0;
        if (value is int i) mbps = i;
        else if (value is double d) mbps = d;
        else if (value is float f) mbps = f;
        else if (value is long l) mbps = l;
        else if (value != null && double.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed)) mbps = parsed;

        if (double.IsNaN(mbps) || double.IsInfinity(mbps) || mbps <= 0)
            return Loc.Get("SpeedLimit_Unlimited");

        if (mbps >= 1000)
            return $"{mbps.ToString("0.##", culture ?? CultureInfo.InvariantCulture)} MB/s ({(mbps / 1000.0).ToString("0.#", culture ?? CultureInfo.InvariantCulture)} GB/s)";

        return $"{mbps.ToString("0.##", culture ?? CultureInfo.InvariantCulture)} MB/s";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string str)
        {
            var match = System.Text.RegularExpressions.Regex.Match(str, @"[\d.]+");
            if (match.Success && double.TryParse(match.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double result))
            {
                if (targetType == typeof(int))
                    return (int)Math.Round(result);
                return result;
            }
        }
        else if (value is int i) return (targetType == typeof(double)) ? (object)(double)i : (object)i;
        else if (value is double d) return (targetType == typeof(int)) ? (object)(int)Math.Round(d) : (object)d;
        return (targetType == typeof(double)) ? (object)0.0 : (object)0;
    }
}

public class IntToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int intVal && parameter is string paramStr && int.TryParse(paramStr, out int targetVal))
        {
            return intVal == targetVal;
        }
        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b && parameter is string paramStr && int.TryParse(paramStr, out int targetVal))
        {
            return targetVal;
        }
        return Binding.DoNothing;
    }
}

public class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int count && count > 0)
            return Visibility.Visible;
        if (value is string s && !string.IsNullOrWhiteSpace(s))
            return Visibility.Visible;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            if (!hex.StartsWith('#')) hex = "#" + hex;
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
            catch
            {
                // fallback
            }
        }
        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class BoolToRightBorderThicknessConverter : IValueConverter
{
    private static readonly Thickness DividerThickness = new(0, 0, 1, 0);
    private static readonly Thickness ZeroThickness = new(0, 0, 0, 0);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b)
            return DividerThickness;
        return ZeroThickness;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}


