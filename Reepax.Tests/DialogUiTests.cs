using System;
using System.Threading;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using Reepax.Services.Storage;
using Reepax.ViewModels;
using System.Windows.Threading;
using Reepax.Models;
using Reepax.Services.Extractor;
using Reepax.Views;
using Xunit;

namespace Reepax.Tests;

public partial class DialogUiTests
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
            var dialog = new AddLinksDialog("https://rapidgator.net/file/12345/Example.App.Update.v2.12-Release.rar");

            // Verify auto-detected update package name
            Assert.False(string.IsNullOrWhiteSpace(dialog.PackageNameTextBox.Text));
            Assert.Contains("Example App", dialog.PackageNameTextBox.Text);

            // User typing custom package name
            dialog.PackageNameTextBox.Text = "ExampleApp";
            Assert.Equal("ExampleApp", dialog.PackageNameTextBox.Text);

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
    public void PasswordInput_EnterCannotResubmitAnExistingPassword()
    {
        RunInSta(() =>
        {
            var settings = SettingsService.Instance.Settings;
            var previousPasswords = settings.ExtractionPasswords;
            var previousStatistics = settings.ArchivePasswordStatistics;
            settings.ExtractionPasswords = new();
            settings.ArchivePasswordStatistics = new();
            var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Reepax"));
            var application = XDocument.Load(Path.Combine(project, "App.xaml"));
            var resources = new XElement(application.Descendants().First(element => element.Name.LocalName == "ResourceDictionary"));
            foreach (var element in resources.DescendantsAndSelf())
            {
                if (element.Name.NamespaceName.StartsWith("clr-namespace:") && !element.Name.NamespaceName.Contains(";assembly="))
                    element.Name = XName.Get(element.Name.LocalName, element.Name.NamespaceName + ";assembly=Reepax");
                if (element.Attribute("Source") is { } source)
                    source.Value = "pack://application:,,,/Reepax;component/" + source.Value;
            }
            foreach (var attribute in application.Root!.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
                resources.SetAttributeValue(attribute.Name, attribute.Value.StartsWith("clr-namespace:")
                    ? attribute.Value + ";assembly=Reepax" : attribute.Value);
            var dictionary = (ResourceDictionary)XamlReader.Parse(resources.ToString());
            Application.Current.Resources.MergedDictionaries.Add(dictionary);
            try
            {
                var vm = new MainViewModel();
                var view = new SettingsView { DataContext = vm };
                using var source = new HwndSource(new HwndSourceParameters("PasswordInputProbe")
                {
                    Width = 600, Height = 400, PositionX = -32000, PositionY = -32000,
                    WindowStyle = unchecked((int)0x80000000)
                });
                void PressEnter() => view.NewArchivePasswordTextBox.RaiseEvent(new KeyEventArgs(
                    Keyboard.PrimaryDevice, source, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
                vm.NewArchivePasswordInput = "KnownKey";
                PressEnter();
                Assert.Single(vm.ArchivePasswordEntries);
                vm.NewArchivePasswordInput = "KnownKey";
                PressEnter();
                Assert.Single(vm.ArchivePasswordEntries);
                Assert.Equal("KnownKey", vm.NewArchivePasswordInput);
                Assert.False(vm.AddArchivePasswordCommand.CanExecute(null));
                view.DataContext = null;
            }
            finally
            {
                Application.Current.Resources.MergedDictionaries.Remove(dictionary);
                settings.ExtractionPasswords = previousPasswords;
                settings.ArchivePasswordStatistics = previousStatistics;
            }
        });
    }

    [Theory]
    [InlineData("Project.Alpha.part01.rar")]
    [InlineData("Project.Alpha.7z.001")]
    public void AddLinksDialog_AutomaticNameTracksLinksWithoutBecomingCustom(string fileName)
    {
        RunInSta(() =>
        {
            var dialog = new AddLinksDialog("https://example.invalid/" + fileName);
            Assert.Equal("Project Alpha", dialog.PackageNameTextBox.Text);
            Assert.Empty(dialog.CustomPackageName);
            dialog.LinksTextBox.Text = "https://example.invalid/Project.Beta.Update.v2.0.zip";
            Assert.Contains("Project Beta", dialog.PackageNameTextBox.Text);
            dialog.LinksTextBox.Text = "https://example.invalid/Project.Gamma.zip";
            Assert.Equal("Project Gamma", dialog.PackageNameTextBox.Text);
            dialog.LinksTextBox.Clear();
            Assert.Empty(dialog.PackageNameTextBox.Text);
        });
    }

    [Fact]
    public void AddLinksDialog_ManualNameSurvivesLinkChangesAndCanReturnToAutomaticNaming()
    {
        RunInSta(() =>
        {
            var dialog = new AddLinksDialog("https://example.invalid/Project.Alpha.zip");
            dialog.PackageNameTextBox.Text = "My Collection";
            dialog.LinksTextBox.Text = "https://example.invalid/Project.Beta.zip";
            Assert.Equal("My Collection", dialog.PackageNameTextBox.Text);
            Assert.Equal("My Collection", dialog.CustomPackageName);
            dialog.PackageNameTextBox.Clear();
            dialog.LinksTextBox.Text = "https://example.invalid/Project.Gamma.zip";
            Assert.Equal("Project Gamma", dialog.PackageNameTextBox.Text);
            Assert.Empty(dialog.CustomPackageName);
        });
    }

    [Fact]
    public void AddLinksDialog_EditExistingPackage_PopulatesFieldsAndPreservesSettings()
    {
        RunInSta(() =>
        {
            var pkg = new DownloadPackage
            {
                Name = "ExistingPackage_v1",
                SaveDirectory = @"C:\Downloads\ExistingPackage_v1",
                AutoExtractArchives = true,
                DeleteArchiveAfterExtraction = true,
                MoveArchiveToRecycleBin = false
            };
            pkg.Items.Add(new DownloadItem
            {
                FileName = "example.zip",
                OriginalUrl = "https://rapidgator.net/file/111/example.zip"
            });

            var dialog = new AddLinksDialog(pkg);

            Assert.True(dialog.IsEditMode);
            Assert.False(dialog.IsAddingLinksToExisting);
            Assert.Equal(pkg, dialog.EditingPackage);

            // Fields populated from existing package
            Assert.Equal("ExistingPackage_v1", dialog.PackageNameTextBox.Text);
            Assert.Equal(@"C:\Downloads\ExistingPackage_v1", dialog.DownloadPathTextBox.Text);
            Assert.Contains("https://rapidgator.net/file/111/example.zip", dialog.LinksTextBox.Text);
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
            var dialog = new PasswordPromptDialog("top_secret.7z", retry: true);
            Assert.Equal(Visibility.Visible, dialog.RetryMessage.Visibility);
            Assert.False(dialog.ExtractButton.IsEnabled);

            // Verify archive name chip display
            Assert.Equal("top_secret.7z", dialog.ArchiveNameTextBlock.Text);
            Assert.Equal(Visibility.Visible, dialog.ArchiveNameBorder.Visibility);

            // Type masked password
            dialog.PasswordInputBox.Password = "SuperSecretPassword42";
            Assert.Equal("SuperSecretPassword42", dialog.Password);
            Assert.True(dialog.ExtractButton.IsEnabled);

            // Click reveal button to show plain text
            dialog.RevealButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal(Visibility.Visible, dialog.PasswordRevealBox.Visibility);
            Assert.Equal(Visibility.Collapsed, dialog.PasswordInputBox.Visibility);
            Assert.Equal("SuperSecretPassword42", dialog.PasswordRevealBox.Text);
            Assert.Equal("🙈", dialog.RevealIcon.Text);

            // Edit password while revealed
            dialog.PasswordRevealBox.Text = "ModifiedSecretKey99";
            Assert.Equal("ModifiedSecretKey99", dialog.Password);
            dialog.PasswordRevealBox.Text = string.Empty;
            Assert.False(dialog.ExtractButton.IsEnabled);
            dialog.PasswordRevealBox.Text = "ModifiedSecretKey99";
            Assert.True(dialog.ExtractButton.IsEnabled);

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

    [Fact]
    public void ConfirmDialog_FileRemovalDefaultsToRecyclingAndRequiresFileOptIn()
    {
        RunInSta(() =>
        {
            var theme = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Reepax;component/Themes/DarkTheme.xaml")
            };
            Application.Current.Resources.MergedDictionaries.Add(theme);
            var shutdownMode = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var dialog = new ConfirmDialog("Remove", "Remove the selected package?");
                Assert.Equal(Visibility.Collapsed, dialog.FileDeletionOptionsPanel.Visibility);
                dialog.ConfigureFileDeletionOptions("Also remove associated files");
                Assert.Equal(Visibility.Visible, dialog.OptionCheckBox.Visibility);
                Assert.Equal(Visibility.Visible, dialog.FileDeletionOptionsPanel.Visibility);
                Assert.False(dialog.IsOptionChecked);
                Assert.True(dialog.RecycleFilesRadioButton.IsChecked);
                Assert.False(dialog.PermanentlyDeleteFilesRadioButton.IsChecked == true);
                dialog.FileDeletionOptionsPanel.GetBindingExpression(UIElement.IsEnabledProperty)?.UpdateTarget();
                Assert.False(dialog.FileDeletionOptionsPanel.IsEnabled);
                dialog.OptionCheckBox.IsChecked = true;
                dialog.FileDeletionOptionsPanel.GetBindingExpression(UIElement.IsEnabledProperty)?.UpdateTarget();
                Assert.True(dialog.FileDeletionOptionsPanel.IsEnabled);
                dialog.PermanentlyDeleteFilesRadioButton.IsChecked = true;
                Assert.False(dialog.RecycleFilesRadioButton.IsChecked == true);
                Assert.True(dialog.PermanentlyDeleteFilesRadioButton.IsChecked);
                dialog.Close();
            }
            finally
            {
                Application.Current.Resources.MergedDictionaries.Remove(theme);
                Application.Current.ShutdownMode = shutdownMode;
            }
        });
    }
}
