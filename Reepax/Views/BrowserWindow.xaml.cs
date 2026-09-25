using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Reepax.Models;
using Reepax.Services;
using Reepax.Services.AdBlock;
using Reepax.Services.Browser;
using Reepax.Services.Download;
using Reepax.Services.Extractor;
using Reepax.Services.Storage;

namespace Reepax.Views;

/// <summary>
/// External browser window for filehoster links that require manual interaction
/// (captcha / download button click) (Scenario C).
/// The actual download stream is intercepted and transferred into the app engine.
/// </summary>
public partial class BrowserWindow : Window
{
    private readonly int _windowId;
    private readonly DownloadItem _item;
    private BrowserWindowController? _controller;
    private readonly System.Windows.Threading.DispatcherTimer? _revealTimer;

    public int WindowId => _windowId;
    public DownloadItem Item => _item;

    public BrowserWindow(int windowId, DownloadItem item, bool startHidden = false)
    {
        InitializeComponent();
        ThemeService.ApplyDarkTitleBar(this, ThemeService.Instance.IsDarkMode);
        ThemeService.Instance.ThemeChanged += UpdateTitleBarTheme;

        _windowId = windowId;
        _item = item;

        Title = $"Reepax Browser {windowId} – {item.HosterName}";
        HosterText.Text = item.HosterName;
        UrlText.Text = item.OriginalUrl;

        // ONLY direct download links and auto-resolvers start minimized.
        // All other links (hoster landing pages, captchas, countdowns, etc.) open in normal window state.
        bool isDirect = startHidden ||
                        FastHostResolver.IsDirectDownloadUrl(item.OriginalUrl) ||
                        !string.IsNullOrWhiteSpace(item.DirectDownloadUrl);

        WindowState = isDirect ? WindowState.Minimized : WindowState.Normal;
        ShowInTaskbar = true;
        EnsureRestoreBoundsCentered();

        if (isDirect)
        {
            // Auto-resolver / direct link: window starts minimized.
            // If automatic resolution fails (e.g. interactive captcha challenge),
            // the window is brought to the foreground after 60s so the user can interact.
            Title = $"Reepax Auto-Resolver – {item.HosterName}";

            _revealTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(60)
            };
            _revealTimer.Tick += (_, _) => Reveal();
            _revealTimer.Start();
        }

        Loaded += BrowserWindow_Loaded;
        StateChanged += BrowserWindow_StateChanged;
    }

    /// <summary>Reveals a minimized auto-resolver window in normal state for manual user interaction.</summary>
    public void Reveal()
    {
        _revealTimer?.Stop();
        Opacity = 1;
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    private void BrowserWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
        {
            try
            {
                WebView?.InvalidateVisual();
                WebView?.UpdateLayout();
            }
            catch { }
        }
    }

    private void EnsureRestoreBoundsCentered()
    {
        try
        {
            var screenWidth = SystemParameters.PrimaryScreenWidth;
            var screenHeight = SystemParameters.PrimaryScreenHeight;
            var width = Width > 0 ? Width : 1100;
            var height = Height > 0 ? Height : 760;
            Left = Math.Max(0, (screenWidth - width) / 2);
            Top = Math.Max(0, (screenHeight - height) / 2);
        }
        catch { }
    }

    private async void BrowserWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= BrowserWindow_Loaded;

        try
        {
            var controller = new BrowserWindowController(_windowId, WebView);
            _controller = controller;
            controller.BlockedCountChanged += UpdateShieldBadge;
            await controller.InitializeAsync();

            // Extensions are installed after initialization -> populate icons
            PopulateExtensions();

            // Window may have been closed during asynchronous initialization
            if (_controller == null)
                return;

            if (controller.IsInitialized)
            {
                controller.Navigate(_item.OriginalUrl);
            }
            else
            {
                QueueManager.Instance.OnWindowNavigationFailed(_windowId, Services.Localization.Loc.Get("Browser_WebViewInitFailed"));
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[BrowserWindow {_windowId}] Initialisierungsfehler", ex);
            QueueManager.Instance.OnWindowNavigationFailed(_windowId, ex.Message);
        }
    }


    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (WebView?.CoreWebView2 != null)
            {
                WebView.CoreWebView2.Reload();
                return;
            }
        }
        catch { }

        if (!string.IsNullOrWhiteSpace(_item.OriginalUrl))
        {
            _controller?.Navigate(_item.OriginalUrl);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // ==================== Erweiterungen (Extensions-Toolbar) ====================

    private void PopulateExtensions()
    {
        ExtensionsPanel.Children.Clear();

        foreach (var ext in BrowserExtensionService.Instance.Extensions)
        {
            var button = new Button
            {
                Width = 22,
                Height = 22,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 4, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ToolTip = BuildExtensionTooltip(ext),
                Tag = ext
            };

            if (!string.IsNullOrWhiteSpace(ext.IconPath) && File.Exists(ext.IconPath))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(ext.IconPath, UriKind.Absolute);
                    bitmap.DecodePixelWidth = 32;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    button.Content = new Image { Source = bitmap, Width = 16, Height = 16 };
                }
                catch
                {
                    button.Content = CreateFallbackExtensionIcon();
                }
            }
            else
            {
                button.Content = CreateFallbackExtensionIcon();
            }

            if (!ext.IsEnabled)
            {
                button.Opacity = 0.35;
            }

            button.Click += ExtensionIcon_Click;
            button.ContextMenu = BuildExtensionContextMenu(ext);
            ExtensionsPanel.Children.Add(button);
        }
    }

    private System.Windows.Shapes.Path CreateFallbackExtensionIcon()
    {
        return new System.Windows.Shapes.Path
        {
            Data = (Geometry)FindResource("IconSettings"),
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform
        };
    }

    private static string BuildExtensionTooltip(BrowserExtensionInfo ext)
    {
        var tooltip = ext.IsEnabled ? ext.Name : $"{ext.Name} {Services.Localization.Loc.Get("Browser_ExtensionDisabled")}";
        if (!string.IsNullOrWhiteSpace(ext.InstallError))
        {
            tooltip += $"\n{Services.Localization.Loc.Format("Browser_ExtensionInstallError", ext.InstallError)}";
        }
        else if (ext.IsEnabled && !ext.HasPage)
        {
            tooltip += $"\n{Services.Localization.Loc.Get("Browser_ExtensionNoOptionsPage")}";
        }
        else if (ext.IsEnabled)
        {
            tooltip += $"\n{Services.Localization.Loc.Get("Browser_ExtensionTooltipHint")}";
        }
        return tooltip;
    }

    private ContextMenu BuildExtensionContextMenu(BrowserExtensionInfo ext)
    {
        var menu = new ContextMenu();

        if (ext.IsEnabled && !string.IsNullOrWhiteSpace(ext.PopupPageUrl))
        {
            var openPopupItem = new MenuItem { Header = ext.Name };
            openPopupItem.Click += (_, _) =>
            {
                var popupWin = new ExtensionOptionsWindow(ext, ext.PopupPageUrl) { Owner = this };
                popupWin.Show();
            };
            menu.Items.Add(openPopupItem);
        }

        if (ext.IsEnabled && !string.IsNullOrWhiteSpace(ext.OptionsPageUrl) &&
            !string.Equals(ext.OptionsPageUrl, ext.PopupPageUrl, StringComparison.OrdinalIgnoreCase))
        {
            var openOptionsItem = new MenuItem { Header = Services.Localization.Loc.Get("Browser_ExtensionOptions") };
            openOptionsItem.Click += (_, _) =>
            {
                var optionsWin = new ExtensionOptionsWindow(ext, ext.OptionsPageUrl) { Owner = this };
                optionsWin.Show();
            };
            menu.Items.Add(openOptionsItem);
        }

        if (menu.Items.Count > 0)
        {
            menu.Items.Add(new Separator());
        }

        var toggleItem = new MenuItem
        {
            Header = ext.IsEnabled ? Services.Localization.Loc.Get("Browser_ExtensionDisable") : Services.Localization.Loc.Get("Browser_ExtensionEnable")
        };
        toggleItem.Click += (_, _) =>
        {
            BrowserExtensionService.Instance.SetEnabled(ext, !ext.IsEnabled);
            PopulateExtensions();
        };
        menu.Items.Add(toggleItem);

        var openFolderItem = new MenuItem { Header = Services.Localization.Loc.Get("Browser_ExtensionOpenFolder") };
        openFolderItem.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{ext.FolderPath}\"",
                    UseShellExecute = true
                });
            }
            catch
            {
                try { Process.Start(new ProcessStartInfo { FileName = ext.FolderPath, UseShellExecute = true }); } catch { }
            }
        };
        menu.Items.Add(openFolderItem);

        menu.Items.Add(new Separator());

        var removeItem = new MenuItem { Header = Services.Localization.Loc.Get("Browser_ExtensionRemove") };
        removeItem.Click += (_, _) =>
        {
            if (ConfirmDialog.Show(Services.Localization.Loc.Get("Dialog_RemoveExtensionTitle"),
                    Services.Localization.Loc.Format("Dialog_RemoveExtensionMessage", ext.Name),
                    Services.Localization.Loc.Get("Common_Yes"),
                    Services.Localization.Loc.Get("Common_No")))
            {
                BrowserExtensionService.Instance.Remove(ext);
                PopulateExtensions();
            }
        };
        menu.Items.Add(removeItem);

        return menu;
    }

    private void ExtensionIcon_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not BrowserExtensionInfo ext)
            return;

        if (!ext.IsEnabled || string.IsNullOrWhiteSpace(ext.PopupOrOptionsUrl))
            return;

        var optionsWindow = new ExtensionOptionsWindow(ext, ext.PopupOrOptionsUrl)
        {
            Owner = this
        };
        optionsWindow.Show();
    }

    private void UpdateShieldBadge(int count)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (count > 0)
            {
                ShieldBadgeText.Text = count > 99 ? "99+" : count.ToString();
                ShieldBadge.Visibility = Visibility.Visible;
                ShieldCountLarge.Text = count.ToString();
            }
            else
            {
                ShieldBadge.Visibility = Visibility.Collapsed;
                ShieldCountLarge.Text = "0";
            }
        });
    }

    private void ShieldButton_Click(object sender, RoutedEventArgs e)
    {
        var currentUrl = _controller?.CurrentNavigatingUrl ?? _item.OriginalUrl;
        string host = "–";
        try
        {
            if (!string.IsNullOrWhiteSpace(currentUrl))
            {
                host = new Uri(currentUrl).Host;
            }
        }
        catch { }

        ShieldHostText.Text = host;
        var blocked = _controller?.BlockedCount ?? 0;
        ShieldCountLarge.Text = blocked.ToString();

        ShieldPopup.IsOpen = true;
    }

    private async void UpdateLists_Click(object sender, RoutedEventArgs e)
    {
        UpdateListsButton.IsEnabled = false;
        UpdateListsButton.Content = Services.Localization.Loc.Get("Browser_Shield_UpdatingLists");
        try
        {
            var result = await AdBlockRustEngine.Instance.UpdateFilterListsAsync();
            if (result == FilterUpdateResult.NewFiltersApplied)
            {
                UpdateListsButton.Content = Services.Localization.Loc.Get("Browser_Shield_ListsUpdatedNew");
            }
            else if (result == FilterUpdateResult.AlreadyUpToDate)
            {
                UpdateListsButton.Content = Services.Localization.Loc.Get("Browser_Shield_ListsUpToDate");
            }
            else
            {
                UpdateListsButton.Content = Services.Localization.Loc.Get("Browser_Shield_ListsUpdateFailed");
            }

            await System.Threading.Tasks.Task.Delay(2500);
            UpdateListsButton.Content = Services.Localization.Loc.Get("Browser_Shield_UpdateLists");
        }
        catch
        {
            UpdateListsButton.Content = Services.Localization.Loc.Get("Browser_Shield_ListsUpdateFailed");
            await System.Threading.Tasks.Task.Delay(2500);
            UpdateListsButton.Content = Services.Localization.Loc.Get("Browser_Shield_UpdateLists");
        }
        finally
        {
            UpdateListsButton.IsEnabled = true;
        }
    }

    private void UpdateTitleBarTheme(bool isDark)
    {
        ThemeService.ApplyDarkTitleBar(this, isDark);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _revealTimer?.Stop();
        ThemeService.Instance.ThemeChanged -= UpdateTitleBarTheme;
        StateChanged -= BrowserWindow_StateChanged;
        if (_controller != null)
        {
            _controller.BlockedCountChanged -= UpdateShieldBadge;
            _controller.Dispose();
            _controller = null;
        }
        try { WebView?.Dispose(); } catch { }
    }
}
