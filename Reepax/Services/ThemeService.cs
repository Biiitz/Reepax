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

    public string CurrentAccentColorHex => FixedAccentColorHex;

    public void Initialize()
    {
        ApplyTheme(true, save: false);
    }

    public void ToggleTheme()
    {
        // No-op: only dark mode is available
    }

    public void SetAccentColor(string hexColor, bool save = true)
    {
        // No-op: accent color is fixed to #3B82F6
        ApplyAccentColor(FixedAccentColorHex, save: false);
    }

    private void ApplyAccentColor(string hexColor, bool save)
    {
        var app = Application.Current;
        if (app != null && app.Dispatcher != null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(() => ApplyAccentColor(hexColor, save));
            return;
        }

        try
        {
            // Fixed accent color: #3B82F6 (Blue 500)
            var color = System.Windows.Media.Color.FromRgb(59, 130, 246);
            var hoverColor = System.Windows.Media.Color.FromRgb(37, 99, 235);

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
            }

            if (save)
            {
                SettingsService.Instance.Settings.AccentColorHex = FixedAccentColorHex;
                SettingsService.Instance.SaveSettings();
            }

            AccentColorChanged?.Invoke(FixedAccentColorHex);
        }
        catch (Exception ex)
        {
            Storage.AppLogger.Error($"[ThemeService] Error applying accent color {FixedAccentColorHex}", ex);
        }
    }

    public void ApplyTheme(bool isDark, bool save = true)
    {
        var app = Application.Current;
        if (app != null && app.Dispatcher != null && !app.Dispatcher.CheckAccess())
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
                SettingsService.Instance.Settings.AccentColorHex = FixedAccentColorHex;
                SettingsService.Instance.SaveSettings();
            }

            ApplyAccentColor(FixedAccentColorHex, save: false);

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
                if (app.Dispatcher != null && !app.Dispatcher.CheckAccess())
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
