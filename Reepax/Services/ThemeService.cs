using System;
using System.Linq;
using System.Windows;
using Reepax.Services.Storage;

namespace Reepax.Services;

public class ThemeService
{
    private static readonly Lazy<ThemeService> _instance = new(() => new ThemeService());
    public static ThemeService Instance => _instance.Value;

    public event Action<bool>? ThemeChanged;
    public event Action<string>? AccentColorChanged;

    public const string FixedAccentColorHex = "#3B82F6";

    public bool IsDarkMode
    {
        get => true;
        set
        {
            // Dark mode only - cannot be changed
        }
    }

    private string _currentAccentColorHex = FixedAccentColorHex;
    public string CurrentAccentColorHex => _currentAccentColorHex;

    public void Initialize()
    {
        ApplyTheme(true, save: false);
        string? savedAccent = SettingsService.Instance.Settings.AccentColorHex;
        if (!string.IsNullOrWhiteSpace(savedAccent) && Helpers.ColorHelper.TryParseHex(savedAccent, out _))
        {
            ApplyAccentColor(savedAccent, save: false);
        }
        else
        {
            ApplyAccentColor(FixedAccentColorHex, save: false);
        }
    }

    public void ToggleTheme()
    {
        // No-op: only dark mode is available
    }

    public static System.Windows.Media.Color GetHoverColor(System.Windows.Media.Color color)
    {
        Helpers.ColorHelper.ToHsv(color, out double h, out double s, out double v);
        double hoverV = v > 0.25 ? Math.Max(0.1, v * 0.85) : Math.Min(1.0, v + 0.25);
        return Helpers.ColorHelper.FromHsv(h, s, hoverV);
    }

    public void SetAccentColor(string hexColor, bool save = true, double? hue = null, double? sat = null, double? val = null)
    {
        if (string.IsNullOrWhiteSpace(hexColor) || !Helpers.ColorHelper.TryParseHex(hexColor, out _))
        {
            hexColor = FixedAccentColorHex;
        }
        ApplyAccentColor(hexColor, save, hue, sat, val);
    }

    private void ApplyAccentColor(string hexColor, bool save, double? hue = null, double? sat = null, double? val = null)
    {
        var app = Application.Current;
        if (!Storage.DownloadPersistenceService.IsTestEnvironment &&
            app != null && app.Dispatcher != null &&
            app.Dispatcher.Thread?.IsAlive == true &&
            !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(() => ApplyAccentColor(hexColor, save, hue, sat, val));
            return;
        }

        try
        {
            if (!Helpers.ColorHelper.TryParseHex(hexColor, out var color))
            {
                hexColor = FixedAccentColorHex;
                Helpers.ColorHelper.TryParseHex(hexColor, out color);
            }

            _currentAccentColorHex = hexColor;
            var hoverColor = GetHoverColor(color);

            if (app != null)
            {
                var accentBrush = new System.Windows.Media.SolidColorBrush(color);
                var hoverBrush = new System.Windows.Media.SolidColorBrush(hoverColor);
                accentBrush.Freeze();
                hoverBrush.Freeze();

                app.Resources["AccentBlueColor"] = color;
                app.Resources["AccentBlueHoverColor"] = hoverColor;
                app.Resources["BorderHighlightColor"] = color;

                app.Resources["AccentBlueBrush"] = accentBrush;
                app.Resources["AccentBlueHoverBrush"] = hoverBrush;
                app.Resources["BorderHighlightBrush"] = accentBrush;
                var glowBrush1 = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(55, color.R, color.G, color.B));
                glowBrush1.Freeze();
                app.Resources["AccentGlowBrush1"] = glowBrush1;

                var glowBrush2 = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(28, color.R, color.G, color.B));
                glowBrush2.Freeze();
                app.Resources["AccentGlowBrush2"] = glowBrush2;

                var selectionBg = new System.Windows.Media.LinearGradientBrush
                {
                    StartPoint = new System.Windows.Point(0, 0),
                    EndPoint = new System.Windows.Point(0, 1)
                };
                selectionBg.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromArgb(77, color.R, color.G, color.B), 0.0));
                selectionBg.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromArgb(30, color.R, color.G, color.B), 0.3));
                selectionBg.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromArgb(24, color.R, color.G, color.B), 0.5));
                selectionBg.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromArgb(30, color.R, color.G, color.B), 0.7));
                selectionBg.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromArgb(77, color.R, color.G, color.B), 1.0));
                selectionBg.Freeze();
                app.Resources["AccentSelectionBgBrush"] = selectionBg;
            }

            if (save)
            {
                var settings = SettingsService.Instance.Settings;
                settings.AccentColorHex = hexColor;
                if (hue.HasValue && sat.HasValue && val.HasValue)
                {
                    settings.AccentColorHue = hue.Value;
                    settings.AccentColorSaturation = sat.Value;
                    settings.AccentColorValue = val.Value;
                }
                else
                {
                    Helpers.ColorHelper.ToHsv(color, settings.AccentColorHue ?? 217.22, out double h, out double s, out double v);
                    settings.AccentColorHue = h;
                    settings.AccentColorSaturation = s;
                    settings.AccentColorValue = v;
                }
                SettingsService.Instance.RequestSaveSettings();
            }

            AccentColorChanged?.Invoke(hexColor);
        }
        catch (Exception ex)
        {
            Storage.AppLogger.Error($"[ThemeService] Error applying accent color {hexColor}", ex);
        }
    }

    public void ApplyTheme(bool isDark, bool save = true)
    {
        var app = Application.Current;
        if (!Storage.DownloadPersistenceService.IsTestEnvironment &&
            app != null && app.Dispatcher != null &&
            app.Dispatcher.Thread?.IsAlive == true &&
            !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(() => ApplyTheme(isDark, save));
            return;
        }

        try
        {
            if (app != null)
            {
                var themeUri = new Uri("Themes/DarkTheme.xaml", UriKind.Relative);
                var newDict = new ResourceDictionary { Source = themeUri };

                // Find and replace existing theme dictionary
                var existingTheme = app.Resources.MergedDictionaries.FirstOrDefault(d => 
                    d.Source != null && (d.Source.OriginalString.Contains("DarkTheme") || d.Source.OriginalString.Contains("LightTheme")));

                if (existingTheme != null)
                {
                    var index = app.Resources.MergedDictionaries.IndexOf(existingTheme);
                    app.Resources.MergedDictionaries.RemoveAt(index);
                    app.Resources.MergedDictionaries.Insert(index, newDict);
                }
                else
                {
                    app.Resources.MergedDictionaries.Add(newDict);
                }

                // Explicitly update root application resource keys for immediate WPF re-evaluation
                foreach (var key in newDict.Keys)
                {
                    app.Resources[key] = newDict[key];
                }
            }

            if (save)
            {
                SettingsService.Instance.Settings.IsDarkMode = true;
                SettingsService.Instance.Settings.EnableForcedDarkMode = true;
                SettingsService.Instance.SaveSettings();
            }

            ApplyAccentColor(_currentAccentColorHex, save: false);

            // Update title bars for all open windows
            UpdateAllOpenWindowsTitleBar(true);

            ThemeChanged?.Invoke(true);
        }
        catch (Exception ex)
        {
            Storage.AppLogger.Error("[ThemeService] Error applying theme", ex);
        }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public static void ApplyDarkTitleBar(Window? window, bool isDark = true)
    {
        if (window == null) return;

        if (window.Dispatcher != null && !window.Dispatcher.CheckAccess())
        {
            window.Dispatcher.Invoke(() => ApplyDarkTitleBar(window, isDark));
            return;
        }

        void UpdateHwnd()
        {
            try
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(window);
                var hwnd = helper.Handle;
                if (hwnd != IntPtr.Zero)
                {
                    int mode = isDark ? 1 : 0;
                    // 20: DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 20H1+ / Windows 11)
                    // 19: DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 (Windows 10 1809-1909)
                    if (DwmSetWindowAttribute(hwnd, 20, ref mode, sizeof(int)) != 0)
                    {
                        DwmSetWindowAttribute(hwnd, 19, ref mode, sizeof(int));
                    }

                    // Windows 11 title bar caption and text coloring (0x00BBGGRR format)
                    int captionColor = isDark ? 0x00161616 : 0x00F9F5F1;
                    DwmSetWindowAttribute(hwnd, 35, ref captionColor, sizeof(int));

                    int textColor = isDark ? 0x00F6F4F3 : 0x002A170F;
                    DwmSetWindowAttribute(hwnd, 36, ref textColor, sizeof(int));

                    int borderColor = isDark ? 0x002A2A2A : 0x00E5E7EB;
                    DwmSetWindowAttribute(hwnd, 34, ref borderColor, sizeof(int));

                    // Windows 11 DWMWA_WINDOW_CORNER_PREFERENCE
                    int cornerPreference = 0;
                    DwmSetWindowAttribute(hwnd, 33, ref cornerPreference, sizeof(int));
                }
            }
            catch { }
        }

        if (window.IsLoaded)
        {
            UpdateHwnd();
        }
        else
        {
            EventHandler? sourceInitHandler = null;
            sourceInitHandler = (s, e) =>
            {
                window.SourceInitialized -= sourceInitHandler;
                UpdateHwnd();
            };
            window.SourceInitialized += sourceInitHandler;

            RoutedEventHandler? loadedHandler = null;
            loadedHandler = (s, e) =>
            {
                window.Loaded -= loadedHandler;
                UpdateHwnd();
            };
            window.Loaded += loadedHandler;
        }
    }

    private static void UpdateAllOpenWindowsTitleBar(bool isDark)
    {
        try
        {
            var app = Application.Current;
            if (app != null)
            {
                if (!Storage.DownloadPersistenceService.IsTestEnvironment &&
                    app.Dispatcher != null &&
                    app.Dispatcher.Thread?.IsAlive == true &&
                    !app.Dispatcher.CheckAccess())
                {
                    app.Dispatcher.Invoke(() => UpdateAllOpenWindowsTitleBar(isDark));
                    return;
                }

                var windows = app.Windows.Cast<Window>().ToArray();
                foreach (var window in windows)
                {
                    ApplyDarkTitleBar(window, isDark);
                }
            }
        }
        catch { }
    }
}
