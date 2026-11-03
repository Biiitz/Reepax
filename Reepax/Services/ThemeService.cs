using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Reepax.Services.Storage;

namespace Reepax.Services;

public partial class ThemeService
{
    private static readonly Lazy<ThemeService> _instance = new(() => new ThemeService());
    public static ThemeService Instance => _instance.Value;

    public event Action<bool>? ThemeChanged;
    public event Action<string>? AccentColorChanged;

    public const string FixedAccentColorHex = "#3B82F6";
    private readonly HashSet<object> _themeResourceKeys = new();
    public ThemeDefinition CurrentTheme { get; private set; } = ThemeCatalog.Resolve(ThemeCatalog.DefaultId);
    public bool SupportsCustomAccent => CurrentTheme.SupportsCustomAccent;

    public bool IsDarkMode
    {
        get => CurrentTheme.IsDark;
        set
        {
            // Legacy callers do not select a theme through the dark-mode flag.
        }
    }

    private string _currentAccentColorHex = FixedAccentColorHex;
    private string _customAccentColorHex = FixedAccentColorHex;
    public string CurrentAccentColorHex => _currentAccentColorHex;

    public void Initialize()
    {
        var saved = SettingsService.Instance.Settings.AccentColorHex;
        _customAccentColorHex = Helpers.ColorHelper.TryParseHex(saved, out _) ? saved : FixedAccentColorHex;
        SelectTheme(SettingsService.Instance.Settings.ThemeId, save: false);
    }

    public void ToggleTheme()
    {
        // Theme selection is explicit so future themes do not change this legacy action.
    }

    public static System.Windows.Media.Color GetHoverColor(System.Windows.Media.Color color)
    {
        Helpers.ColorHelper.ToHsv(color, out double h, out double s, out double v);
        double hoverV = v > 0.25 ? Math.Max(0.1, v * 0.85) : Math.Min(1.0, v + 0.25);
        return Helpers.ColorHelper.FromHsv(h, s, hoverV);
    }

    public void SetAccentColor(string hexColor, bool save = true, double? hue = null, double? sat = null, double? val = null)
    {
        if (!SupportsCustomAccent) return;
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
            _customAccentColorHex = hexColor;
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
                app.Resources["ChoiceBorderBrush"] = accentBrush;
                app.Resources["AccentBlueHoverBrush"] = hoverBrush;
                var contrastBrush = new System.Windows.Media.SolidColorBrush(Helpers.ColorHelper.GetContrastForeground(color));
                contrastBrush.Freeze();
                app.Resources["AccentForegroundBrush"] = contrastBrush;
                var hoverContrastBrush = new System.Windows.Media.SolidColorBrush(Helpers.ColorHelper.GetContrastForeground(hoverColor));
                hoverContrastBrush.Freeze();
                app.Resources["AccentHoverForegroundBrush"] = hoverContrastBrush;
                app.Resources["BorderHighlightBrush"] = accentBrush;
                var glowBrush1 = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(44, color.R, color.G, color.B));
                glowBrush1.Freeze();
                app.Resources["AccentGlowBrush1"] = glowBrush1;

                var glowBrush2 = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(20, color.R, color.G, color.B));
                glowBrush2.Freeze();
                app.Resources["AccentGlowBrush2"] = glowBrush2;

                var selectionBg = new System.Windows.Media.LinearGradientBrush
                {
                    StartPoint = new System.Windows.Point(0, 0),
                    EndPoint = new System.Windows.Point(0, 1)
                };
                selectionBg.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromArgb(77, color.R, color.G, color.B), 0.0));
                selectionBg.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromArgb(23, color.R, color.G, color.B), 0.3));
                selectionBg.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromArgb(16, color.R, color.G, color.B), 0.5));
                selectionBg.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromArgb(23, color.R, color.G, color.B), 0.7));
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

    public void ApplyTheme(bool isDark, bool save = true) => SelectTheme(CurrentTheme.Id, save);

    public void SelectTheme(string? themeId, bool save = true)
    {
        var app = Application.Current;
        if (!DownloadPersistenceService.IsTestEnvironment && app?.Dispatcher is { } dispatcher &&
            dispatcher.Thread.IsAlive && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => SelectTheme(themeId, save));
            return;
        }
        var theme = ThemeCatalog.Resolve(themeId);
        var dictionary = new ResourceDictionary
        {
            Source = new Uri("/Reepax;component/Themes/" + theme.DictionaryPath, UriKind.Relative)
        };
        if (app != null)
        {
            var existing = app.Resources.MergedDictionaries.FirstOrDefault(candidate =>
                candidate.Source != null && ThemeCatalog.Themes.Any(definition =>
                    candidate.Source.OriginalString.EndsWith(definition.DictionaryPath, StringComparison.OrdinalIgnoreCase)));
            int index = existing == null ? app.Resources.MergedDictionaries.Count : app.Resources.MergedDictionaries.IndexOf(existing);
            if (existing != null) app.Resources.MergedDictionaries.RemoveAt(index);
            app.Resources.MergedDictionaries.Insert(index, dictionary);
            foreach (var key in _themeResourceKeys) app.Resources.Remove(key);
            _themeResourceKeys.Clear();
            CopyResources(dictionary, app.Resources);
        }
        CurrentTheme = theme;
        var settings = SettingsService.Instance.Settings;
        if (theme.SupportsCustomAccent)
        {
            ApplyAccentColor(_customAccentColorHex, save: false);
        }
        else
        {
            _currentAccentColorHex = theme.AccentColor;
            AccentColorChanged?.Invoke(_currentAccentColorHex);
        }
        if (save)
        {
            settings.ThemeId = theme.Id;
            settings.IsDarkMode = theme.IsDark;
            SettingsService.Instance.RequestSaveSettings();
        }
        UpdateAllOpenWindowsTitleBar(theme.IsDark);
        RefreshStatusBindings();
        ThemeChanged?.Invoke(theme.IsDark);
    }

    private void CopyResources(ResourceDictionary source, ResourceDictionary target)
    {
        foreach (var merged in source.MergedDictionaries) CopyResources(merged, target);
        foreach (var key in source.Keys)
        {
            target[key] = source[key];
            _themeResourceKeys.Add(key);
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
                    int captionColor = isDark ? 0x00161616 : 0x00F3F1F1;
                    DwmSetWindowAttribute(hwnd, 35, ref captionColor, sizeof(int));

                    int textColor = isDark ? 0x00F6F4F3 : 0x001F1D1D;
                    DwmSetWindowAttribute(hwnd, 36, ref textColor, sizeof(int));

                    int borderColor = isDark ? 0x002A2A2A : 0x00D6D1D1;
                    DwmSetWindowAttribute(hwnd, 34, ref borderColor, sizeof(int));

                    // Windows 11 DWMWA_WINDOW_CORNER_PREFERENCE
                    int cornerPreference = isDark ? 0 : 2;
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
