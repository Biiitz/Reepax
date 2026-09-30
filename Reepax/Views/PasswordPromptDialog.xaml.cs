using System;
using System.Windows;
using Reepax.Services;
using Reepax.Services.Storage;

namespace Reepax.Views;

public partial class PasswordPromptDialog : Window
{
    private bool _isPasswordRevealed;

    public string Password { get; private set; } = string.Empty;
    public bool RememberPassword { get; private set; }

    public PasswordPromptDialog(string? archiveName = null)
    {
        InitializeComponent();
        ThemeService.ApplyDarkTitleBar(this, ThemeService.Instance.IsDarkMode);

        if (!string.IsNullOrWhiteSpace(archiveName))
        {
            ArchiveNameTextBlock.Text = archiveName;
            ArchiveNameBorder.Visibility = Visibility.Visible;
        }
        else
        {
            ArchiveNameBorder.Visibility = Visibility.Collapsed;
        }

        Loaded += (_, _) =>
        {
            PasswordInputBox.Focus();
        };
    }

    private void PasswordInputBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_isPasswordRevealed)
        {
            Password = PasswordInputBox.Password;
        }
    }

    private void PasswordRevealBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_isPasswordRevealed)
        {
            Password = PasswordRevealBox.Text;
        }
    }

    private void RevealButton_Click(object sender, RoutedEventArgs e)
    {
        _isPasswordRevealed = !_isPasswordRevealed;
        if (_isPasswordRevealed)
        {
            PasswordRevealBox.Text = PasswordInputBox.Password;
            PasswordInputBox.Visibility = Visibility.Collapsed;
            PasswordRevealBox.Visibility = Visibility.Visible;
            PasswordRevealBox.Focus();
            PasswordRevealBox.CaretIndex = PasswordRevealBox.Text.Length;
            RevealIcon.Text = "🙈";
        }
        else
        {
            PasswordInputBox.Password = PasswordRevealBox.Text;
            PasswordRevealBox.Visibility = Visibility.Collapsed;
            PasswordInputBox.Visibility = Visibility.Visible;
            PasswordInputBox.Focus();
            RevealIcon.Text = "👁";
        }
    }

    private void ExtractButton_Click(object sender, RoutedEventArgs e)
    {
        Password = _isPasswordRevealed ? PasswordRevealBox.Text : PasswordInputBox.Password;
        RememberPassword = RememberCheckBox.IsChecked == true;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    /// <summary>
    /// Displays the password prompt dialog modally on the UI thread.
    /// Returns the entered password and whether it should be remembered.
    /// </summary>
    public static (string? Password, bool Remember) ShowDialog(string archiveName, Window? owner = null)
    {
        if (DownloadPersistenceService.IsTestEnvironment)
        {
            return (null, false);
        }

        if (Application.Current != null && Application.Current.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
        {
            return Application.Current.Dispatcher.Invoke(() => ShowDialog(archiveName, owner));
        }

        var targetOwner = owner ?? Application.Current?.MainWindow;
        var dialog = new PasswordPromptDialog(archiveName);

        if (targetOwner != null && targetOwner.IsVisible && targetOwner.WindowState != WindowState.Minimized)
        {
            dialog.Owner = targetOwner;
        }

        bool result = dialog.ShowDialog() == true;
        if (result && !string.IsNullOrEmpty(dialog.Password))
        {
            return (dialog.Password, dialog.RememberPassword);
        }

        return (null, false);
    }
}
