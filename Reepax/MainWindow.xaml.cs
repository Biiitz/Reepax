using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Reepax.Helpers;
using Reepax.Models;
using Reepax.Services;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;
using Reepax.ViewModels;
using Reepax.Views;

namespace Reepax;

public partial class MainWindow : Window
{
    private bool _hasInitializedDownloadsTab;
    private bool _isTabIndicatorInitialized;

    public MainWindow()
    {
        InitializeComponent();
        Title = ViewModel.WindowTitle;
        ThemeService.ApplyDarkTitleBar(this, ThemeService.Instance.IsDarkMode);

        Loaded += MainWindow_TabIndicator_Loaded;

        // Browser-style navigation
        CommandBindings.Add(new CommandBinding(NavigationCommands.BrowseBack, (s, e) =>
        {
            if (ViewModel.CanGoBack)
            {
                ViewModel.GoBackCommand.Execute(null);
                e.Handled = true;
            }
        }, (s, e) =>
        {
            e.CanExecute = ViewModel.CanGoBack;
            e.Handled = true;
        }));

        CommandBindings.Add(new CommandBinding(NavigationCommands.BrowseForward, (s, e) =>
        {
            if (ViewModel.CanGoForward)
            {
                ViewModel.GoForwardCommand.Execute(null);
                e.Handled = true;
            }
        }, (s, e) =>
        {
            e.CanExecute = ViewModel.CanGoForward;
            e.Handled = true;
        }));
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);

        if (e.ChangedButton == MouseButton.XButton1)
        {
            if (ViewModel.CanGoBack)
            {
                ViewModel.GoBackCommand.Execute(null);
            }
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.XButton2)
        {
            if (ViewModel.CanGoForward)
            {
                ViewModel.GoForwardCommand.Execute(null);
            }
            e.Handled = true;
        }
    }

    protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseUp(e);
        if (e.ChangedButton is MouseButton.XButton1 or MouseButton.XButton2)
        {
            e.Handled = true;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        ThemeService.Instance.Initialize();
        ThemeService.ApplyDarkTitleBar(this, ThemeService.Instance.IsDarkMode);
        ThemeService.Instance.ThemeChanged += UpdateTitleBarTheme;

        Services.SystemIntegration.TrayIconService.Instance.Initialize(this);

        var args = Environment.GetCommandLineArgs();
        if (args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) || a.Equals("--autostart", StringComparison.OrdinalIgnoreCase)))
        {
            Services.SystemIntegration.TrayIconService.Instance.ShowTrayIcon();
            Hide();
        }

        // Check for package file (.repx/.sdlr) arguments passed at startup
        foreach (var arg in args.Skip(1))
        {
            if (PackageExportImportService.IsSupportedPackageFile(arg) && File.Exists(arg))
            {
                _ = PackageExportImportService.ImportAndAddAsync(arg, ViewModel);
            }
        }

        try
        {
            var settings = SettingsService.Instance.Settings;
            if (settings.WindowWidth > 300) Width = settings.WindowWidth;
            if (settings.WindowHeight > 200) Height = settings.WindowHeight;

            if (settings.WindowLeft.HasValue && settings.WindowTop.HasValue)
            {
                var virtualLeft = SystemParameters.VirtualScreenLeft;
                var virtualTop = SystemParameters.VirtualScreenTop;
                var virtualWidth = SystemParameters.VirtualScreenWidth;
                var virtualHeight = SystemParameters.VirtualScreenHeight;

                if (settings.WindowLeft.Value >= virtualLeft - 50 &&
                    settings.WindowLeft.Value < virtualLeft + virtualWidth - 50 &&
                    settings.WindowTop.Value >= virtualTop - 50 &&
                    settings.WindowTop.Value < virtualTop + virtualHeight - 50)
                {
                    Left = settings.WindowLeft.Value;
                    Top = settings.WindowTop.Value;
                }
            }

            if (settings.IsWindowMaximized)
            {
                WindowState = WindowState.Maximized;
            }
        }
        catch { }

        // Connect external browser window pool to the download queue
        try
        {
            Services.Download.QueueManager.Instance.BrowserHost = new Services.Browser.BrowserWindowManager();
        }
        catch { }

        LocationChanged += (_, _) => SaveWindowState();
        SizeChanged += (_, _) =>
        {
            SaveWindowState();
            AutoFitNameColumn();
        };
        Loaded += (_, _) => AutoFitNameColumn();

        ViewModel.ColumnLayoutChanged += (_, _) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                AutoFitNameColumn();
            });
        };

        ViewModel.RequestShowAddLinksDialog += (_, _) => ShowAddLinksDialog();
        ViewModel.RequestShowUpdateDialog += (_, updateInfo) => ShowUpdateDialog(updateInfo);
    }

    private void TabContent_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;

        var transform = (element.RenderTransform as TranslateTransform)
                     ?? (element.RenderTransform = new TranslateTransform()) as TranslateTransform;

        if (element.IsVisible)
        {
            // Skip the initial animation on cold window startup for the default Downloads tab
            if (!_hasInitializedDownloadsTab && element == DownloadsTabContent)
            {
                _hasInitializedDownloadsTab = true;
                return;
            }
            _hasInitializedDownloadsTab = true;

            AnimateTabIn(element, transform);
        }
        else
        {
            ResetTabAnimation(element, transform);
        }
    }

    private static void ResetTabAnimation(FrameworkElement element, TranslateTransform? transform)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1.0;
        if (transform != null)
        {
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.Y = 0.0;
        }
    }

    private void AnimateTabIn(FrameworkElement element, TranslateTransform? transform)
    {
        ResetTabAnimation(element, transform);

        var duration = TimeSpan.FromMilliseconds(240);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var fadeAnim = new DoubleAnimation
        {
            From = 0.0,
            To = 1.0,
            Duration = duration,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        };

        fadeAnim.Completed += (s, e) =>
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1.0;
        };

        element.BeginAnimation(UIElement.OpacityProperty, fadeAnim);

        if (transform != null)
        {
            var slideAnim = new DoubleAnimation
            {
                From = 20.0,
                To = 0.0,
                Duration = duration,
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd
            };

            slideAnim.Completed += (s, e) =>
            {
                transform.BeginAnimation(TranslateTransform.YProperty, null);
                transform.Y = 0.0;
            };

            transform.BeginAnimation(TranslateTransform.YProperty, slideAnim);
        }
    }

    private void MainWindow_TabIndicator_Loaded(object sender, RoutedEventArgs e)
    {
        if (DownloadsTabButton != null)
            DownloadsTabButton.SizeChanged += (_, _) => UpdateSlidingTabIndicator(animate: false);
        if (SettingsTabButton != null)
            SettingsTabButton.SizeChanged += (_, _) => UpdateSlidingTabIndicator(animate: false);
        if (TabStripContainer != null)
            TabStripContainer.SizeChanged += (_, _) => UpdateSlidingTabIndicator(animate: false);

        if (ViewModel != null)
            ViewModel.PropertyChanged += ViewModel_TabIndicator_PropertyChanged;

        UpdateSlidingTabIndicator(animate: false);
        _isTabIndicatorInitialized = true;
    }

    private void ViewModel_TabIndicator_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.SelectedMainTab))
        {
            Dispatcher.BeginInvoke(new Action(() => UpdateSlidingTabIndicator(animate: true)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void UpdateSlidingTabIndicator(bool animate = true)
    {
        if (DownloadsTabButton == null || SettingsTabButton == null || 
            TabIndicatorTransform == null || SlidingTabIndicator == null || 
            TabStripContainer == null)
            return;

        var targetButton = ViewModel?.SelectedMainTab == AppMainTab.Settings 
            ? SettingsTabButton 
            : DownloadsTabButton;

        double targetWidth = targetButton.ActualWidth;
        if (targetWidth <= 0)
        {
            targetButton.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            targetWidth = targetButton.DesiredSize.Width > 0 ? targetButton.DesiredSize.Width : targetButton.MinWidth;
        }

        Point relativePoint;
        try
        {
            relativePoint = targetButton.TranslatePoint(new Point(0, 0), TabStripContainer);
        }
        catch
        {
            return;
        }

        double targetX = relativePoint.X;

        if (!animate || !_isTabIndicatorInitialized)
        {
            TabIndicatorTransform.BeginAnimation(TranslateTransform.XProperty, null);
            TabIndicatorTransform.X = targetX;
            SlidingTabIndicator.BeginAnimation(FrameworkElement.WidthProperty, null);
            SlidingTabIndicator.Width = targetWidth;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(200);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var slideAnim = new DoubleAnimation
        {
            To = targetX,
            Duration = duration,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        };

        var widthAnim = new DoubleAnimation
        {
            To = targetWidth,
            Duration = duration,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        };

        TabIndicatorTransform.BeginAnimation(TranslateTransform.XProperty, slideAnim);
        SlidingTabIndicator.BeginAnimation(FrameworkElement.WidthProperty, widthAnim);
    }

    private void SaveWindowState()
    {
        try
        {
            var settings = SettingsService.Instance.Settings;
            if (WindowState == WindowState.Maximized)
            {
                settings.IsWindowMaximized = true;
                if (RestoreBounds.Width > 200 && RestoreBounds.Height > 200)
                {
                    settings.WindowWidth = RestoreBounds.Width;
                    settings.WindowHeight = RestoreBounds.Height;
                    settings.WindowLeft = RestoreBounds.Left;
                    settings.WindowTop = RestoreBounds.Top;
                }
            }
            else if (WindowState == WindowState.Normal)
            {
                settings.IsWindowMaximized = false;
                settings.WindowWidth = Width;
                settings.WindowHeight = Height;
                settings.WindowLeft = Left;
                settings.WindowTop = Top;
            }

            SettingsService.Instance.SaveSettings();
        }
        catch { }
    }

    private bool _isForceExit;

    public void ForceExit()
    {
        _isForceExit = true;
        Close();
    }

    public void PrepareForRestart()
    {
        SaveWindowState();
        var settings = SettingsService.Instance.Settings;
        if (ViewModel != null)
        {
            settings.ColWidthName = ViewModel.ColWidthName;
            settings.ColWidthHoster = ViewModel.ColWidthHoster;
            settings.ColWidthSize = ViewModel.ColWidthSize;
            settings.ColWidthProgress = ViewModel.ColWidthProgress;
            settings.ColWidthSpeed = ViewModel.ColWidthSpeed;
            settings.ColWidthEta = ViewModel.ColWidthEta;
            settings.ColWidthStatus = ViewModel.ColWidthStatus;
            settings.ColWidthActions = ViewModel.ColWidthActions;
        }
        SettingsService.Instance.SaveSettings();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);

        var settings = SettingsService.Instance.Settings;
        var minimizeToTray = ViewModel?.MinimizeToTrayOnClose ?? settings.MinimizeToTrayOnClose;

        if (!_isForceExit && minimizeToTray)
        {
            e.Cancel = true;
            SaveWindowState();
            Hide();
            Services.SystemIntegration.TrayIconService.Instance.ShowTrayIcon();
            return;
        }

        try
        {
            // Remove tray icon immediately
            Services.SystemIntegration.TrayIconService.Instance.HideTrayIcon();
            Services.SystemIntegration.TrayIconService.Instance.Dispose();

            SaveWindowState();

            // Save Column Widths
            if (ViewModel != null)
            {
                settings.ColWidthName = ViewModel.ColWidthName;
                settings.ColWidthHoster = ViewModel.ColWidthHoster;
                settings.ColWidthSize = ViewModel.ColWidthSize;
                settings.ColWidthProgress = ViewModel.ColWidthProgress;
                settings.ColWidthSpeed = ViewModel.ColWidthSpeed;
                settings.ColWidthEta = ViewModel.ColWidthEta;
                settings.ColWidthStatus = ViewModel.ColWidthStatus;
                settings.ColWidthActions = ViewModel.ColWidthActions;
            }

            SettingsService.Instance.SaveSettings();

            // Perform central safe shutdown (pauses all downloads, flushes streams, saves downloads.json)
            Services.Download.QueueManager.PerformSafeShutdown();
        }
        catch (Exception ex)
        {
            Services.Storage.AppLogger.Error("[MainWindow] Fehler beim Schließen der Anwendung", ex);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        ThemeService.Instance.ThemeChanged -= UpdateTitleBarTheme;
        try
        {
            Application.Current?.Shutdown();
        }
        catch { }
    }

    private void UpdateTitleBarTheme(bool isDark)
    {
        ThemeService.ApplyDarkTitleBar(this, isDark);
    }

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        if (ViewModel.IsBusy)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.Text) ||
            e.Data.GetDataPresent(DataFormats.UnicodeText) ||
            e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (ViewModel.IsBusy)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent("Reepax.DownloadPackage"))
        {
            var pkg = e.Data.GetData("Reepax.DownloadPackage") as DownloadPackage;
            if (pkg != null && pkg.IsClipped)
            {
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
                return;
            }
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.Text) ||
            e.Data.GetDataPresent(DataFormats.UnicodeText) ||
            e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        // Reset effects
    }

    private AddLinksDialog? _openAddLinksDialog;

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (ViewModel.IsBusy)
            return;

        if (e.Data.GetDataPresent("Reepax.DownloadPackage"))
        {
            var pkg = e.Data.GetData("Reepax.DownloadPackage") as DownloadPackage;
            if (pkg != null && pkg.IsClipped)
            {
                ViewModel.UnclipPackage(pkg);
                e.Handled = true;
                return;
            }
            return;
        }

        // The IDataObject must be read synchronously in the drop event (OLE lifetime).
        // Dialog/MessageBox must NOT be opened synchronously in the drop callback —
        // the source browser would block until the dialog is closed.
        string? droppedText = null;
        string[]? droppedFiles = null;

        try
        {
            // For drag & drop from browsers, inspect HTML format first (contains real
            // href links); plaintext selection often lacks the target URLs.
            if (e.Data.GetDataPresent(DataFormats.Html))
            {
                droppedText = e.Data.GetData(DataFormats.Html) as string;
            }

            if (string.IsNullOrWhiteSpace(droppedText) && e.Data.GetDataPresent(DataFormats.Text))
            {
                droppedText = e.Data.GetData(DataFormats.Text) as string;
            }
            else if (string.IsNullOrWhiteSpace(droppedText) && e.Data.GetDataPresent(DataFormats.UnicodeText))
            {
                droppedText = e.Data.GetData(DataFormats.UnicodeText) as string;
            }
            else if (string.IsNullOrWhiteSpace(droppedText) && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    droppedFiles = files;
                }
            }
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            Dispatcher.BeginInvoke(new Action(() =>
                MessageBox.Show(Services.Localization.Loc.Format("Dialog_DropErrorMessage", message), Services.Localization.Loc.Get("Dialog_DropErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Warning)));
            return;
        }

        // Remaining tasks (file reading, opening dialog) only after returning from OLE callback.
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                if (droppedFiles != null && droppedFiles.Length > 0)
                {
                    var pkgFiles = droppedFiles.Where(f => PackageExportImportService.IsSupportedPackageFile(f) && File.Exists(f)).ToList();
                    var otherFiles = droppedFiles.Where(f => !PackageExportImportService.IsSupportedPackageFile(f)).ToArray();

                    foreach (var pkg in pkgFiles)
                    {
                        await PackageExportImportService.ImportAndAddAsync(pkg, ViewModel);
                    }

                    if (otherFiles.Length > 0)
                    {
                        // Read text from dropped text/URL files asynchronously (max 5 MB per file to prevent UI freeze/OOM)
                        droppedText = await Task.Run(() => ReadDroppedFiles(otherFiles));
                    }
                    else
                    {
                        droppedText = null;
                    }
                }

                if (!string.IsNullOrWhiteSpace(droppedText))
                {
                    ShowAddLinksDialog(droppedText);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(Services.Localization.Loc.Format("Dialog_DropErrorMessage", ex.Message), Services.Localization.Loc.Get("Dialog_DropErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }));
    }

    private static string ReadDroppedFiles(string[] files)
    {
        var textBuilder = new System.Text.StringBuilder();
        const long maxSizeBytes = 5 * 1024 * 1024; // 5 MB
        foreach (var file in files)
        {
            if (File.Exists(file))
            {
                try
                {
                    var fi = new FileInfo(file);
                    if (fi.Length > 0 && fi.Length <= maxSizeBytes)
                    {
                        textBuilder.AppendLine(File.ReadAllText(file));
                    }
                }
                catch { }
            }
        }
        return textBuilder.ToString();
    }


    private void ShowAddLinksDialog(string? prefill = null, DownloadPackage? targetPackage = null)
    {
        if (ViewModel.IsBusy)
            return;

        // Already open dialog: avoid second modal window — append dropped text instead.
        if (_openAddLinksDialog != null)
        {
            if (!string.IsNullOrWhiteSpace(prefill))
            {
                _openAddLinksDialog.AppendText(prefill);
            }
            _openAddLinksDialog.Activate();
            return;
        }

        var dialog = targetPackage != null
            ? new AddLinksDialog(prefill, targetPackage, isAddingLinksToExisting: true)
            : new AddLinksDialog(prefill);
        dialog.Owner = this;

        _openAddLinksDialog = dialog;
        dialog.Closed += (_, _) => _openAddLinksDialog = null;

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.EnteredText))
        {
            if (targetPackage != null)
            {
                bool isAlreadyCompletedOrExtracted = targetPackage.CheckIsFullyCompleted() ||
                    targetPackage.Status == DownloadStatus.Completed ||
                    targetPackage.NextTaskSteps.Any(s => s.Key == "Extract" && s.State == NextTaskStepState.Done);

                if (!isAlreadyCompletedOrExtracted)
                {
                    targetPackage.AutoExtractArchives = dialog.AutoExtractArchives;
                    targetPackage.LowResourceExtraction = dialog.LowResourceExtraction;
                    targetPackage.DeleteArchiveAfterExtraction = dialog.DeleteArchiveAfterExtraction;
                    targetPackage.MoveArchiveToRecycleBin = dialog.MoveArchiveToRecycleBin;
                    targetPackage.EnsureNextTaskSteps();
                }

                ViewModel.AddLinksToPackage(targetPackage, dialog.EnteredText, dialog.AutoResolveHostLinks);
            }
            else
            {
                ViewModel.AddLinksFromText(
                    dialog.EnteredText,
                    dialog.CustomPackageName,
                    dialog.AutoExtractArchives,
                    dialog.LowResourceExtraction,
                    dialog.CustomDownloadDirectory,
                    dialog.DeleteArchiveAfterExtraction,
                    dialog.MoveArchiveToRecycleBin,
                    dialog.AutoResolveHostLinks);
            }
        }
    }

    private void AddLinks_Click(object sender, RoutedEventArgs e)
    {
        ShowAddLinksDialog();
    }

    private UpdateDialog? _openUpdateDialog;

    private void ShowUpdateDialog(Services.Update.UpdateInfo updateInfo)
    {
        if (_openUpdateDialog != null)
        {
            _openUpdateDialog.Activate();
            return;
        }

        var dialog = new UpdateDialog(updateInfo)
        {
            Owner = this
        };
        _openUpdateDialog = dialog;
        dialog.Closed += (_, _) => _openUpdateDialog = null;
        dialog.ShowDialog();
    }

    private void EditPackage_MenuItem_Click(object sender, RoutedEventArgs e)
    {
        var package = GetPackageFromSender(sender);
        if (package == null) return;
        ShowEditPackageDialog(package);
    }

    private void ShowEditPackageDialog(DownloadPackage package)
    {
        if (ViewModel.IsBusy || package == null)
            return;

        if (_openAddLinksDialog != null)
        {
            _openAddLinksDialog.Activate();
            return;
        }

        var dialog = new AddLinksDialog(package);
        dialog.Owner = this;
        _openAddLinksDialog = dialog;
        dialog.Closed += (_, _) => _openAddLinksDialog = null;

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.EnteredText))
        {
            ViewModel.EditPackage(
                package,
                dialog.EnteredText,
                dialog.CustomPackageName,
                dialog.CustomDownloadDirectory,
                dialog.AutoExtractArchives,
                dialog.LowResourceExtraction,
                dialog.DeleteArchiveAfterExtraction,
                dialog.MoveArchiveToRecycleBin,
                dialog.AutoResolveHostLinks);
        }
    }

    private void AddLinksToPackage_MenuItem_Click(object sender, RoutedEventArgs e)
    {
        var package = GetPackageFromSender(sender);
        if (package == null) return;

        ShowAddLinksDialog(targetPackage: package);
    }

    private void ExportPackage_MenuItem_Click(object sender, RoutedEventArgs e)
    {
        var pkg = GetPackageFromSender(sender);
        if (pkg != null)
        {
            PackageExportImportService.ExportWithDialog(pkg, this);
        }
    }

    private DownloadPackage? GetPackageFromSender(object sender)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadPackage pkg1)
            return pkg1;

        if (((sender as FrameworkElement)?.Parent as ContextMenu)?.PlacementTarget is FrameworkElement fe && fe.DataContext is DownloadPackage pkg2)
            return pkg2;

        return ViewModel.SelectedPackage ?? ViewModel.Packages.FirstOrDefault(p => p.IsSelected);
    }

    private DownloadItem? GetItemFromSender(object sender)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadItem item1)
            return item1;

        if (((sender as FrameworkElement)?.Parent as ContextMenu)?.PlacementTarget is FrameworkElement fe && fe.DataContext is DownloadItem item2)
            return item2;

        return ViewModel.SelectedItem ?? ViewModel.Packages.SelectMany(p => p.Items).FirstOrDefault(i => i.IsSelected);
    }

    private void CopyPackageUrls_MenuItem_Click(object sender, RoutedEventArgs e)
    {
        var package = GetPackageFromSender(sender);
        if (package != null)
        {
            ViewModel.CopyPackageUrls(package);
        }
    }

    private Point _dragStartPoint;
    private DownloadPackage? _draggedPackage;
    private bool _isDraggingPackage;
    private ContextMenu? _activeContextMenu;
    private DateTime _lastContextMenuClosedTime = DateTime.MinValue;

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T parent)
                return parent;
            child = System.Windows.Media.VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private void ClearAllDropTargets()
    {
        foreach (var p in ViewModel.Packages)
        {
            p.IsDropTarget = false;
        }
    }

    private void PackageRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // If a context menu is still open or was just closed (within 250ms),
        // this click must never initiate a drag operation.
        if ((_activeContextMenu != null && _activeContextMenu.IsOpen) || 
            (DateTime.UtcNow - _lastContextMenuClosedTime).TotalMilliseconds < 250)
        {
            if (_activeContextMenu != null && _activeContextMenu.IsOpen)
            {
                _activeContextMenu.IsOpen = false;
            }
            _activeContextMenu = null;
            _draggedPackage = null;
            _isDraggingPackage = false;
            ClearAllDropTargets();
            return;
        }

        if (e.OriginalSource is DependencyObject dep)
        {
            // Do not initiate drag if user is clicking interactive controls (Buttons, TextBoxes, ToggleButtons)
            if (FindVisualParent<ButtonBase>(dep) != null || FindVisualParent<TextBox>(dep) != null)
            {
                _draggedPackage = null;
                return;
            }
        }

        _dragStartPoint = e.GetPosition(this);
        _draggedPackage = (sender as FrameworkElement)?.DataContext as DownloadPackage;
    }

    private void PackageRow_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedPackage == null || _isDraggingPackage)
            return;

        if ((_activeContextMenu != null && _activeContextMenu.IsOpen) ||
            (DateTime.UtcNow - _lastContextMenuClosedTime).TotalMilliseconds < 250)
        {
            _draggedPackage = null;
            return;
        }

        Point currentPosition = e.GetPosition(this);
        Vector diff = _dragStartPoint - currentPosition;

        // Clear threshold (at least 16 pixels) so a click never accidentally triggers drag
        if (Math.Abs(diff.X) > 16 || Math.Abs(diff.Y) > 16)
        {
            var pkgToDrag = _draggedPackage;
            _isDraggingPackage = true;
            try
            {
                var data = new DataObject("Reepax.DownloadPackage", pkgToDrag);
                DragDrop.DoDragDrop(sender as DependencyObject ?? this, data, DragDropEffects.Move);
            }
            finally
            {
                _isDraggingPackage = false;
                _draggedPackage = null;
                ClearAllDropTargets();
            }
        }
    }

    private void PackageRow_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _draggedPackage = null;
        _isDraggingPackage = false;
        ClearAllDropTargets();
    }

    private void PackageRow_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("Reepax.DownloadPackage"))
        {
            var sourcePkg = e.Data.GetData("Reepax.DownloadPackage") as DownloadPackage;
            var targetPkg = (sender as FrameworkElement)?.DataContext as DownloadPackage;

            if (sourcePkg != null && targetPkg != null && MainViewModel.CanClip(sourcePkg, targetPkg))
            {
                e.Effects = DragDropEffects.Move;
                targetPkg.IsDropTarget = true;
                e.Handled = true;
                return;
            }
        }

        if ((sender as FrameworkElement)?.DataContext is DownloadPackage pkg)
        {
            pkg.IsDropTarget = false;
        }

        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void PackageRow_DragLeave(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadPackage targetPkg)
        {
            targetPkg.IsDropTarget = false;
        }
    }

    private void PackageRow_Drop(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadPackage targetPkg)
        {
            targetPkg.IsDropTarget = false;

            if (e.Data.GetDataPresent("Reepax.DownloadPackage"))
            {
                var sourcePkg = e.Data.GetData("Reepax.DownloadPackage") as DownloadPackage;
                if (sourcePkg != null)
                {
                    if (MainViewModel.CanClip(sourcePkg, targetPkg))
                    {
                        ViewModel.ClipPackage(sourcePkg, targetPkg);
                    }
                    // Always set e.Handled = true! A drop on any package row (even itself
                    // or current parent) must never bubble up to ScrollViewer and unclip by accident.
                    e.Handled = true;
                    return;
                }
            }
        }
    }

    private void PackagesScrollViewer_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("Reepax.DownloadPackage"))
        {
            var pkg = e.Data.GetData("Reepax.DownloadPackage") as DownloadPackage;
            if (pkg != null && pkg.IsClipped)
            {
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
                return;
            }
        }
    }

    private void PackagesScrollViewer_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("Reepax.DownloadPackage"))
        {
            var pkg = e.Data.GetData("Reepax.DownloadPackage") as DownloadPackage;
            if (pkg != null && pkg.IsClipped)
            {
                Point currentPos = e.GetPosition(this);
                Vector diff = _dragStartPoint - currentPos;
                if (Math.Abs(diff.X) > 20 || Math.Abs(diff.Y) > 20)
                {
                    ViewModel.UnclipPackage(pkg);
                }
                e.Handled = true;
                return;
            }
        }
    }

    private void PackageContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        _activeContextMenu = menu;

        var pkg = (menu.PlacementTarget as FrameworkElement)?.DataContext as DownloadPackage 
                  ?? ViewModel.SelectedPackage 
                  ?? ViewModel.Packages.FirstOrDefault(p => p.IsSelected);

        var openPackageMenuItem = menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "OpenPackageMenuItem" || (string?)m.Tag == "OpenPackageMenuItem");
        if (openPackageMenuItem != null)
        {
            if (pkg != null)
            {
                bool exists = pkg.RefreshAndCheckExistsOnDisk();
                openPackageMenuItem.Visibility = exists ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                openPackageMenuItem.Visibility = Visibility.Collapsed;
            }
        }

        var verifyMenuItem = menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "VerifyBinFilesMenuItem" || (string?)m.Tag == "VerifyBinFilesMenuItem");
        if (verifyMenuItem != null)
        {
            if (pkg != null)
            {
                pkg.CheckAndRefreshVerifyBatFile();
                verifyMenuItem.Visibility = pkg.HasVerifyBatFile ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                verifyMenuItem.Visibility = Visibility.Collapsed;
            }
        }

        var par2MenuItem = menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "Par2RepairMenuItem" || (string?)m.Tag == "Par2RepairMenuItem");
        if (par2MenuItem != null)
        {
            if (pkg != null)
            {
                bool hasPar2 = pkg.HasPar2Files();
                par2MenuItem.Visibility = hasPar2 ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                par2MenuItem.Visibility = Visibility.Collapsed;
            }
        }

        var verificationSep = menu.Items.OfType<Separator>().FirstOrDefault(s => (string?)s.Tag == "VerificationSeparator");
        if (verificationSep != null)
        {
            bool hasVerify = verifyMenuItem?.Visibility == Visibility.Visible;
            bool hasPar2 = par2MenuItem?.Visibility == Visibility.Visible;
            verificationSep.Visibility = (hasVerify || hasPar2) ? Visibility.Visible : Visibility.Collapsed;
        }

        if (pkg == null) return;

        var unclipMenuItem = menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "UnclipPackageMenuItem" || (string?)m.Tag == "UnclipPackageMenuItem");
        if (unclipMenuItem != null)
        {
            unclipMenuItem.Visibility = pkg.IsClipped ? Visibility.Visible : Visibility.Collapsed;
        }

        var unclipSep = menu.Items.OfType<Separator>().FirstOrDefault(s => (string?)s.Tag == "UnclipSeparator");
        if (unclipSep != null)
        {
            unclipSep.Visibility = pkg.IsClipped ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void PackageContextMenu_Closed(object sender, RoutedEventArgs e)
    {
        _activeContextMenu = null;
        _lastContextMenuClosedTime = DateTime.UtcNow;
        _draggedPackage = null;
        _isDraggingPackage = false;
        ClearAllDropTargets();
    }

    private void VerifyBinFiles_MenuItem_Click(object sender, RoutedEventArgs e)
    {
        var package = GetPackageFromSender(sender);
        if (package == null) return;

        var batPath = package.VerifyBatFilePath;
        if (string.IsNullOrEmpty(batPath) || !File.Exists(batPath))
        {
            batPath = DownloadPackage.FindVerifyBatFilePath(package);
        }

        if (string.IsNullOrEmpty(batPath) || !File.Exists(batPath))
        {
            MessageBox.Show(
                this,
                Loc.Get("Dialog_VerifyBinFilesNotFoundMessage"),
                Loc.Get("Dialog_VerifyBinFilesNotFoundTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            var workingDir = Path.GetDirectoryName(batPath) ?? string.Empty;
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = batPath,
                WorkingDirectory = workingDir,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
            ViewModel.StatusSummary = Loc.Format("Status_VerifyBinFilesLaunched", Path.GetFileName(batPath));
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[VerifyBinFiles] Fehler beim Starten von '{batPath}'", ex);
            MessageBox.Show(
                this,
                Loc.Format("Dialog_VerifyBinFilesErrorMessage", ex.Message),
                Loc.Get("Dialog_VerifyBinFilesErrorTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void Par2Repair_MenuItem_Click(object sender, RoutedEventArgs e)
    {
        var package = GetPackageFromSender(sender);
        if (package != null && ViewModel != null)
        {
            _ = ViewModel.Par2RepairPackageCommand.ExecuteAsync(package);
        }
    }

    private void OpenPackageFolder_MenuItem_Click(object sender, RoutedEventArgs e)
    {
        var package = GetPackageFromSender(sender);
        if (package == null) return;

        if (!package.RefreshAndCheckExistsOnDisk())
        {
            ViewModel.StatusSummary = Loc.Format("Status_CannotOpenDownloadFolder", package.SaveDirectory ?? package.Name);
            return;
        }

        var dir = package.SaveDirectory;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
            ViewModel.StatusSummary = Loc.Format("Status_PackageFolderOpened", dir);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[OpenPackageFolder] Fehler beim Öffnen von '{dir}'", ex);
            MessageBox.Show(
                this,
                Loc.Format("Status_CannotOpenFolder", ex.Message),
                Loc.Get("Dialog_ErrorTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void CreateGameInstallFolder_MenuItem_Click(object sender, RoutedEventArgs e)
    {
        var package = GetPackageFromSender(sender);
        if (package == null) return;

        string? targetBaseDir = null;

        // If "Game installation directory" setting is disabled:
        // Prompt dialog to choose destination directory.
        if (!SettingsService.Instance.Settings.CreateGameInstallFolder)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = Loc.Get("Dialog_SelectGameInstallDestinationDirTitle"),
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };

            if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            {
                return; // User cancelled
            }

            targetBaseDir = dialog.FolderName;
        }

        var result = GameInstallFolderService.CreateOrValidateGameInstallFolder(package, targetBaseDir);

        if (result.Status == GameInstallFolderStatus.AlreadyExists)
        {
            var pathStr = result.Path ?? string.Empty;
            ViewModel.StatusSummary = Loc.Format("Status_GameInstallFolderCreated", pathStr);
            MessageBox.Show(
                this,
                Loc.Format("Dialog_GameInstallFolderExistsMessage", pathStr),
                Loc.Get("Dialog_GameInstallFolderExistsTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        else if (result.Status == GameInstallFolderStatus.Created)
        {
            var pathStr = result.Path ?? string.Empty;
            ViewModel.StatusSummary = Loc.Format("Status_GameInstallFolderCreated", pathStr);
            MessageBox.Show(
                this,
                Loc.Format("Dialog_GameInstallFolderCreatedMessage", pathStr),
                Loc.Get("Dialog_GameInstallFolderCreatedTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        else if (result.Status == GameInstallFolderStatus.Failed)
        {
            MessageBox.Show(
                this,
                Loc.Format("Dialog_GameInstallFolderErrorMessage", package.Name, result.ErrorMessage ?? "Unbekannter Fehler"),
                Loc.Get("Dialog_GameInstallFolderErrorTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void UnclipPackage_MenuItem_Click(object sender, RoutedEventArgs e)
    {
        var package = GetPackageFromSender(sender);
        if (package != null && package.IsClipped)
        {
            ViewModel.UnclipPackage(package);
        }
    }

    private void DeletePackage_MenuItem_Click(object sender, RoutedEventArgs e)
    {
        var package = GetPackageFromSender(sender);
        if (package != null)
        {
            ViewModel.RemovePackage(package);
        }
    }

    private void RetryItem_MenuItem_Click(object sender, RoutedEventArgs e)
    {
        var item = GetItemFromSender(sender);
        if (item != null)
        {
            if (item.Status == Models.DownloadStatus.Completed)
                return;

            bool wasBrowserClosed = item.StatusMessage == Services.Localization.Loc.Get("Status_BrowserWindowClosed") ||
                                    item.StatusMessage.Contains("Browser window closed", StringComparison.OrdinalIgnoreCase) ||
                                    item.StatusMessage.Contains("Browser-Fenster geschlossen", StringComparison.OrdinalIgnoreCase);

            if (wasBrowserClosed || item.Status == Models.DownloadStatus.Failed)
            {
                ViewModel.RetryItem(item);
            }
            else
            {
                ViewModel.ToggleItemPause(item);
            }
        }
    }

    private void CopyItemUrl_MenuItem_Click(object sender, RoutedEventArgs e)
    {
        var item = GetItemFromSender(sender);
        if (item != null)
        {
            ViewModel.CopyItemUrl(item);
        }
    }

    private void DeleteItem_MenuItem_Click(object sender, RoutedEventArgs e)
    {
        var item = GetItemFromSender(sender);
        if (item != null)
        {
            ViewModel.RemoveItem(item);
        }
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        var currentDir = SettingsService.Instance.Settings.DefaultDownloadDirectory;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Services.Localization.Loc.Get("Dialog_SelectDefaultDownloadDirTitle"),
            InitialDirectory = Directory.Exists(currentDir) ? currentDir : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        if (dialog.ShowDialog(this) == true)
        {
            SettingsService.Instance.Settings.DefaultDownloadDirectory = dialog.FolderName;
            SettingsService.Instance.SaveSettings();
            ViewModel.StatusSummary = Services.Localization.Loc.Format("Status_DownloadDirChanged", dialog.FolderName);
            MessageBox.Show(
                Services.Localization.Loc.Format("Dialog_DownloadDirChangedMessage", dialog.FolderName), 
                Services.Localization.Loc.Get("Common_SettingsSaved"), 
                MessageBoxButton.OK, 
                MessageBoxImage.Information);
        }
    }

    #region Selection & Inline Renaming Handlers

    private void PackageRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadPackage pkg)
        {
            // Toggle: clicking an already selected row deselects everything
            if (pkg.IsSelected)
            {
                foreach (var p in ViewModel.Packages)
                {
                    p.IsSelected = false;
                    foreach (var item in p.Items)
                    {
                        item.IsSelected = false;
                    }
                }

                ViewModel.SelectedPackage = null;
                ViewModel.SelectedItem = null;
                return;
            }

            foreach (var p in ViewModel.Packages)
            {
                p.IsSelected = false;
                foreach (var item in p.Items)
                {
                    item.IsSelected = false;
                }
            }

            pkg.IsSelected = true;
            ViewModel.SelectedPackage = pkg;
            ViewModel.SelectedItem = null;
            (sender as UIElement)?.Focus();
        }
    }

    private void PackageRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadPackage pkg)
        {
            foreach (var p in ViewModel.Packages)
            {
                p.IsSelected = false;
                foreach (var item in p.Items)
                {
                    item.IsSelected = false;
                }
            }

            pkg.IsSelected = true;
            ViewModel.SelectedPackage = pkg;
            ViewModel.SelectedItem = null;
            (sender as UIElement)?.Focus();
        }
    }

    private void ItemRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadItem item)
        {
            foreach (var p in ViewModel.Packages)
            {
                p.IsSelected = false;
                foreach (var i in p.Items)
                {
                    i.IsSelected = false;
                }
            }

            item.IsSelected = true;
            ViewModel.SelectedItem = item;
            ViewModel.SelectedPackage = null;
            (sender as UIElement)?.Focus();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 1. If currently recording a shortcut in Settings
        if (ViewModel.RecordingShortcut != null)
        {
            var rawKey = e.Key == Key.System ? e.SystemKey : e.Key;
            var mods = Keyboard.Modifiers;

            // Ignore standalone modifiers
            if (rawKey is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            {
                e.Handled = true;
                return;
            }

            if (rawKey == Key.Escape && mods == ModifierKeys.None)
            {
                ViewModel.CancelRecordingShortcut();
                e.Handled = true;
                return;
            }

            ViewModel.FinishRecordingShortcut(rawKey, mods);
            e.Handled = true;
            return;
        }

        // 2. Cancel column header drag if in progress
        if (e.Key == Key.Escape && _isDraggingColumnHeader)
        {
            CancelHeaderDragState();
            e.Handled = true;
            return;
        }

        // 3. Do not intercept if actively typing inside an inline editing TextBox or any input control
        if (e.OriginalSource is TextBox or PasswordBox or RichTextBox)
            return;
        if (Keyboard.FocusedElement is TextBox or PasswordBox or RichTextBox)
            return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        // Browser-style back/forward navigation shortcuts
        if ((key == Key.Back && modifiers == ModifierKeys.None) ||
            (key == Key.Left && modifiers == ModifierKeys.Alt) ||
            key == Key.BrowserBack)
        {
            if (ViewModel.CanGoBack)
            {
                ViewModel.GoBackCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }
        else if ((key == Key.Right && modifiers == ModifierKeys.Alt) ||
                 key == Key.BrowserForward)
        {
            if (ViewModel.CanGoForward)
            {
                ViewModel.GoForwardCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }

        bool isDownloadsTab = ViewModel.SelectedMainTab == AppMainTab.Downloads;

        // 3. Match against dynamic keyboard shortcuts
        var matched = ViewModel.ShortcutManager.FindMatch(key, modifiers);
        if (matched != null)
        {
            // If in Settings tab, ignore all download-tab-specific shortcuts
            if (!isDownloadsTab)
            {
                switch (matched.Id)
                {
                    case "TogglePauseResume":
                    case "AddLinks":
                    case "ToggleExpandCollapse":
                    case "DeleteSelected":
                    case "RenameSelected":
                    case "SelectAll":
                    case "DeselectAll":
                    case "StartAll":
                    case "PauseAll":
                    case "ClearCompleted":
                    case "ImportPackage":
                        return;
                }
            }

            switch (matched.Id)
            {
                case "TogglePauseResume":
                    if (ViewModel.TogglePauseResumeCommand.CanExecute(null))
                    {
                        ViewModel.TogglePauseResumeCommand.Execute(null);
                        e.Handled = true;
                    }
                    return;

                case "AddLinks":
                    ShowAddLinksDialog();
                    e.Handled = true;
                    return;

                case "ToggleExpandCollapse":
                    if (ViewModel.ToggleExpandCollapseAllCommand.CanExecute(null))
                    {
                        ViewModel.ToggleExpandCollapseAllCommand.Execute(null);
                        e.Handled = true;
                    }
                    return;

                case "DeleteSelected":
                    HandleDeleteSelected();
                    e.Handled = true;
                    return;

                case "RenameSelected":
                    HandleRenameSelected();
                    e.Handled = true;
                    return;

                case "SelectAll":
                    ViewModel.SelectAll();
                    e.Handled = true;
                    return;

                case "DeselectAll":
                    ViewModel.DeselectAll();
                    e.Handled = true;
                    return;

                case "StartAll":
                    if (ViewModel.StartAllCommand.CanExecute(null))
                    {
                        ViewModel.StartAllCommand.Execute(null);
                        e.Handled = true;
                    }
                    return;

                case "PauseAll":
                    if (ViewModel.PauseAllCommand.CanExecute(null))
                    {
                        ViewModel.PauseAllCommand.Execute(null);
                        e.Handled = true;
                    }
                    return;

                case "ClearCompleted":
                    if (ViewModel.ClearCompletedCommand.CanExecute(null))
                    {
                        ViewModel.ClearCompletedCommand.Execute(null);
                        e.Handled = true;
                    }
                    return;

                case "OpenDownloadFolder":
                    ViewModel.OpenDownloadDirectoryInExplorer();
                    e.Handled = true;
                    return;

                case "OpenExtensionsFolder":
                    ViewModel.OpenExtensionsFolder();
                    e.Handled = true;
                    return;

                case "OpenSettings":
                case "ShowSettings":
                    ViewModel.SelectedMainTab = AppMainTab.Settings;
                    ViewModel.SelectedSettingsCategory = SettingsCategory.Shortcuts;
                    e.Handled = true;
                    return;

                case "ShowDownloads":
                    ViewModel.SelectedMainTab = AppMainTab.Downloads;
                    e.Handled = true;
                    return;

                case "ImportPackage":
                    _ = ViewModel.ImportPackage();
                    e.Handled = true;
                    return;

                case "RestartApp":
                    ViewModel.RestartApplicationCommand.Execute(null);
                    e.Handled = true;
                    return;
            }
        }

        // 4. Explicit fallback for F2 Rename even if shortcut manager didn't match
        if (isDownloadsTab && key == Key.F2 && modifiers == ModifierKeys.None)
        {
            HandleRenameSelected();
            e.Handled = true;
            return;
        }

        // 5. Explicit fallback for Delete / Back even if shortcut manager didn't match
        if (isDownloadsTab && (key == Key.Delete || key == Key.Back) && modifiers == ModifierKeys.None)
        {
            HandleDeleteSelected();
            e.Handled = true;
            return;
        }
    }

    private void HandleRenameSelected()
    {
        if (ViewModel.SelectedMainTab != AppMainTab.Downloads)
            return;

        // 1. Check if an item is selected
        if (ViewModel.SelectedItem != null && ViewModel.SelectedItem.IsSelected)
        {
            ViewModel.SelectedItem.IsEditing = true;
            return;
        }

        // 2. Check if a package is selected
        if (ViewModel.SelectedPackage != null && ViewModel.SelectedPackage.IsSelected)
        {
            ViewModel.SelectedPackage.IsEditing = true;
            return;
        }

        // 3. Fallback: search items across root and clipped packages
        var item = ViewModel.Packages.SelectMany(p => p.Items).FirstOrDefault(i => i.IsSelected)
                   ?? ViewModel.Packages.SelectMany(p => p.ClippedPackages).SelectMany(cp => cp.Items).FirstOrDefault(i => i.IsSelected);
        if (item != null)
        {
            ViewModel.SelectedItem = item;
            item.IsEditing = true;
            return;
        }

        // 4. Fallback: search packages across root and clipped packages
        var pkg = ViewModel.Packages.FirstOrDefault(p => p.IsSelected)
                  ?? ViewModel.Packages.SelectMany(p => p.ClippedPackages).FirstOrDefault(cp => cp.IsSelected);
        if (pkg != null)
        {
            ViewModel.SelectedPackage = pkg;
            pkg.IsEditing = true;
            return;
        }
    }

    private void RenameTextBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox tb && tb.IsVisible)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                tb.Focus();
                tb.SelectAll();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void HandleDeleteSelected()
    {
        if (ViewModel.SelectedMainTab != AppMainTab.Downloads)
            return;

        // 1. Check if an item is selected
        var itemToDelete = ViewModel.SelectedItem ?? ViewModel.Packages.SelectMany(p => p.Items).FirstOrDefault(i => i.IsSelected);
        if (itemToDelete != null)
        {
            ViewModel.SelectedItem = itemToDelete;
            ViewModel.RemoveItem(itemToDelete);
            return;
        }

        // 2. Check if a package is selected
        var packageToDelete = ViewModel.SelectedPackage ?? ViewModel.Packages.FirstOrDefault(p => p.IsSelected);
        if (packageToDelete != null)
        {
            ViewModel.SelectedPackage = packageToDelete;
            ViewModel.RemovePackage(packageToDelete);
            return;
        }

        if (ViewModel.DeleteSelectedCommand.CanExecute(null))
        {
            ViewModel.DeleteSelectedCommand.Execute(null);
        }
    }

    private void ItemRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadItem item)
        {
            // Toggle: clicking an already selected row deselects everything
            if (item.IsSelected)
            {
                foreach (var p in ViewModel.Packages)
                {
                    p.IsSelected = false;
                    foreach (var i in p.Items)
                    {
                        i.IsSelected = false;
                    }
                }

                ViewModel.SelectedPackage = null;
                ViewModel.SelectedItem = null;
                return;
            }

            foreach (var p in ViewModel.Packages)
            {
                p.IsSelected = false;
                foreach (var i in p.Items)
                {
                    i.IsSelected = false;
                }
            }

            item.IsSelected = true;
            ViewModel.SelectedItem = item;
            ViewModel.SelectedPackage = null;
            (sender as UIElement)?.Focus();
        }
    }

    private void PackageName_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && (sender as FrameworkElement)?.DataContext is DownloadPackage pkg)
        {
            pkg.IsEditing = true;
            e.Handled = true;
        }
    }

    private void ItemName_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && (sender as FrameworkElement)?.DataContext is DownloadItem item)
        {
            item.IsEditing = true;
            e.Handled = true;
        }
    }

    private void PackageRenameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if ((sender as TextBox)?.DataContext is DownloadPackage pkg)
        {
            if (e.Key == Key.Enter)
            {
                var tb = sender as TextBox;
                if (tb != null)
                {
                    pkg.Rename(tb.Text);
                }
                pkg.IsEditing = false;
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                pkg.IsEditing = false;
                e.Handled = true;
            }
        }
    }

    private void PackageRenameTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if ((sender as TextBox)?.DataContext is DownloadPackage pkg)
        {
            var tb = sender as TextBox;
            if (tb != null)
            {
                pkg.Rename(tb.Text);
            }
            pkg.IsEditing = false;
        }
    }

    private void ItemRenameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if ((sender as TextBox)?.DataContext is DownloadItem item)
        {
            if (e.Key == Key.Enter)
            {
                var tb = sender as TextBox;
                if (tb != null)
                {
                    item.Rename(tb.Text);
                }
                item.IsEditing = false;
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                item.IsEditing = false;
                e.Handled = true;
            }
        }
    }

    private void ItemRenameTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if ((sender as TextBox)?.DataContext is DownloadItem item)
        {
            var tb = sender as TextBox;
            if (tb != null)
            {
                item.Rename(tb.Text);
            }
            item.IsEditing = false;
        }
    }

    private void MaxDownloads_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0)
        {
            ViewModel.IncrementMaxDownloads();
        }
        else if (e.Delta < 0)
        {
            ViewModel.DecrementMaxDownloads();
        }
        e.Handled = true;
    }

    private void Connections_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0)
        {
            ViewModel.IncrementConnections();
        }
        else if (e.Delta < 0)
        {
            ViewModel.DecrementConnections();
        }
        e.Handled = true;
    }

    private void SpeedLimit_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0)
        {
            ViewModel.IncrementSpeedLimit();
        }
        else if (e.Delta < 0)
        {
            ViewModel.DecrementSpeedLimit();
        }
        e.Handled = true;
    }

    private void RenamePackage_MenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is DownloadPackage pkg)
        {
            pkg.IsEditing = true;
        }
    }

    private void RenameItem_MenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is DownloadItem item)
        {
            item.IsEditing = true;
        }
    }

    private void TreeListScrollViewer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
    }

    private void TreeList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var scv = sender as ScrollViewer ?? TreeListScrollViewer;
        if (scv != null)
        {
            scv.ScrollToVerticalOffset(scv.VerticalOffset - e.Delta);
            e.Handled = true;
        }
    }

    private void ScrollToTop_Click(object sender, RoutedEventArgs e)
    {
        TreeListScrollViewer?.ScrollToTop();
    }

    private void ScrollToBottom_Click(object sender, RoutedEventArgs e)
    {
        TreeListScrollViewer?.ScrollToBottom();
    }

    private bool _isResizingColumn;
    private string? _resizingColumnTag;
    private double _resizeStartColWidth;
    private double _resizeStartNameWidth;
    private Dictionary<string, double> _resizeStartMetadataWidths = new();
    private double _resizeStartMouseScreenX;

    private bool _headerMouseDown;
    private bool _isDraggingColumnHeader;
    private string? _draggedColumnTag;
    private FrameworkElement? _draggedHeaderElement;
    private Point _headerMouseDownPos;
    private Point _headerMouseDownGridPos;
    private double _draggedColumnStartLeft;
    private double _draggedColumnWidth;
    private double _draggedGrabOffsetX;
    private string? _dropTargetBeforeCol;
    private bool _dropAtEnd;

    internal static double GetColumnLeft(MainViewModel? vm, string col)
    {
        if (vm == null) return 0;
        var order = vm.ColumnOrder;
        if (order != null && order.Count > 0)
        {
            double left = 0;
            foreach (var c in order)
            {
                if (c == col) return left;
                if (vm.IsColumnVisible(c))
                    left += vm.GetColumnPixelWidth(c);
            }
            return left;
        }

        return 0;
    }

    internal static double CalculateDraggedHeaderBarLeft(double mouseGridX, double grabOffsetX, double containerWidth, double barWidth)
    {
        double maxLeft = Math.Max(0, containerWidth - barWidth);
        return Math.Clamp(mouseGridX - grabOffsetX, 0, maxLeft);
    }

    internal static (string? targetBefore, bool atEnd, double indicatorX) CalculateDropInsertion(
        MainViewModel? vm,
        double mouseX)
    {
        if (vm == null) return (null, false, 0);
        var order = vm.ColumnOrder;
        if (order == null || order.Count == 0) return (null, false, 0);

        List<string> visibleCols = new();
        foreach (var c in order)
        {
            if (vm.IsColumnVisible(c))
                visibleCols.Add(c);
        }

        if (visibleCols.Count == 0) return (null, false, 0);

        string? targetBefore = null;
        bool atEnd = false;
        double indicatorX = 0;
        double currentLeft = 0;
        bool found = false;

        for (int i = 0; i < visibleCols.Count; i++)
        {
            string col = visibleCols[i];
            double colWidth = vm.GetColumnPixelWidth(col);
            double midPoint = currentLeft + (colWidth / 2.0);

            if (mouseX < midPoint)
            {
                targetBefore = col;
                indicatorX = currentLeft;
                found = true;
                break;
            }

            currentLeft += colWidth;
        }

        if (!found)
        {
            atEnd = true;
            indicatorX = currentLeft;
        }

        return (targetBefore, atEnd, indicatorX);
    }

    internal static void SetColumnWidth(MainViewModel? vm, string col, double width)
    {
        if (vm == null) return;
        switch (col)
        {
            case "Name": vm.ColWidthName = width; break;
            case "Hoster": vm.ColWidthHoster = width; break;
            case "SavePath": vm.ColWidthSavePath = width; break;
            case "Size": vm.ColWidthSize = width; break;
            case "Progress": vm.ColWidthProgress = width; break;
            case "Speed": vm.ColWidthSpeed = width; break;
            case "Eta": vm.ColWidthEta = width; break;
            case "Status": vm.ColWidthStatus = width; break;
            case "AddedDate": vm.ColWidthAddedDate = width; break;
            case "CompletedDate": vm.ColWidthCompletedDate = width; break;
            case "Checksum": vm.ColWidthChecksum = width; break;
            case "Actions": vm.ColWidthActions = width; break;
        }
    }

    private void ColumnConfigButton_Click(object sender, RoutedEventArgs e)
    {
        if (ColumnHeaderContextMenu != null && sender is FrameworkElement el)
        {
            ColumnHeaderContextMenu.PlacementTarget = el;
            ColumnHeaderContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            ColumnHeaderContextMenu.IsOpen = true;
        }
    }



    private bool _isAutoFittingName;
    public void AutoFitNameColumn()
    {
        if (ViewModel == null || _isResizingColumn || _isAutoFittingName) return;

        double viewerWidth = (HeaderViewportBorder?.ActualWidth > 50 
            ? HeaderViewportBorder.ActualWidth 
            : (TreeListScrollViewer?.ActualWidth > 50 ? TreeListScrollViewer.ActualWidth : ActualWidth) - 18);

        if (viewerWidth <= 50) return;

        // All visible columns fit perfectly within the visible viewport width (up to the gear button).
        // The table right edge aligns flush with the gear button border (targetTableWidth = Math.Floor(viewerWidth)).
        double targetTableWidth = Math.Floor(viewerWidth);
        double metadataWidth = GetVisibleMetadataSum(ViewModel);

        const double minNameWidth = 80.0;
        double remaining = targetTableWidth - metadataWidth;

        _isAutoFittingName = true;
        try
        {
            if (remaining >= minNameWidth)
            {
                if (Math.Abs(ViewModel.ColWidthName - remaining) > 0.5)
                {
                    ViewModel.ColWidthName = Math.Round(remaining, 1);
                }
            }
            else
            {
                if (Math.Abs(ViewModel.ColWidthName - minNameWidth) > 0.5)
                {
                    ViewModel.ColWidthName = minNameWidth;
                }
                CompressMetadataColumnsToFit(ViewModel, targetTableWidth - minNameWidth);
            }
        }
        finally
        {
            _isAutoFittingName = false;
        }
    }

    internal static void CompressMetadataColumnsToFit(MainViewModel vm, double availableMetadataWidth)
    {
        var visibleCols = new List<string>();
        if (vm.ShowColHoster) visibleCols.Add("Hoster");
        if (vm.ShowColSavePath) visibleCols.Add("SavePath");
        if (vm.ShowColSize) visibleCols.Add("Size");
        if (vm.ShowColProgress) visibleCols.Add("Progress");
        if (vm.ShowColSpeed) visibleCols.Add("Speed");
        if (vm.ShowColEta) visibleCols.Add("Eta");
        if (vm.ShowColStatus) visibleCols.Add("Status");
        if (vm.ShowColAddedDate) visibleCols.Add("AddedDate");
        if (vm.ShowColCompletedDate) visibleCols.Add("CompletedDate");
        if (vm.ShowColChecksum) visibleCols.Add("Checksum");
        if (vm.ShowColActions) visibleCols.Add("Actions");

        if (visibleCols.Count == 0) return;

        double currentSum = visibleCols.Sum(c => GetColumnWidth(vm, c));
        double minSum = visibleCols.Sum(c => GetColumnMinWidth(c));

        if (currentSum <= availableMetadataWidth) return;

        if (availableMetadataWidth <= minSum)
        {
            foreach (var col in visibleCols)
            {
                SetColumnWidth(vm, col, GetColumnMinWidth(col));
            }
            return;
        }

        double excessToReduce = currentSum - availableMetadataWidth;
        double totalReducible = visibleCols.Sum(c => Math.Max(0, GetColumnWidth(vm, c) - GetColumnMinWidth(c)));

        if (totalReducible > 0)
        {
            double sumAssigned = 0;
            for (int i = 0; i < visibleCols.Count - 1; i++)
            {
                string col = visibleCols[i];
                double cur = GetColumnWidth(vm, col);
                double min = GetColumnMinWidth(col);
                double reducible = Math.Max(0, cur - min);
                double share = reducible / totalReducible;
                double newWidth = Math.Max(min, Math.Round(cur - (excessToReduce * share), 1));
                SetColumnWidth(vm, col, newWidth);
                sumAssigned += newWidth;
            }
            string lastCol = visibleCols[^1];
            double lastWidth = Math.Max(GetColumnMinWidth(lastCol), Math.Round(availableMetadataWidth - sumAssigned, 1));
            SetColumnWidth(vm, lastCol, lastWidth);
        }
    }

    public void AutoFitAllColumns()
    {
        if (ViewModel == null) return;

        // Auto-fit all active metadata columns to clean, optimal compact widths
        ViewModel.AutoFitColumns();

        // Dynamically size Name column so all visible columns fit perfectly within the visible viewport width
        AutoFitNameColumn();
        SaveColumnWidths();
    }

    internal static string? GetLastVisibleColumn(MainViewModel? vm)
    {
        if (vm == null) return null;
        var order = vm.ColumnOrder;
        if (order != null && order.Count > 0)
        {
            for (int i = order.Count - 1; i >= 0; i--)
            {
                var col = order[i];
                if (vm.IsColumnVisible(col))
                    return col;
            }
        }

        if (vm.ShowColActions) return "Actions";
        if (vm.ShowColChecksum) return "Checksum";
        if (vm.ShowColCompletedDate) return "CompletedDate";
        if (vm.ShowColAddedDate) return "AddedDate";
        if (vm.ShowColStatus) return "Status";
        if (vm.ShowColEta) return "Eta";
        if (vm.ShowColSpeed) return "Speed";
        if (vm.ShowColProgress) return "Progress";
        if (vm.ShowColSize) return "Size";
        if (vm.ShowColSavePath) return "SavePath";
        if (vm.ShowColHoster) return "Hoster";
        if (vm.ShowColName) return "Name";
        return null;
    }

    internal static double GetColumnMinWidth(string col) => col switch
    {
        "Name" => 80.0,
        "Hoster" => 36.0,
        "SavePath" => 50.0,
        "Size" => 36.0,
        "Progress" => 50.0,
        "Speed" => 40.0,
        "Eta" => 36.0,
        "Status" => 40.0,
        "AddedDate" => 50.0,
        "CompletedDate" => 50.0,
        "Checksum" => 50.0,
        "Actions" => 40.0,
        _ => 36.0
    };

    internal static double GetColumnWidth(MainViewModel? vm, string col)
    {
        if (vm == null) return 100;
        return col switch
        {
            "Name" => vm.ColWidthName,
            "Hoster" => vm.ColWidthHoster,
            "SavePath" => vm.ColWidthSavePath,
            "Size" => vm.ColWidthSize,
            "Progress" => vm.ColWidthProgress,
            "Speed" => vm.ColWidthSpeed,
            "Eta" => vm.ColWidthEta,
            "Status" => vm.ColWidthStatus,
            "AddedDate" => vm.ColWidthAddedDate,
            "CompletedDate" => vm.ColWidthCompletedDate,
            "Checksum" => vm.ColWidthChecksum,
            "Actions" => vm.ColWidthActions,
            _ => 100
        };
    }

    internal static Dictionary<string, double> GetVisibleMetadataWidths(MainViewModel vm)
    {
        var dict = new Dictionary<string, double>();
        if (vm.ShowColHoster) dict["Hoster"] = vm.ColWidthHoster;
        if (vm.ShowColSavePath) dict["SavePath"] = vm.ColWidthSavePath;
        if (vm.ShowColSize) dict["Size"] = vm.ColWidthSize;
        if (vm.ShowColProgress) dict["Progress"] = vm.ColWidthProgress;
        if (vm.ShowColSpeed) dict["Speed"] = vm.ColWidthSpeed;
        if (vm.ShowColEta) dict["Eta"] = vm.ColWidthEta;
        if (vm.ShowColStatus) dict["Status"] = vm.ColWidthStatus;
        if (vm.ShowColAddedDate) dict["AddedDate"] = vm.ColWidthAddedDate;
        if (vm.ShowColCompletedDate) dict["CompletedDate"] = vm.ColWidthCompletedDate;
        if (vm.ShowColChecksum) dict["Checksum"] = vm.ColWidthChecksum;
        if (vm.ShowColActions) dict["Actions"] = vm.ColWidthActions;
        return dict;
    }

    internal static double GetVisibleMetadataSum(MainViewModel vm)
    {
        return (vm.ShowColHoster ? vm.ColWidthHoster : 0) +
               (vm.ShowColSavePath ? vm.ColWidthSavePath : 0) +
               (vm.ShowColSize ? vm.ColWidthSize : 0) +
               (vm.ShowColProgress ? vm.ColWidthProgress : 0) +
               (vm.ShowColSpeed ? vm.ColWidthSpeed : 0) +
               (vm.ShowColEta ? vm.ColWidthEta : 0) +
               (vm.ShowColStatus ? vm.ColWidthStatus : 0) +
               (vm.ShowColAddedDate ? vm.ColWidthAddedDate : 0) +
               (vm.ShowColCompletedDate ? vm.ColWidthCompletedDate : 0) +
               (vm.ShowColChecksum ? vm.ColWidthChecksum : 0) +
               (vm.ShowColActions ? vm.ColWidthActions : 0);
    }

    internal static void ResetSingleColumnWidth(MainViewModel? vm, string col)
    {
        if (vm == null) return;

        switch (col)
        {
            case "Name": vm.ColWidthName = 220; break;
            case "Hoster": vm.ColWidthHoster = 75; break;
            case "SavePath": vm.ColWidthSavePath = 140; break;
            case "Size": vm.ColWidthSize = 75; break;
            case "Progress": vm.ColWidthProgress = 110; break;
            case "Speed": vm.ColWidthSpeed = 80; break;
            case "Eta": vm.ColWidthEta = 65; break;
            case "Status": vm.ColWidthStatus = 95; break;
            case "AddedDate": vm.ColWidthAddedDate = 95; break;
            case "CompletedDate": vm.ColWidthCompletedDate = 95; break;
            case "Checksum": vm.ColWidthChecksum = 90; break;
            case "Actions": vm.ColWidthActions = 95; break;
        }
    }

    private void ResetSingleColumn(string col)
    {
        if (ViewModel == null) return;
        ResetSingleColumnWidth(ViewModel, col);
        AutoFitNameColumn();
        SaveColumnWidths();
    }

    private void ColumnHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is string col && ViewModel != null)
        {
            _headerMouseDown = true;
            _isDraggingColumnHeader = false;
            _draggedColumnTag = col;
            _draggedHeaderElement = el;
            _headerMouseDownPos = e.GetPosition(this);
            if (HeaderColumnsGrid != null)
            {
                _headerMouseDownGridPos = e.GetPosition(HeaderColumnsGrid);
                _draggedColumnStartLeft = GetColumnLeft(ViewModel, col);
                _draggedColumnWidth = el.ActualWidth > 0 ? el.ActualWidth : ViewModel.GetColumnPixelWidth(col);
                _draggedGrabOffsetX = Math.Max(0, _headerMouseDownGridPos.X - _draggedColumnStartLeft);
            }
            _dropTargetBeforeCol = null;
            _dropAtEnd = false;
            el.CaptureMouse();
            e.Handled = true;
        }
    }

    private void ColumnHeader_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_headerMouseDown || _draggedColumnTag == null || ViewModel == null)
            return;

        Point curPos = e.GetPosition(this);

        if (!_isDraggingColumnHeader)
        {
            double diffX = Math.Abs(curPos.X - _headerMouseDownPos.X);
            double diffY = Math.Abs(curPos.Y - _headerMouseDownPos.Y);
            if (diffX >= SystemParameters.MinimumHorizontalDragDistance || diffY >= SystemParameters.MinimumVerticalDragDistance)
            {
                StartHeaderDrag(e);
            }
        }

        if (_isDraggingColumnHeader)
        {
            UpdateHeaderDrag(e);
            e.Handled = true;
        }
    }

    private void StartHeaderDrag(MouseEventArgs e)
    {
        _isDraggingColumnHeader = true;

        if (_draggedHeaderElement != null)
        {
            _draggedHeaderElement.Opacity = 0.35;
        }

        if (DraggedHeaderBar != null && DraggedHeaderBarText != null && _draggedColumnTag != null && ViewModel != null)
        {
            double colWidth = _draggedHeaderElement != null && _draggedHeaderElement.ActualWidth > 0
                ? _draggedHeaderElement.ActualWidth
                : (_draggedColumnWidth > 0 ? _draggedColumnWidth : ViewModel.GetColumnPixelWidth(_draggedColumnTag));

            DraggedHeaderBar.Width = colWidth;
            if (_draggedHeaderElement != null && _draggedHeaderElement.ActualHeight > 0)
            {
                DraggedHeaderBar.Height = _draggedHeaderElement.ActualHeight;
            }
            DraggedHeaderBarText.Text = GetColumnDisplayName(_draggedColumnTag);

            if (DraggedHeaderBarSortArrow != null)
            {
                if (ViewModel.SortColumn != null && string.Equals(ViewModel.SortColumn, _draggedColumnTag, StringComparison.OrdinalIgnoreCase) && ViewModel.SortDirection.HasValue)
                {
                    DraggedHeaderBarSortArrow.Data = ViewModel.SortDirection.Value == ListSortDirection.Ascending
                        ? Geometry.Parse("M 0 5 L 4 0 L 8 5 Z")
                        : Geometry.Parse("M 0 0 L 4 5 L 8 0 Z");
                    DraggedHeaderBarSortArrow.Visibility = Visibility.Visible;
                }
                else
                {
                    DraggedHeaderBarSortArrow.Visibility = Visibility.Collapsed;
                }
            }

            Canvas.SetTop(DraggedHeaderBar, 0);
            Canvas.SetLeft(DraggedHeaderBar, _draggedColumnStartLeft);
            DraggedHeaderBar.Visibility = Visibility.Visible;
        }
    }

    private void UpdateHeaderDrag(MouseEventArgs e)
    {
        if (HeaderColumnsGrid == null || ViewModel == null || _draggedColumnTag == null)
            return;

        Point curGridPos = e.GetPosition(HeaderColumnsGrid);

        // Slide the full dragged column bar horizontally directly locked to the user's mouse grip
        if (DraggedHeaderBar != null)
        {
            double barWidth = DraggedHeaderBar.Width > 0 ? DraggedHeaderBar.Width : _draggedColumnWidth;
            double barLeft = CalculateDraggedHeaderBarLeft(curGridPos.X, _draggedGrabOffsetX, HeaderColumnsGrid.ActualWidth, barWidth);
            Canvas.SetLeft(DraggedHeaderBar, barLeft);
            Canvas.SetTop(DraggedHeaderBar, 0);
        }

        var (targetBefore, atEnd, indicatorX) = CalculateDropInsertion(ViewModel, curGridPos.X);
        _dropTargetBeforeCol = targetBefore;
        _dropAtEnd = atEnd;

        if (ColumnDropIndicator != null)
        {
            ColumnDropIndicator.Margin = new Thickness(Math.Max(0, indicatorX - 1), 0, 0, 0);
            ColumnDropIndicator.Visibility = Visibility.Visible;
        }
    }

    private void FinishHeaderDrag()
    {
        if (ViewModel == null || _draggedColumnTag == null)
            return;

        if (_dropAtEnd)
        {
            string? lastVisible = GetLastVisibleColumn(ViewModel);
            if (lastVisible != null && lastVisible != _draggedColumnTag)
            {
                ViewModel.MoveColumnAfter(_draggedColumnTag, lastVisible);
            }
        }
        else if (_dropTargetBeforeCol != null && _dropTargetBeforeCol != _draggedColumnTag)
        {
            ViewModel.MoveColumnBefore(_draggedColumnTag, _dropTargetBeforeCol);
        }

        SaveColumnWidths();
    }

    private void CancelHeaderDragState()
    {
        if (ColumnDropIndicator != null)
            ColumnDropIndicator.Visibility = Visibility.Collapsed;

        if (DraggedHeaderBar != null)
            DraggedHeaderBar.Visibility = Visibility.Collapsed;

        if (_draggedHeaderElement != null)
        {
            _draggedHeaderElement.Opacity = 1.0;
            if (_draggedHeaderElement.IsMouseCaptured)
            {
                _draggedHeaderElement.ReleaseMouseCapture();
            }
            _draggedHeaderElement = null;
        }

        _headerMouseDown = false;
        _isDraggingColumnHeader = false;
        _draggedColumnTag = null;
        _dropTargetBeforeCol = null;
        _dropAtEnd = false;
    }

    private void ColumnHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_headerMouseDown)
        {
            if (_isDraggingColumnHeader)
            {
                FinishHeaderDrag();
            }
            else if (_draggedColumnTag != null && ViewModel != null)
            {
                ViewModel.ToggleColumnSort(_draggedColumnTag);
            }

            CancelHeaderDragState();
            e.Handled = true;
        }
    }

    private void ColumnHeader_LostMouseCapture(object sender, MouseEventArgs e)
    {
        CancelHeaderDragState();
    }

    private static string GetColumnDisplayName(string tag)
    {
        return tag switch
        {
            "Name" => LocalizationService.Instance.Get("Col_Name"),
            "Hoster" => LocalizationService.Instance.Get("Col_Hoster"),
            "SavePath" => LocalizationService.Instance.Get("Col_SavePath"),
            "Size" => LocalizationService.Instance.Get("Col_Size"),
            "Progress" => LocalizationService.Instance.Get("Col_Progress"),
            "Speed" => LocalizationService.Instance.Get("Col_Speed"),
            "Eta" => LocalizationService.Instance.Get("Col_Eta"),
            "Status" => LocalizationService.Instance.Get("Col_Status"),
            "AddedDate" => LocalizationService.Instance.Get("Col_AddedDate"),
            "CompletedDate" => LocalizationService.Instance.Get("Col_CompletedDate"),
            "Checksum" => LocalizationService.Instance.Get("Col_Checksum"),
            "Actions" => LocalizationService.Instance.Get("Col_Actions"),
            _ => tag
        };
    }

    private void HeaderDivider_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is string col && ViewModel != null)
        {
            _isResizingColumn = true;
            _resizingColumnTag = col;
            ViewModel.IsDraggingColumnWidth = true;

            _resizeStartColWidth = GetColumnWidth(ViewModel, col);
            _resizeStartNameWidth = ViewModel.ColWidthName;
            _resizeStartMetadataWidths = GetVisibleMetadataWidths(ViewModel);
            _resizeStartMouseScreenX = e.GetPosition(this).X;

            el.CaptureMouse();
            e.Handled = true;
        }
    }

    internal static void ResizeMetadataColumn(
        MainViewModel vm,
        string col,
        double startColWidth,
        double startNameWidth,
        double deltaX)
    {
        double minColWidth = GetColumnMinWidth(col);
        double requestedColWidth = Math.Max(minColWidth, startColWidth + deltaX);
        double delta = requestedColWidth - startColWidth;

        const double minNameWidth = 80.0;
        double newNameWidth = startNameWidth - delta;

        if (newNameWidth < minNameWidth)
        {
            // Name cannot shrink below minimum (80px)
            newNameWidth = minNameWidth;
            double maxAbsorbable = startNameWidth - minNameWidth;
            requestedColWidth = startColWidth + maxAbsorbable;
        }

        vm.ColWidthName = Math.Round(newNameWidth, 1);
        SetColumnWidth(vm, col, Math.Round(requestedColWidth, 1));
    }

    internal static void ResizeNameColumn(
        MainViewModel vm,
        double startNameWidth,
        Dictionary<string, double> startMetadataWidths,
        double deltaX)
    {
        if (startMetadataWidths.Count == 0) return;

        const double minNameWidth = 80.0;
        double targetNameWidth = Math.Max(minNameWidth, startNameWidth + deltaX);
        double delta = targetNameWidth - startNameWidth;

        if (delta > 0)
        {
            // Name wants to expand: metadata columns must shrink proportionally to absorb delta
            double totalReducible = startMetadataWidths.Sum(kv => Math.Max(0, kv.Value - GetColumnMinWidth(kv.Key)));
            if (totalReducible <= 0) return;

            double actualDelta = Math.Min(delta, totalReducible);
            targetNameWidth = startNameWidth + actualDelta;

            double sumAssigned = 0;
            var list = startMetadataWidths.ToList();
            for (int i = 0; i < list.Count - 1; i++)
            {
                var kv = list[i];
                double colMin = GetColumnMinWidth(kv.Key);
                double colReducible = Math.Max(0, kv.Value - colMin);
                double share = colReducible / totalReducible;
                double newColWidth = Math.Max(colMin, Math.Round(kv.Value - (actualDelta * share), 1));
                SetColumnWidth(vm, kv.Key, newColWidth);
                sumAssigned += (kv.Value - newColWidth);
            }
            var lastKv = list[^1];
            double lastColMin = GetColumnMinWidth(lastKv.Key);
            double lastNewColWidth = Math.Max(lastColMin, Math.Round(lastKv.Value - (actualDelta - sumAssigned), 1));
            SetColumnWidth(vm, lastKv.Key, lastNewColWidth);
        }
        else if (delta < 0)
        {
            // Name wants to shrink: metadata columns expand proportionally to fill freed space
            double freedSpace = -delta;
            double currentMetadataSum = startMetadataWidths.Values.Sum();
            if (currentMetadataSum <= 0) return;

            double sumAssigned = 0;
            var list = startMetadataWidths.ToList();
            for (int i = 0; i < list.Count - 1; i++)
            {
                var kv = list[i];
                double share = kv.Value / currentMetadataSum;
                double added = Math.Round(freedSpace * share, 1);
                double newColWidth = kv.Value + added;
                SetColumnWidth(vm, kv.Key, newColWidth);
                sumAssigned += added;
            }
            var lastKv = list[^1];
            double lastNewColWidth = Math.Round(lastKv.Value + (freedSpace - sumAssigned), 1);
            SetColumnWidth(vm, lastKv.Key, lastNewColWidth);
        }

        vm.ColWidthName = Math.Round(targetNameWidth, 1);
    }

    private void HeaderDivider_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isResizingColumn && _resizingColumnTag != null && sender is FrameworkElement el && el.IsMouseCaptured && ViewModel != null)
        {
            double currentX = e.GetPosition(this).X;
            double deltaX = currentX - _resizeStartMouseScreenX;

            if (_resizingColumnTag == "Name")
            {
                ResizeNameColumn(ViewModel, _resizeStartNameWidth, _resizeStartMetadataWidths, deltaX);
            }
            else
            {
                ResizeMetadataColumn(ViewModel, _resizingColumnTag, _resizeStartColWidth, _resizeStartNameWidth, deltaX);
            }

            e.Handled = true;
        }
    }

    private void EndColumnResize()
    {
        if (!_isResizingColumn) return;

        _isResizingColumn = false;
        _resizingColumnTag = null;
        _resizeStartMetadataWidths.Clear();

        if (ViewModel != null)
        {
            ViewModel.IsDraggingColumnWidth = false;
        }

        SaveColumnWidths();
    }

    private void HeaderDivider_LostMouseCapture(object sender, MouseEventArgs e)
    {
        EndColumnResize();
    }

    private void HeaderDivider_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isResizingColumn && sender is FrameworkElement el)
        {
            el.ReleaseMouseCapture();
            EndColumnResize();
            e.Handled = true;
        }
    }

    private void SaveColumnWidths()
    {
        try
        {
            var settings = SettingsService.Instance.Settings;
            settings.ColWidthName = ViewModel.ColWidthName;
            settings.ColWidthHoster = ViewModel.ColWidthHoster;
            settings.ColWidthSavePath = ViewModel.ColWidthSavePath;
            settings.ColWidthSize = ViewModel.ColWidthSize;
            settings.ColWidthProgress = ViewModel.ColWidthProgress;
            settings.ColWidthSpeed = ViewModel.ColWidthSpeed;
            settings.ColWidthEta = ViewModel.ColWidthEta;
            settings.ColWidthStatus = ViewModel.ColWidthStatus;
            settings.ColWidthAddedDate = ViewModel.ColWidthAddedDate;
            settings.ColWidthCompletedDate = ViewModel.ColWidthCompletedDate;
            settings.ColWidthChecksum = ViewModel.ColWidthChecksum;
            settings.ColWidthActions = ViewModel.ColWidthActions;
            settings.ColumnOrder = new List<string>(ViewModel.ColumnOrder);
            SettingsService.Instance.SaveSettings();
        }
        catch { }
    }

    #endregion

    private long _quickSettingsClosedTimestamp;

    private void QuickSettingsPopup_Closed(object? sender, EventArgs e)
    {
        _quickSettingsClosedTimestamp = Environment.TickCount64;
        ResetQuickSettingsNumericAnimations();
    }

    private void QuickSettingsButton_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (QuickSettingsPopup.IsOpen || (Environment.TickCount64 - _quickSettingsClosedTimestamp < 350))
        {
            QuickSettingsPopup.IsOpen = false;
            _quickSettingsClosedTimestamp = Environment.TickCount64;
            e.Handled = true;
        }
    }

    private void QuickSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (Environment.TickCount64 - _quickSettingsClosedTimestamp < 350)
        {
            return;
        }

        if (QuickSettingsButton.ActualWidth > 0)
        {
            QuickSettingsPopup.HorizontalOffset = QuickSettingsButton.ActualWidth - 352;
        }
        else
        {
            QuickSettingsPopup.HorizontalOffset = -304;
        }
        QuickSettingsPopup.VerticalOffset = -13;
        QuickSettingsPopup.IsOpen = !QuickSettingsPopup.IsOpen;
    }

    private void OpenAllSettings_Click(object sender, RoutedEventArgs e)
    {
        QuickSettingsPopup.IsOpen = false;
        ViewModel.SwitchToSettingsTab();
    }

    #region Quick Settings Smooth Numeric Roll Animation

    private string _lastQuickSpeedLimit = "";
    private string _lastQuickMaxDownloads = "";
    private string _lastQuickConnections = "";

    private void QuickSpeedLimitBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string newText = QuickSpeedLimitBox?.Text ?? "";
        if (!string.IsNullOrEmpty(_lastQuickSpeedLimit) && _lastQuickSpeedLimit != newText &&
            QuickSpeedLimitGhostText != null && QuickSpeedLimitTranslate != null && QuickSpeedLimitGhostTranslate != null)
        {
            AnimateNumericChange(QuickSpeedLimitBox!, QuickSpeedLimitGhostText, QuickSpeedLimitTranslate, QuickSpeedLimitGhostTranslate, _lastQuickSpeedLimit, newText);
        }
        _lastQuickSpeedLimit = newText;
    }

    private void QuickMaxDownloadsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string newText = QuickMaxDownloadsBox?.Text ?? "";
        if (!string.IsNullOrEmpty(_lastQuickMaxDownloads) && _lastQuickMaxDownloads != newText &&
            QuickMaxDownloadsGhostText != null && QuickMaxDownloadsTranslate != null && QuickMaxDownloadsGhostTranslate != null)
        {
            AnimateNumericChange(QuickMaxDownloadsBox!, QuickMaxDownloadsGhostText, QuickMaxDownloadsTranslate, QuickMaxDownloadsGhostTranslate, _lastQuickMaxDownloads, newText);
        }
        _lastQuickMaxDownloads = newText;
    }

    private void QuickConnectionsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string newText = QuickConnectionsBox?.Text ?? "";
        if (!string.IsNullOrEmpty(_lastQuickConnections) && _lastQuickConnections != newText &&
            QuickConnectionsGhostText != null && QuickConnectionsTranslate != null && QuickConnectionsGhostTranslate != null)
        {
            AnimateNumericChange(QuickConnectionsBox!, QuickConnectionsGhostText, QuickConnectionsTranslate, QuickConnectionsGhostTranslate, _lastQuickConnections, newText);
        }
        _lastQuickConnections = newText;
    }

    private void AnimateNumericChange(
        TextBox textBox,
        TextBlock ghostText,
        TranslateTransform textTranslate,
        TranslateTransform ghostTranslate,
        string oldValStr,
        string newValStr)
    {
        if (textBox.IsKeyboardFocused) return;
        if (string.Equals(oldValStr, newValStr, StringComparison.Ordinal)) return;

        if (!double.TryParse(oldValStr.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double oldVal) ||
            !double.TryParse(newValStr.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double newVal))
        {
            return;
        }

        if (Math.Abs(oldVal - newVal) < 0.0001) return;

        bool isUp = newVal > oldVal;
        double offset = 12.0;
        double ghostTargetY = isUp ? -offset : offset;
        double textStartY = isUp ? offset : -offset;

        ghostText.Text = oldValStr;
        ghostText.Opacity = 1.0;
        ghostTranslate.Y = 0.0;

        var duration = TimeSpan.FromMilliseconds(240);
        var bounceEase = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 };
        var smoothEase = new CubicEase { EasingMode = EasingMode.EaseOut };

        // Animate ghost out
        var ghostSlide = new DoubleAnimation(0.0, ghostTargetY, duration) { EasingFunction = smoothEase };
        var ghostFade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(180)) { EasingFunction = smoothEase };
        ghostTranslate.BeginAnimation(TranslateTransform.YProperty, ghostSlide);
        ghostText.BeginAnimation(UIElement.OpacityProperty, ghostFade);

        // Animate textBox in with subtle bounce
        var textSlide = new DoubleAnimation(textStartY, 0.0, duration) { EasingFunction = bounceEase };
        var textFade = new DoubleAnimation(0.0, 1.0, duration) { EasingFunction = smoothEase };

        textFade.Completed += (s, e) =>
        {
            textTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            textTranslate.Y = 0.0;
            textBox.BeginAnimation(UIElement.OpacityProperty, null);
            textBox.Opacity = 1.0;
            ghostText.Opacity = 0.0;
        };

        textTranslate.BeginAnimation(TranslateTransform.YProperty, textSlide);
        textBox.BeginAnimation(UIElement.OpacityProperty, textFade);
    }

    private void ResetQuickSettingsNumericAnimations()
    {
        if (QuickSpeedLimitTranslate != null)
        {
            QuickSpeedLimitTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            QuickSpeedLimitTranslate.Y = 0;
        }
        if (QuickSpeedLimitBox != null)
        {
            QuickSpeedLimitBox.BeginAnimation(UIElement.OpacityProperty, null);
            QuickSpeedLimitBox.Opacity = 1.0;
        }
        if (QuickSpeedLimitGhostText != null)
        {
            QuickSpeedLimitGhostText.Opacity = 0;
        }

        if (QuickMaxDownloadsTranslate != null)
        {
            QuickMaxDownloadsTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            QuickMaxDownloadsTranslate.Y = 0;
        }
        if (QuickMaxDownloadsBox != null)
        {
            QuickMaxDownloadsBox.BeginAnimation(UIElement.OpacityProperty, null);
            QuickMaxDownloadsBox.Opacity = 1.0;
        }
        if (QuickMaxDownloadsGhostText != null)
        {
            QuickMaxDownloadsGhostText.Opacity = 0;
        }

        if (QuickConnectionsTranslate != null)
        {
            QuickConnectionsTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            QuickConnectionsTranslate.Y = 0;
        }
        if (QuickConnectionsBox != null)
        {
            QuickConnectionsBox.BeginAnimation(UIElement.OpacityProperty, null);
            QuickConnectionsBox.Opacity = 1.0;
        }
        if (QuickConnectionsGhostText != null)
        {
            QuickConnectionsGhostText.Opacity = 0;
        }
    }

    #endregion
}
