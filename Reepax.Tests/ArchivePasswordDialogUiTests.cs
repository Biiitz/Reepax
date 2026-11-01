using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Reepax.Models;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.Views;
using Xunit;

namespace Reepax.Tests;

public partial class DialogUiTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void ArchivePasswordsDialog_MasksCandidatesAndPreservesExactInput(string language)
    {
        RunInSta(() =>
        {
            string root = Path.Combine(Path.GetTempPath(), "ArchivePasswordUiTests_" + Guid.NewGuid().ToString("N"));
            var settings = SettingsService.Instance;
            var previous = settings.GetArchivePasswordEntries();
            string originalLanguage = LocalizationService.Instance.CurrentLanguage;
            var originalShutdownMode = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Directory.CreateDirectory(root);
            try
            {
                LocalizationService.Instance.CurrentLanguage = language;
                settings.ReplaceArchivePasswords(new[] { " Key " });
                string path = Path.Combine(root, "sample.zip");
                using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                using (var writer = new StreamWriter(archive.CreateEntry("payload.txt").Open())) writer.Write("sample payload");
                var package = new DownloadPackage { Name = "Sample package", SaveDirectory = root };
                package.Items.Add(new DownloadItem { FileName = "sample.zip", SaveFilePath = path, IsEnabled = true, Status = DownloadStatus.Completed });
                var dialog = new ArchivePasswordsDialog(package);
                try
                {
                    Assert.Equal(language == "de" ? "Archivpasswörter" : "Archive passwords", dialog.Title);
                    Assert.Equal(language == "de" ? "Passwörter prüfen" : "Check passwords", dialog.CheckButton.Content);
                    Assert.Equal(language == "de" ? "Schließen" : "Close", Loc.Get("ArchivePasswords_Close"));
                    Assert.False(dialog.ContinueButton.IsEnabled);
                    Assert.True(dialog.CandidateList.IsReadOnly);
                    Assert.Equal(Visibility.Collapsed, dialog.PasswordReveal.Visibility);
                    object row = dialog.CandidateList.Items.Cast<object>().Single();
                    Assert.Equal("••••••••", row.GetType().GetProperty("Display")!.GetValue(row));
                    dialog.CandidateList.SelectedIndex = 0;
                    Assert.Equal(" Key ", dialog.PasswordInput.Password);
                    Assert.True(dialog.ContinueButton.IsEnabled);
                    dialog.ShowPasswords.IsChecked = true;
                    dialog.ShowPasswords.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Equal(Visibility.Visible, dialog.PasswordReveal.Visibility);
                    Assert.Equal(" Key ", dialog.PasswordReveal.Text);
                    row = dialog.CandidateList.Items.Cast<object>().Single();
                    Assert.Equal(" Key ", row.GetType().GetProperty("Display")!.GetValue(row));
                    dialog.PasswordReveal.Text = string.Empty;
                    Assert.False(dialog.ContinueButton.IsEnabled);
                    Assert.NotEqual(true, dialog.RememberPassword.IsChecked);
                    Assert.NotEqual(true, dialog.ApplyToAll.IsChecked);
                }
                finally { dialog.Close(); }
            }
            finally
            {
                LocalizationService.Instance.CurrentLanguage = originalLanguage;
                settings.ReplaceArchivePasswords(previous.Select(entry => entry.Password));
                settings.Settings.ArchivePasswordStatistics = previous.ToList();
                Application.Current.ShutdownMode = originalShutdownMode;
                string fullRoot = Path.GetFullPath(root);
                if (Path.GetDirectoryName(fullRoot) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                    !Path.GetFileName(fullRoot).StartsWith("ArchivePasswordUiTests_", StringComparison.Ordinal))
                    throw new InvalidOperationException("Invalid test directory.");
                Directory.Delete(fullRoot, recursive: true);
            }
        });
    }
}
