using System;
using System.Windows;
using System.Threading;
using Reepax.Services;
using Reepax.Services.Storage;

namespace Reepax.Views;

public partial class PasswordPromptDialog : Window
{
    private bool _isPasswordRevealed;

    public string Password { get; private set; } = string.Empty;
    public bool RememberPassword { get; private set; }

    public PasswordPromptDialog(string? archiveName = null, bool retry = false)
    {
        InitializeComponent();
        RetryMessage.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
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
            UpdateExtractButton();
        }
    }

    private void PasswordRevealBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_isPasswordRevealed)
        {
            Password = PasswordRevealBox.Text;
            UpdateExtractButton();
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
        if (string.IsNullOrEmpty(Password)) return;
        RememberPassword = RememberCheckBox.IsChecked == true;
        DialogResult = true;
    }

    private void UpdateExtractButton()
    {
        if (ExtractButton != null) ExtractButton.IsEnabled = !string.IsNullOrEmpty(Password);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    /// <summary>
    /// Displays the password prompt dialog modally on the UI thread.
    /// Returns the entered password and whether it should be remembered.
    /// </summary>
    public static (string? Password, bool Remember) ShowDialog(string archiveName, Window? owner = null,
        bool retry = false, CancellationToken cancellationToken = default)
    {
        if (DownloadPersistenceService.IsTestEnvironment || cancellationToken.IsCancellationRequested)
        {
            return (null, false);
        }

        if (Application.Current != null && Application.Current.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
        {
            return Application.Current.Dispatcher.Invoke(() => ShowDialog(archiveName, owner, retry, cancellationToken));
        }

        var targetOwner = owner ?? Application.Current?.MainWindow;
        var dialog = new PasswordPromptDialog(archiveName, retry);

        if (targetOwner != null && targetOwner.IsVisible && targetOwner.WindowState != WindowState.Minimized)
        {
            dialog.Owner = targetOwner;
        }

        using var registration = cancellationToken.Register(() => dialog.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (dialog.IsVisible) dialog.Close();
        })));
        if (cancellationToken.IsCancellationRequested) return (null, false);
        bool result = dialog.ShowDialog() == true;
        if (result && !string.IsNullOrEmpty(dialog.Password))
        {
            return (dialog.Password, dialog.RememberPassword);
        }

        return (null, false);
    }
}
