using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Reepax.Models;
using Reepax.Services.Extractor;
using Reepax.Views;
using Xunit;

namespace Reepax.Tests;

public class DialogUiTests
{
    private static Dispatcher? _staDispatcher;
    private static readonly object _staLock = new();

    private static void RunInSta(Action action)
    {
        lock (_staLock)
        {
            if (_staDispatcher == null || _staDispatcher.Thread?.IsAlive != true)
            {
                using var readyEvent = new ManualResetEventSlim(false);
                var thread = new Thread(() =>
                {
                    _staDispatcher = Dispatcher.CurrentDispatcher;
                    readyEvent.Set();
                    Dispatcher.Run();
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Start();
                readyEvent.Wait();
            }

            Exception? caughtEx = null;
            _staDispatcher!.Invoke(() =>
            {
                try
                {
                    var app = Application.Current ?? new Application();
                    if (!app.Resources.Contains("IconShield"))
                    {
                        var uri = new Uri("pack://application:,,,/Reepax;component/Themes/VectorIcons.xaml", UriKind.RelativeOrAbsolute);
                        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = uri });
                    }

                    action();
                }
                catch (Exception ex)
                {
                    caughtEx = ex;
                }
            });

            if (caughtEx != null)
            {
                throw new AggregateException(caughtEx);
            }
        }
    }

    [Fact]
    public void AddLinksDialog_NewPackage_SyncsExtractionDefaultsAndDetectsNames()
    {
        RunInSta(() =>
        {
            var dialog = new AddLinksDialog("https://rapidgator.net/file/12345/Cyberpunk.2077.Update.v2.12-ELAMIGOS.rar");

            // Verify auto-detected update package name
            Assert.False(string.IsNullOrWhiteSpace(dialog.PackageNameTextBox.Text));
            Assert.Contains("Cyberpunk", dialog.PackageNameTextBox.Text);

            // User typing custom package name
            dialog.PackageNameTextBox.Text = "Cyberpunk2077";
            Assert.Equal("Cyberpunk2077", dialog.PackageNameTextBox.Text);

            // Enable AutoExtract first
            dialog.AutoExtractCheckBox.IsChecked = true;
            Assert.True(dialog.AutoExtractCheckBox.IsChecked);
            Assert.True(dialog.DeleteArchivesCheckBox.IsEnabled);
            Assert.True(dialog.RecycleArchivesCheckBox.IsEnabled);

            // Verify checkbox mutual exclusivity:
            // Check DeleteArchives
            dialog.DeleteArchivesCheckBox.IsChecked = true;
            Assert.True(dialog.DeleteArchivesCheckBox.IsChecked);
            Assert.False(dialog.RecycleArchivesCheckBox.IsChecked);

            // Check RecycleArchives -> DeleteArchives should be unchecked
            dialog.RecycleArchivesCheckBox.IsChecked = true;
            Assert.True(dialog.RecycleArchivesCheckBox.IsChecked);
            Assert.False(dialog.DeleteArchivesCheckBox.IsChecked);

            // Unchecking AutoExtract should disable child extraction checkboxes and clear them
            dialog.AutoExtractCheckBox.IsChecked = false;
            Assert.False(dialog.AutoExtractCheckBox.IsChecked);
            Assert.False(dialog.DeleteArchivesCheckBox.IsEnabled);
            Assert.False(dialog.RecycleArchivesCheckBox.IsEnabled);
            Assert.False(dialog.DeleteArchivesCheckBox.IsChecked);
            Assert.False(dialog.RecycleArchivesCheckBox.IsChecked);
        });
    }

    [Fact]
    public void AddLinksDialog_EditExistingPackage_PopulatesFieldsAndPreservesSettings()
    {
        RunInSta(() =>
        {
            var pkg = new DownloadPackage
            {
                Name = "ExistingGame_v1",
                SaveDirectory = @"C:\Downloads\ExistingGame_v1",
                AutoExtractArchives = true,
                DeleteArchiveAfterExtraction = true,
                MoveArchiveToRecycleBin = false
            };
            pkg.Items.Add(new DownloadItem
            {
                FileName = "game.zip",
                OriginalUrl = "https://rapidgator.net/file/111/game.zip"
            });

            var dialog = new AddLinksDialog(pkg);

            Assert.True(dialog.IsEditMode);
            Assert.False(dialog.IsAddingLinksToExisting);
            Assert.Equal(pkg, dialog.EditingPackage);

            // Fields populated from existing package
            Assert.Equal("ExistingGame_v1", dialog.PackageNameTextBox.Text);
            Assert.Equal(@"C:\Downloads\ExistingGame_v1", dialog.DownloadPathTextBox.Text);
            Assert.Contains("https://rapidgator.net/file/111/game.zip", dialog.LinksTextBox.Text);
            Assert.True(dialog.AutoExtractCheckBox.IsChecked);
            Assert.True(dialog.DeleteArchivesCheckBox.IsChecked);
            Assert.False(dialog.RecycleArchivesCheckBox.IsChecked);
        });
    }

    [Fact]
    public void AddLinksDialog_AddLinksToExistingPackage_LocksNameAndPath()
    {
        RunInSta(() =>
        {
            var pkg = new DownloadPackage
            {
                Name = "ExpansionPackage",
                SaveDirectory = @"C:\Downloads\ExpansionPackage"
            };

            var dialog = new AddLinksDialog("https://rapidgator.net/file/222/dlc.zip", pkg, isAddingLinksToExisting: true);

            Assert.True(dialog.IsAddingLinksToExisting);
            Assert.False(dialog.IsEditMode);

            // Controls must be locked to prevent renaming the target package or redirecting its folder
            Assert.Equal("ExpansionPackage", dialog.PackageNameTextBox.Text);
            Assert.False(dialog.PackageNameTextBox.IsEnabled);
            Assert.NotNull(dialog.PackageNameTextBox.ToolTip);

            Assert.Equal(@"C:\Downloads\ExpansionPackage", dialog.DownloadPathTextBox.Text);
            Assert.False(dialog.DownloadPathTextBox.IsEnabled);
            Assert.NotNull(dialog.DownloadPathTextBox.ToolTip);

            Assert.False(dialog.BrowseFolderButton.IsEnabled);
            Assert.NotNull(dialog.BrowseFolderButton.ToolTip);
        });
    }

    [Fact]
    public void AddLinksDialog_CompletedOrExtractedPackage_LocksExtractionSettings()
    {
        RunInSta(() =>
        {
            var pkg = new DownloadPackage
            {
                Name = "FinishedPackage",
                SaveDirectory = @"C:\Downloads\FinishedPackage",
                Status = DownloadStatus.Completed,
                AutoExtractArchives = true
            };

            var dialog = new AddLinksDialog(pkg);

            // Since extraction already finished or package is fully completed, extraction controls are locked
            Assert.False(dialog.AutoExtractCheckBox.IsEnabled);
            Assert.False(dialog.DeleteArchivesCheckBox.IsEnabled);
            Assert.False(dialog.RecycleArchivesCheckBox.IsEnabled);
            Assert.NotNull(dialog.AutoExtractCheckBox.ToolTip);
        });
    }

    [Fact]
    public void PasswordPromptDialog_PasswordTogglingAndReveal()
    {
        RunInSta(() =>
        {
            var dialog = new PasswordPromptDialog("top_secret.7z");

            // Verify archive name chip display
            Assert.Equal("top_secret.7z", dialog.ArchiveNameTextBlock.Text);
            Assert.Equal(Visibility.Visible, dialog.ArchiveNameBorder.Visibility);

            // Type masked password
            dialog.PasswordInputBox.Password = "SuperSecretPassword42";
            Assert.Equal("SuperSecretPassword42", dialog.Password);

            // Click reveal button to show plain text
            dialog.RevealButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal(Visibility.Visible, dialog.PasswordRevealBox.Visibility);
            Assert.Equal(Visibility.Collapsed, dialog.PasswordInputBox.Visibility);
            Assert.Equal("SuperSecretPassword42", dialog.PasswordRevealBox.Text);
            Assert.Equal("🙈", dialog.RevealIcon.Text);

            // Edit password while revealed
            dialog.PasswordRevealBox.Text = "ModifiedSecretKey99";
            Assert.Equal("ModifiedSecretKey99", dialog.Password);

            // Click reveal button again to hide password
            dialog.RevealButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal(Visibility.Collapsed, dialog.PasswordRevealBox.Visibility);
            Assert.Equal(Visibility.Visible, dialog.PasswordInputBox.Visibility);
            Assert.Equal("ModifiedSecretKey99", dialog.PasswordInputBox.Password);
            Assert.Equal("👁", dialog.RevealIcon.Text);

            // Remember checkbox
            dialog.RememberCheckBox.IsChecked = true;
            Assert.True(dialog.RememberCheckBox.IsChecked);
            Assert.Equal("ModifiedSecretKey99", dialog.Password);
        });
    }

    [Fact]
    public void ConfirmDialog_ShowAndShowWithOption_RespectsOverrideAndRetrievesOption()
    {
        try
        {
            // Override returns true
            ConfirmDialog.ShowDialogOverrideForTesting = () => true;
            bool resultTrue = ConfirmDialog.Show("Delete?", "Are you sure?");
            Assert.True(resultTrue);

            // Override returns false
            ConfirmDialog.ShowDialogOverrideForTesting = () => false;
            bool resultFalse = ConfirmDialog.Show("Delete?", "Are you sure?");
            Assert.False(resultFalse);

            // Test ShowWithOption passing defaultOptionChecked
            ConfirmDialog.ShowDialogOverrideForTesting = () => true;
            bool optResult = ConfirmDialog.ShowWithOption(
                "Delete Files?",
                "Delete files from disk?",
                "Also delete archive",
                out bool isOptionChecked,
                defaultOptionChecked: true);

            Assert.True(optResult);
            Assert.True(isOptionChecked);

            ConfirmDialog.ShowWithOption(
                "Delete Files?",
                "Delete files from disk?",
                "Also delete archive",
                out bool isOptionCheckedFalse,
                defaultOptionChecked: false);

            Assert.False(isOptionCheckedFalse);
        }
        finally
        {
            ConfirmDialog.ShowDialogOverrideForTesting = null;
        }
    }
}
