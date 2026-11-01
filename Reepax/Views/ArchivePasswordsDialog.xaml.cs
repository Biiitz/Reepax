using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Reepax.Models;
using Reepax.Services.Extractor;
using Reepax.Services.Localization;
using Reepax.Services.Storage;

namespace Reepax.Views;

public partial class ArchivePasswordsDialog : Window
{
    private readonly DownloadPackage _package;
    private readonly ArchiveExtractionService _extractor = ArchiveExtractionService.Instance;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy;
    private bool _closed;
    private bool _ready;

    public ArchivePasswordsDialog(DownloadPackage package)
    {
        _package = package;
        InitializeComponent();
        ArchiveSelector.DisplayMemberPath = nameof(PackageArchivePasswordTarget.DisplayName);
        ArchiveSelector.ItemsSource = _extractor.GetPackageArchivePasswordTargets(package);
        ArchiveSelector.SelectedIndex = 0;
        package.PropertyChanged += Package_PropertyChanged;
        SettingsService.Instance.ArchivePasswordsChanged += Passwords_Changed;
        Loaded += (_, _) => PasswordInput.Focus();
        RefreshRows();
    }

    private string? ArchiveName => (ArchiveSelector.SelectedItem as PackageArchivePasswordTarget)?.Key;
    private string Input => ShowPasswords.IsChecked == true ? PasswordReveal.Text : PasswordInput.Password;

    private void RefreshRows()
    {
        if (_closed || ArchiveName is not { } name) { UpdateButtons(); return; }
        ArchiveStatus.Text = _extractor.GetPackageArchivePasswordStatus(_package, name);
        bool reveal = ShowPasswords.IsChecked == true;
        CandidateList.ItemsSource = _extractor.GetPackagePasswordCandidates(_package, name)
            .Select(candidate => new PasswordRow(candidate, reveal)).ToArray();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        if (CheckButton == null) return;
        bool available = !_busy && !_package.IsExtracting && ArchiveName != null;
        CheckButton.IsEnabled = available;
        ContinueButton.IsEnabled = available && (_ready || !string.IsNullOrEmpty(Input));
        ArchiveSelector.IsEnabled = available;
        PasswordInput.IsEnabled = available;
        PasswordReveal.IsEnabled = available;
        CandidateList.IsEnabled = available;
        ApplyToAll.IsEnabled = available;
        RememberPassword.IsEnabled = available;
    }

    private bool ApplyInput(bool retry = false)
    {
        if (ArchiveName is not { } name || string.IsNullOrEmpty(Input)) return true;
        try
        {
            _extractor.AddPackageArchivePassword(_package, ApplyToAll.IsChecked == true ? "*" : name,
                Input, RememberPassword.IsChecked == true, retry);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            CheckStatus.Text = Loc.Get("ArchivePasswords_CheckFailed");
            return false;
        }
    }

    private async void CheckButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyInput(retry: true)) return;
        _busy = true;
        _ready = false;
        CheckStatus.Text = Loc.Get("ArchivePasswords_Checking");
        UpdateButtons();
        try
        {
            var result = await _extractor.CheckPackagePasswordsAsync(_package, _lifetime.Token);
            if (_closed) return;
            _ready = result.Ready;
            CheckStatus.Text = result.FailureStatus ?? Loc.Get(result.Ready ? "ArchivePasswords_Ready" : "ArchivePasswords_Missing");
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!_closed) CheckStatus.Text = Loc.Get(_package.IsExtracting ? "ArchivePasswords_Busy" : "ArchivePasswords_CheckFailed");
        }
        finally
        {
            _busy = false;
            if (!_closed) RefreshRows();
        }
    }

    private void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ContinueButton.IsEnabled) return;
        if (!ApplyInput()) return;
        DialogResult = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowPasswords_Click(object sender, RoutedEventArgs e)
    {
        bool show = ShowPasswords.IsChecked == true;
        if (show) PasswordReveal.Text = PasswordInput.Password;
        else PasswordInput.Password = PasswordReveal.Text;
        PasswordReveal.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PasswordInput.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        RefreshRows();
    }

    private void PasswordInput_Changed(object sender, RoutedEventArgs e) => UpdateButtons();
    private void PasswordReveal_Changed(object sender, TextChangedEventArgs e) => UpdateButtons();
    private void ArchiveSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshRows();

    private void CandidateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CandidateList.SelectedItem is not PasswordRow row) return;
        PasswordInput.Password = row.Password;
        PasswordReveal.Text = row.Password;
        UpdateButtons();
    }

    private void Passwords_Changed(object? sender, EventArgs e)
    {
        if (!_closed) Dispatcher.BeginInvoke(() => { if (!_busy && !_closed) RefreshRows(); });
    }

    private void Package_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadPackage.IsExtracted) && _package.IsExtracted)
            Dispatcher.BeginInvoke(() => { if (!_closed && IsVisible) Close(); });
        if (e.PropertyName is nameof(DownloadPackage.ArchivePasswordStateRevision) or nameof(DownloadPackage.IsExtracting))
            Passwords_Changed(sender, EventArgs.Empty);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _package.PropertyChanged -= Package_PropertyChanged;
        SettingsService.Instance.ArchivePasswordsChanged -= Passwords_Changed;
        base.OnClosed(e);
    }

    private sealed class PasswordRow
    {
        internal string Password { get; }
        public string Display { get; }
        public string Source { get; }
        public string Result { get; }

        internal PasswordRow(ArchivePasswordCandidate candidate, bool reveal)
        {
            Password = candidate.Password;
            Display = reveal ? candidate.Password : "••••••••";
            Source = Loc.Get(candidate.IsSaved ? "ArchivePasswords_Saved" : "ArchivePasswords_Package");
            Result = Loc.Get(candidate.Outcome switch
            {
                ArchivePasswordOutcome.Accepted => "ArchivePasswords_Accepted",
                ArchivePasswordOutcome.Verified => "ArchivePasswords_Verified",
                ArchivePasswordOutcome.NotConfirmed => "ArchivePasswords_NotConfirmed",
                _ => "ArchivePasswords_NotTried"
            });
        }
    }
}
