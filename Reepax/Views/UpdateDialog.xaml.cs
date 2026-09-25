using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Reepax.Services;
using Reepax.Services.Storage;
using Reepax.Services.Update;

namespace Reepax.Views;

public partial class UpdateDialog : Window
{
    private readonly UpdateInfo _updateInfo;

    public UpdateDialog(UpdateInfo updateInfo)
    {
        InitializeComponent();
        _updateInfo = updateInfo ?? throw new ArgumentNullException(nameof(updateInfo));

        CurrentVersionTextBlock.Text = !string.IsNullOrWhiteSpace(updateInfo.CurrentVersion)
            ? updateInfo.CurrentVersion
            : AppUpdateService.AppCurrentVersion;

        NewVersionTextBlock.Text = !string.IsNullOrWhiteSpace(updateInfo.NewVersion)
            ? updateInfo.NewVersion
            : "Update";

        ReleaseDateTextBlock.Text = !string.IsNullOrWhiteSpace(updateInfo.FormattedDate)
            ? updateInfo.FormattedDate
            : DateTime.Now.ToString("dd.MM.yyyy");

        ChangelogViewer.Document = MarkdownDocumentRenderer.CreateFlowDocument(updateInfo.Changelog);

        RestoreWindowBounds();

        LocationChanged += (_, _) => SaveWindowBounds();
        SizeChanged += (_, _) => SaveWindowBounds();
        StateChanged += (_, _) => SaveWindowBounds();

        ThemeService.Instance.ThemeChanged += UpdateTitleBarTheme;
        ThemeService.ApplyDarkTitleBar(this, ThemeService.Instance.IsDarkMode);
    }

    private void UpdateTitleBarTheme(bool isDark)
    {
        ThemeService.ApplyDarkTitleBar(this, isDark);
    }

    private void RestoreWindowBounds()
    {
        try
        {
            var settings = SettingsService.Instance.Settings;
            if (settings.UpdateDialogWidth <= 450 || settings.UpdateDialogHeight > 520)
            {
                // Reset legacy tall/narrow format to new wide/shorter default
                Width = 600;
                Height = 450;
            }
            else
            {
                if (settings.UpdateDialogWidth > 300) Width = settings.UpdateDialogWidth;
                if (settings.UpdateDialogHeight > 200) Height = settings.UpdateDialogHeight;
            }

            if (settings.UpdateDialogLeft.HasValue && settings.UpdateDialogTop.HasValue)
            {
                var virtualLeft = SystemParameters.VirtualScreenLeft;
                var virtualTop = SystemParameters.VirtualScreenTop;
                var virtualWidth = SystemParameters.VirtualScreenWidth;
                var virtualHeight = SystemParameters.VirtualScreenHeight;

                if (settings.UpdateDialogLeft.Value >= virtualLeft - 50 &&
                    settings.UpdateDialogLeft.Value < virtualLeft + virtualWidth - 50 &&
                    settings.UpdateDialogTop.Value >= virtualTop - 50 &&
                    settings.UpdateDialogTop.Value < virtualTop + virtualHeight - 50)
                {
                    Left = settings.UpdateDialogLeft.Value;
                    Top = settings.UpdateDialogTop.Value;
                }
                else
                {
                    CenterOnOwnerOrScreen();
                }
            }
            else
            {
                CenterOnOwnerOrScreen();
            }

            if (settings.IsUpdateDialogMaximized)
            {
                WindowState = WindowState.Maximized;
            }
        }
        catch { }
    }

    private void CenterOnOwnerOrScreen()
    {
        var owner = Owner ?? Application.Current?.MainWindow;
        if (owner != null && owner.IsVisible && owner.WindowState != WindowState.Minimized)
        {
            Left = Math.Max(0, owner.Left + (owner.Width - Width) / 2);
            Top = Math.Max(0, owner.Top + (owner.Height - Height) / 2);
        }
        else
        {
            Left = Math.Max(0, (SystemParameters.PrimaryScreenWidth - Width) / 2);
            Top = Math.Max(0, (SystemParameters.PrimaryScreenHeight - Height) / 2);
        }
    }

    private void SaveWindowBounds()
    {
        if (!IsLoaded) return;
        try
        {
            var settings = SettingsService.Instance.Settings;
            if (WindowState == WindowState.Normal)
            {
                double currentWidth = !double.IsNaN(Width) && Width > 200 ? Width : ActualWidth;
                double currentHeight = !double.IsNaN(Height) && Height > 150 ? Height : ActualHeight;

                if (currentWidth > 200) settings.UpdateDialogWidth = currentWidth;
                if (currentHeight > 150) settings.UpdateDialogHeight = currentHeight;
                settings.UpdateDialogLeft = Left;
                settings.UpdateDialogTop = Top;
                settings.IsUpdateDialogMaximized = false;
            }
            else if (WindowState == WindowState.Maximized)
            {
                settings.IsUpdateDialogMaximized = true;
                if (RestoreBounds.Width > 200 && RestoreBounds.Height > 150)
                {
                    settings.UpdateDialogWidth = RestoreBounds.Width;
                    settings.UpdateDialogHeight = RestoreBounds.Height;
                    settings.UpdateDialogLeft = RestoreBounds.Left;
                    settings.UpdateDialogTop = RestoreBounds.Top;
                }
            }

            SettingsService.Instance.SaveSettings();
        }
        catch { }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        SaveWindowBounds();
    }

    private void GetUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var url = !string.IsNullOrWhiteSpace(_updateInfo.ReleaseUrl)
                ? _updateInfo.ReleaseUrl
                : "https://github.com/Biiitz/Reepax/releases";

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[UpdateDialog] Could not open release url: {ex.Message}");
        }

        DialogResult = true;
        Close();
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ChangelogViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var scv = ChangelogViewer.Template?.FindName("PART_ContentHost", ChangelogViewer) as ScrollViewer
               ?? FindVisualChild<ScrollViewer>(ChangelogViewer);

        if (scv != null)
        {
            scv.ScrollToVerticalOffset(scv.VerticalOffset - (e.Delta * 0.5));
            e.Handled = true;
        }
    }

    private static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent == null) return null;
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild) return typedChild;
            var desc = FindVisualChild<T>(child);
            if (desc != null) return desc;
        }
        return null;
    }

    protected override void OnClosed(EventArgs e)
    {
        ThemeService.Instance.ThemeChanged -= UpdateTitleBarTheme;
        base.OnClosed(e);
    }
}
