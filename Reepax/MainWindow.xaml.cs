using System;
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

    public MainWindow()
    {
        InitializeComponent();
        Title = ViewModel.WindowTitle;
        ThemeService.ApplyDarkTitleBar(this, ThemeService.Instance.IsDarkMode);
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
        SizeChanged += (_, _) => SaveWindowState();

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

    public void ForceExit()
    {
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);

        try
        {
            // Remove tray icon immediately
            Services.SystemIntegration.TrayIconService.Instance.HideTrayIcon();
            Services.SystemIntegration.TrayIconService.Instance.Dispose();

            SaveWindowState();

            var settings = SettingsService.Instance.Settings;

            // Save Column Widths
            settings.ColWidthName = ViewModel.ColWidthName;
            settings.ColWidthHoster = ViewModel.ColWidthHoster;
            settings.ColWidthSize = ViewModel.ColWidthSize;
            settings.ColWidthProgress = ViewModel.ColWidthProgress;
            settings.ColWidthSpeed = ViewModel.ColWidthSpeed;
            settings.ColWidthEta = ViewModel.ColWidthEta;
            settings.ColWidthStatus = ViewModel.ColWidthStatus;
            settings.ColWidthActions = ViewModel.ColWidthActions;

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

        // 2. Do not intercept if actively typing inside an inline editing TextBox or any input control
        if (e.OriginalSource is TextBox or PasswordBox or RichTextBox)
            return;
        if (Keyboard.FocusedElement is TextBox or PasswordBox or RichTextBox)
            return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
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
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) || Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt))
            {
                // Horizontal scrolling when ALT is held down
                scv.ScrollToHorizontalOffset(scv.HorizontalOffset - e.Delta);
            }
            else
            {
                // Instant, 100% fluid vertical scrolling directly tracking hardware wheel events with zero latency
                scv.ScrollToVerticalOffset(scv.VerticalOffset - e.Delta);
            }
            e.Handled = true;
        }
    }

    private void TreeListScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (HeaderGridTransform != null && sender is ScrollViewer scv)
        {
            HeaderGridTransform.X = -scv.HorizontalOffset;
        }
    }

    private bool _isResizingColumn;
    private string? _resizingColumnTag;
    private Point _resizeStartPoint;
    private double _resizeStartWidth;

    private void ColumnConfigButton_Click(object sender, RoutedEventArgs e)
    {
        if (ColumnHeaderContextMenu != null && sender is FrameworkElement el)
        {
            ColumnHeaderContextMenu.PlacementTarget = el;
            ColumnHeaderContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            ColumnHeaderContextMenu.IsOpen = true;
        }
    }

    private void HeaderDivider_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is string col)
        {
            _isResizingColumn = true;
            _resizingColumnTag = col;
            _resizeStartPoint = e.GetPosition(this);
            _resizeStartWidth = col switch
            {
                "Name" => ViewModel.ColWidthName,
                "Hoster" => ViewModel.ColWidthHoster,
                "SavePath" => ViewModel.ColWidthSavePath,
                "Size" => ViewModel.ColWidthSize,
                "Progress" => ViewModel.ColWidthProgress,
                "Speed" => ViewModel.ColWidthSpeed,
                "Eta" => ViewModel.ColWidthEta,
                "Status" => ViewModel.ColWidthStatus,
                "AddedDate" => ViewModel.ColWidthAddedDate,
                "CompletedDate" => ViewModel.ColWidthCompletedDate,
                "Checksum" => ViewModel.ColWidthChecksum,
                "Actions" => ViewModel.ColWidthActions,
                _ => 100
            };
            el.CaptureMouse();
            e.Handled = true;
        }
    }

    private void HeaderDivider_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isResizingColumn && _resizingColumnTag != null && sender is FrameworkElement el && el.IsMouseCaptured)
        {
            Point currentPoint = e.GetPosition(this);
            double deltaX = currentPoint.X - _resizeStartPoint.X;

            double newWidth = Math.Max(36, _resizeStartWidth + deltaX);
            switch (_resizingColumnTag)
            {
                case "Name":
                    ViewModel.ColWidthName = Math.Max(80, newWidth);
                    break;
                case "Hoster":
                    ViewModel.ColWidthHoster = Math.Max(36, newWidth);
                    break;
                case "SavePath":
                    ViewModel.ColWidthSavePath = Math.Max(50, newWidth);
                    break;
                case "Size":
                    ViewModel.ColWidthSize = Math.Max(36, newWidth);
                    break;
                case "Progress":
                    ViewModel.ColWidthProgress = Math.Max(50, newWidth);
                    break;
                case "Speed":
                    ViewModel.ColWidthSpeed = Math.Max(40, newWidth);
                    break;
                case "Eta":
                    ViewModel.ColWidthEta = Math.Max(36, newWidth);
                    break;
                case "Status":
                    ViewModel.ColWidthStatus = Math.Max(40, newWidth);
                    break;
                case "AddedDate":
                    ViewModel.ColWidthAddedDate = Math.Max(50, newWidth);
                    break;
                case "CompletedDate":
                    ViewModel.ColWidthCompletedDate = Math.Max(50, newWidth);
                    break;
                case "Checksum":
                    ViewModel.ColWidthChecksum = Math.Max(50, newWidth);
                    break;
                case "Actions":
                    ViewModel.ColWidthActions = Math.Max(40, newWidth);
                    break;
            }
            e.Handled = true;
        }
    }

    private void HeaderDivider_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isResizingColumn)
        {
            _isResizingColumn = false;
            _resizingColumnTag = null;
            SaveColumnWidths();
        }
    }

    private void HeaderDivider_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isResizingColumn && sender is FrameworkElement el)
        {
            el.ReleaseMouseCapture();
            _isResizingColumn = false;
            _resizingColumnTag = null;
            SaveColumnWidths();
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
            SettingsService.Instance.SaveSettings();
        }
        catch { }
    }

    #endregion

    private long _quickSettingsClosedTimestamp;

    private void QuickSettingsPopup_Closed(object? sender, EventArgs e)
    {
        _quickSettingsClosedTimestamp = Environment.TickCount64;
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
        QuickSettingsPopup.VerticalOffset = -18;
        QuickSettingsPopup.IsOpen = !QuickSettingsPopup.IsOpen;
    }

    private void OpenAllSettings_Click(object sender, RoutedEventArgs e)
    {
        QuickSettingsPopup.IsOpen = false;
        ViewModel.SwitchToSettingsTab();
    }
}