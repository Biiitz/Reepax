using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Reepax.Models;
using Reepax.Services;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.Views;
using Xunit;

namespace Reepax.Tests;

public partial class DialogUiTests
{
    [Theory]
    [InlineData(ThemeCatalog.DefaultId, "en")]
    [InlineData(ThemeCatalog.MacOSId, "de")]
    public void ArchivePasswordActions_UseReadableSharedStylesInEveryButtonState(string theme, string language)
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var service = ThemeService.Instance;
            string previousTheme = service.CurrentTheme.Id;
            string previousLanguage = LocalizationService.Instance.CurrentLanguage;
            string? targetAccent = null;
            var shutdown = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ArchivePasswordsDialog? dialog = null;
            try
            {
                service.SelectTheme(theme, save: false);
                targetAccent = service.CurrentAccentColorHex;
                LocalizationService.Instance.CurrentLanguage = language;
                dialog = new ArchivePasswordsDialog(new DownloadPackage())
                { Left = -10000, Top = -10000, ShowActivated = false };
                dialog.Show();
                PumpSettingsAnimations(20);
                Assert.Same(dialog.FindResource("AccentButtonStyle"), dialog.ContinueButton.Style);
                Assert.DoesNotContain("…", Loc.Get("Menu_ArchivePasswords"));
                Assert.DoesNotContain("...", Loc.Get("Settings_ArchivePasswords_Placeholder"));
                var buttons = ((DockPanel)dialog.ContinueButton.Parent).Children.OfType<Button>().ToArray();
                foreach (string accent in new[] { "#FFFFFF", "#000000", "#3B82F6" })
                {
                    service.SetAccentColor(accent, save: false);
                    foreach (var button in buttons)
                    {
                        button.IsEnabled = true;
                        button.ApplyTemplate();
                        var frame = Assert.IsType<Border>(button.Template.FindName("border", button));
                        foreach (string state in new[] { "Normal", "IsMouseOver", "IsKeyboardFocused", "IsPressed", "Disabled" })
                        {
                            DependencyPropertyKey? property = state is "Normal" or "Disabled" ? null :
                                (DependencyPropertyKey)(state == "IsPressed" ? typeof(ButtonBase) : typeof(UIElement))
                                    .GetField(state + "PropertyKey", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
                            if (property != null) button.SetValue(property, true);
                            button.IsEnabled = state != "Disabled";
                            button.UpdateLayout();
                            var foreground = Assert.IsType<SolidColorBrush>(button.Foreground).Color;
                            var background = Assert.IsType<SolidColorBrush>(frame.Background).Color;
                            Assert.True(Contrast(foreground, background) >= 4.5, button.Content + " / " + state + " / " + accent);
                            if (property != null) button.SetValue(property, false);
                            button.IsEnabled = true;
                        }
                    }
                }
            }
            finally
            {
                dialog?.Close();
                LocalizationService.Instance.CurrentLanguage = previousLanguage;
                if (targetAccent != null)
                {
                    service.SelectTheme(theme, save: false);
                    service.SetAccentColor(targetAccent, save: false);
                }
                service.SelectTheme(previousTheme, save: false);
                Application.Current.ShutdownMode = shutdown;
            }
        });
    }

    [Theory]
    [InlineData(ThemeCatalog.DefaultId)]
    [InlineData(ThemeCatalog.MacOSId)]
    public void NativeDialogTitleBars_MatchTheInitialThemeAndSubsequentThemeChanges(string theme)
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var service = ThemeService.Instance;
            string previous = service.CurrentTheme.Id;
            var shutdown = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Window[] dialogs = [];
            try
            {
                service.SelectTheme(theme, save: false);
                dialogs = [new ArchivePasswordsDialog(new DownloadPackage()),
                    new CrashReportDialog(new InvalidOperationException("Example error"), new CrashLogResult())];
                foreach (var dialog in dialogs)
                {
                    dialog.Left = dialog.Top = -10000;
                    dialog.ShowActivated = false;
                    dialog.Show();
                }
                PumpSettingsAnimations(20);
                foreach (string target in new[] { theme, ThemeCatalog.MacOSId, ThemeCatalog.DefaultId })
                {
                    if (service.CurrentTheme.Id != target) service.SelectTheme(target, save: false);
                    foreach (var dialog in dialogs)
                    {
                        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) continue;
                        var handle = new WindowInteropHelper(dialog).Handle;
                        Assert.Equal(0, DwmGetSquareWindowAttribute(handle, 20, out int dark, sizeof(int)));
                        Assert.Equal(service.IsDarkMode ? 1 : 0, dark);
                        // Caption colors are setter-only DWM attributes; query the native theme flag.
                    }
                }
            }
            finally
            {
                foreach (var dialog in dialogs) dialog.Close();
                service.SelectTheme(previous, save: false);
                Application.Current.ShutdownMode = shutdown;
            }
        });
    }

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
