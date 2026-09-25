using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Reepax.Services;
using Reepax.Services.Browser;
using Reepax.Services.Storage;

namespace Reepax.Views;

/// <summary>
/// Options or popup window for a browser extension. Runs in the same shared
/// WebView2 environment as browser windows so extension settings
/// are persistently saved in the profile.
/// </summary>
public partial class ExtensionOptionsWindow : Window
{
    private readonly BrowserExtensionInfo _extension;
    private readonly string _targetUrl;

    public ExtensionOptionsWindow(BrowserExtensionInfo extension, string? targetUrl = null)
    {
        InitializeComponent();
        ThemeService.ApplyDarkTitleBar(this, ThemeService.Instance.IsDarkMode);
        ThemeService.Instance.ThemeChanged += UpdateTitleBarTheme;

        _extension = extension;
        _targetUrl = !string.IsNullOrWhiteSpace(targetUrl) ? targetUrl : extension.PopupOrOptionsUrl;

        bool isPopup = !string.IsNullOrWhiteSpace(extension.PopupPageUrl) &&
                       string.Equals(_targetUrl, extension.PopupPageUrl, StringComparison.OrdinalIgnoreCase);

        Title = isPopup 
            ? extension.Name 
            : Services.Localization.Loc.Format("ExtensionOptions_Title", extension.Name);

        ExtensionNameText.Text = extension.Name;

        if (isPopup)
        {
            Width = 420;
            Height = 580;
        }

        // Set extension icon if available
        if (!string.IsNullOrWhiteSpace(extension.IconPath) && File.Exists(extension.IconPath))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(extension.IconPath, UriKind.Absolute);
                bitmap.DecodePixelWidth = 32;
                bitmap.EndInit();
                bitmap.Freeze();

                ExtensionIconImage.Source = bitmap;
                ExtensionIconImage.Visibility = Visibility.Visible;
                DefaultIconPath.Visibility = Visibility.Collapsed;
            }
            catch { }
        }

        Loaded += ExtensionOptionsWindow_Loaded;
    }

    private async void ExtensionOptionsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= ExtensionOptionsWindow_Loaded;

        try
        {
            if (string.IsNullOrWhiteSpace(_targetUrl))
            {
                Close();
                return;
            }

            var env = await BrowserWindowController.GetSharedEnvironmentAsync();
            await WebView.EnsureCoreWebView2Async(env);

            WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            WebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            WebView.CoreWebView2.Settings.IsWebMessageEnabled = true;

            // Handle popups/links opened from within the extension page
            WebView.CoreWebView2.NewWindowRequested += (s, args) =>
            {
                args.Handled = true;
                if (!string.IsNullOrWhiteSpace(args.Uri))
                {
                    if (args.Uri.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
                    {
                        var subWin = new ExtensionOptionsWindow(_extension, args.Uri)
                        {
                            Owner = this.Owner ?? this
                        };
                        subWin.Show();
                    }
                    else if (args.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                             args.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo { FileName = args.Uri, UseShellExecute = true });
                        }
                        catch { }
                    }
                }
            };

            WebView.CoreWebView2.Navigate(_targetUrl);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[Extensions] Seite von '{_extension.Name}' konnte nicht geöffnet werden", ex);
            Close();
        }
    }

    private void UpdateTitleBarTheme(bool isDark)
    {
        ThemeService.ApplyDarkTitleBar(this, isDark);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        ThemeService.Instance.ThemeChanged -= UpdateTitleBarTheme;
        try { WebView?.Dispose(); } catch { }
    }
}
