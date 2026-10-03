using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Reepax.Services.Localization;
using Reepax.Services.Storage;

namespace Reepax.Views;

/// <summary>
/// Interaction logic for CrashReportDialog.xaml
/// Provides a modern, dark-themed, transparent error dialog displaying exception details,
/// exact crash log file path, and one-click actions to open the folder or copy the path.
/// </summary>
public partial class CrashReportDialog : Window
{
    private readonly string _logPath;

    public CrashReportDialog(Exception ex, CrashLogResult result, bool isCritical = false)
    {
        InitializeComponent();

        _logPath = result.PrimaryLogPath ?? string.Empty;
        LogPathTextBox.Text = string.IsNullOrEmpty(_logPath) ? "N/A" : _logPath;

        string title = isCritical
            ? Loc.Get("Crash_CriticalErrorTitle")
            : Loc.Get("Crash_UnexpectedErrorTitle");
        Title = title;
        TitleTextBlock.Text = title;

        SubtitleTextBlock.Text = isCritical
            ? (LocalizationService.Instance.CurrentLanguage == "de"
                ? "Ein kritischer Fehler hat das Fortsetzen der Anwendung verhindert."
                : "A critical error prevented the application from continuing.")
            : (LocalizationService.Instance.CurrentLanguage == "de"
                ? "Ein unerwarteter Ausnahmefehler ist aufgetreten."
                : "An unexpected exception has occurred.");

        ErrorMessageTextBlock.Text = $"{ex.GetType().Name}: {ex.Message}";

        if (result.LoggingDisabled)
        {
            LogPathTextBox.Text = Loc.Get("Crash_LoggingDisabled");
            OpenFolderButton.IsEnabled = false;
            CopyPathButton.IsEnabled = false;
        }
        else if (!result.Success)
        {
            var warnText = Loc.Format("Crash_SaveFailedWarning", result.ErrorMessage ?? "Unknown IO error");
            LogPathTextBox.Text = warnText;
            OpenFolderButton.IsEnabled = false;
            CopyPathButton.IsEnabled = false;
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(_logPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{_logPath}\"",
                    UseShellExecute = true
                });
            }
            else
            {
                var dir = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{dir}\"",
                        UseShellExecute = true
                    });
                }
            }
        }
        catch { }
    }

    private async void CopyPathButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrEmpty(_logPath))
            {
                Clipboard.SetText(_logPath);
                CopyTextBlock.Text = Loc.Get("Crash_PathCopied");
                await Task.Delay(2000);
                CopyTextBlock.Text = Loc.Get("Crash_CopyPath");
            }
        }
        catch { }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// Displays the modal crash report dialog on the active UI dispatcher,
    /// with an indestructible fallback to Win32 MessageBox if WPF window creation fails.
    /// </summary>
    public static void ShowModal(Exception ex, CrashLogResult result, bool isCritical = false)
    {
        if (DownloadPersistenceService.IsTestEnvironment)
            return;

        try
        {
            if (Application.Current != null && Application.Current.Dispatcher != null && !Application.Current.Dispatcher.HasShutdownStarted)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var dialog = new CrashReportDialog(ex, result, isCritical);
                    if (Application.Current.MainWindow != null && Application.Current.MainWindow.IsVisible)
                    {
                        dialog.Owner = Application.Current.MainWindow;
                    }
                    dialog.ShowDialog();
                });
                return;
            }
        }
        catch
        {
            // Fall back cleanly to MessageBox if WPF window rendering throws
        }

        // Resilient Fallback to standard MessageBox
        try
        {
            string title = isCritical
                ? Loc.Get("Crash_CriticalErrorTitle")
                : Loc.Get("Crash_UnexpectedErrorTitle");
            string pathInfo = result.LoggingDisabled
                ? Loc.Get("Crash_LoggingDisabled")
                : (result.Success ? result.PrimaryLogPath : "Failed to write to disk");
            string msgPattern = isCritical
                ? Loc.Get("Crash_CriticalErrorMessage")
                : Loc.Get("Crash_UnexpectedErrorMessage");
            string message = Loc.Format(msgPattern, ex.Message, pathInfo);

            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
    }
}
