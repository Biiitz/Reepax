using System.Windows;
using Reepax.Services;
using Reepax.Services.Storage;

namespace Reepax.Views;

public partial class ConfirmDialog : Window
{
    /// <summary>
    /// Test hook to simulate dialog results in automated tests.
    /// </summary>
    public static Func<bool>? ShowDialogOverrideForTesting { get; set; }

    public ConfirmDialog(string title, string message, string? confirmText = null, string? cancelText = null, bool isDanger = true)
    {
        InitializeComponent();
        Title = title;
        MessageTextBlock.Text = message;
        ConfirmButton.Content = !string.IsNullOrWhiteSpace(confirmText) ? confirmText : Services.Localization.Loc.Get("Common_Yes");
        CancelButton.Content = !string.IsNullOrWhiteSpace(cancelText) ? cancelText : Services.Localization.Loc.Get("Common_No");

        if (!isDanger)
        {
            if (TryFindResource("AccentButtonStyle") is Style accentStyle)
            {
                ConfirmButton.Style = accentStyle;
            }
        }

        ThemeService.ApplyDarkTitleBar(this, ThemeService.Instance.IsDarkMode);
    }

    public bool IsOptionChecked => OptionCheckBox.IsChecked == true;

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    /// <summary>
    /// Shows a styled confirmation dialog (Yes/No). Automatically returns true in test environments unless overridden.
    /// </summary>
    public static bool Show(string title, string message, string? confirmText = null, string? cancelText = null, Window? owner = null, bool isDanger = true)
    {
        return ShowWithOption(title, message, null, out _, false, confirmText, cancelText, owner, isDanger);
    }

    /// <summary>
    /// Shows a styled confirmation dialog with an optional checkbox (e.g. also delete files from disk).
    /// Automatically returns true in test environments unless overridden.
    /// </summary>
    public static bool ShowWithOption(
        string title, 
        string message, 
        string? optionText, 
        out bool isOptionChecked, 
        bool defaultOptionChecked = false, 
        string? confirmText = null, 
        string? cancelText = null, 
        Window? owner = null,
        bool isDanger = true)
    {
        isOptionChecked = false;

        if (ShowDialogOverrideForTesting != null)
        {
            isOptionChecked = defaultOptionChecked;
            return ShowDialogOverrideForTesting.Invoke();
        }

        if (DownloadPersistenceService.IsTestEnvironment)
        {
            isOptionChecked = defaultOptionChecked;
            return true;
        }

        if (Application.Current != null && Application.Current.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
        {
            bool optVal = defaultOptionChecked;
            bool result = Application.Current.Dispatcher.Invoke(() =>
                ShowWithOption(title, message, optionText, out optVal, defaultOptionChecked, confirmText, cancelText, owner, isDanger));
            isOptionChecked = optVal;
            return result;
        }

        var targetOwner = owner ?? Application.Current?.MainWindow;
        var dialog = new ConfirmDialog(title, message, confirmText, cancelText, isDanger);

        if (targetOwner != null && targetOwner.IsVisible && targetOwner.WindowState != WindowState.Minimized)
        {
            dialog.Owner = targetOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Topmost = true;
            dialog.ShowInTaskbar = true;
        }

        if (!string.IsNullOrWhiteSpace(optionText))
        {
            dialog.OptionCheckBox.Content = optionText;
            dialog.OptionCheckBox.IsChecked = defaultOptionChecked;
            dialog.OptionCheckBox.Visibility = Visibility.Visible;
        }

        dialog.Loaded += (s, e) =>
        {
            try
            {
                dialog.Activate();
                dialog.Focus();
            }
            catch { }
        };

        var dialogResult = dialog.ShowDialog() == true;
        isOptionChecked = dialogResult && dialog.IsOptionChecked;
        return dialogResult;
    }
}
