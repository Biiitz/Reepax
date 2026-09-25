using System.Windows;
using Reepax.Services;
using Reepax.Services.Storage;

namespace Reepax.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string? confirmText = null, string? cancelText = null)
    {
        InitializeComponent();
        Title = title;
        MessageTextBlock.Text = message;
        ConfirmButton.Content = !string.IsNullOrWhiteSpace(confirmText) ? confirmText : Services.Localization.Loc.Get("Common_Yes");
        CancelButton.Content = !string.IsNullOrWhiteSpace(cancelText) ? cancelText : Services.Localization.Loc.Get("Common_No");

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
    /// Shows a styled confirmation dialog (Yes/No). Automatically returns true in test environments.
    /// </summary>
    public static bool Show(string title, string message, string? confirmText = null, string? cancelText = null, Window? owner = null)
    {
        return ShowWithOption(title, message, null, out _, false, confirmText, cancelText, owner);
    }

    /// <summary>
    /// Shows a styled confirmation dialog with an optional checkbox (e.g. also delete files from disk).
    /// Automatically returns true in test environments.
    /// </summary>
    public static bool ShowWithOption(
        string title, 
        string message, 
        string? optionText, 
        out bool isOptionChecked, 
        bool defaultOptionChecked = false, 
        string? confirmText = null, 
        string? cancelText = null, 
        Window? owner = null)
    {
        isOptionChecked = false;

        if (DownloadPersistenceService.IsTestEnvironment)
        {
            isOptionChecked = defaultOptionChecked;
            return true;
        }

        if (Application.Current != null && Application.Current.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
        {
            bool optVal = defaultOptionChecked;
            bool result = Application.Current.Dispatcher.Invoke(() =>
                ShowWithOption(title, message, optionText, out optVal, defaultOptionChecked, confirmText, cancelText, owner));
            isOptionChecked = optVal;
            return result;
        }

        var targetOwner = owner ?? Application.Current?.MainWindow;
        var dialog = new ConfirmDialog(title, message, confirmText, cancelText);

        if (targetOwner != null && targetOwner.IsVisible && targetOwner.WindowState != WindowState.Minimized)
        {
            dialog.Owner = targetOwner;
        }

        if (!string.IsNullOrWhiteSpace(optionText))
        {
            dialog.OptionCheckBox.Content = optionText;
            dialog.OptionCheckBox.IsChecked = defaultOptionChecked;
            dialog.OptionCheckBox.Visibility = Visibility.Visible;
        }

        var dialogResult = dialog.ShowDialog() == true;
        isOptionChecked = dialogResult && dialog.IsOptionChecked;
        return dialogResult;
    }
}
