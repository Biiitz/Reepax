using System.Windows;
using System.Windows.Controls;
using Reepax.Services;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.Services.Update;
using Reepax.Views;

namespace Reepax.Tests;

public partial class DialogUiTests
{
    [Theory]
    [InlineData("en", ThemeCatalog.DefaultId, "Preparing update: 50%", true, false)]
    [InlineData("de", ThemeCatalog.MacOSId, "Update wird vorbereitet: 50%", true, false)]
    [InlineData("en", ThemeCatalog.DefaultId, "Preparing update: 50%", false, false)]
    [InlineData("en", ThemeCatalog.DefaultId, "Preparing update: 50%", false, true)]
    [InlineData("de", ThemeCatalog.MacOSId, "Update wird vorbereitet: 50%", false, true)]
    public void UpdateDialog_OffersInstallOrMigrationInstructions(string language, string theme, string progress,
        bool packageUpdate, bool migrationRequired)
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            string previousLanguage = LocalizationService.Instance.CurrentLanguage;
            string previousTheme = ThemeService.Instance.CurrentTheme.Id;
            var previousShutdown = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var settings = SettingsService.Instance.Settings;
            var previousBounds = (settings.UpdateDialogWidth, settings.UpdateDialogHeight,
                settings.UpdateDialogLeft, settings.UpdateDialogTop, settings.IsUpdateDialogMaximized);
            UpdateDialog? dialog = null;
            try
            {
                LocalizationService.Instance.CurrentLanguage = language;
                ThemeService.Instance.SelectTheme(theme, save: false);
                var release = new Velopack.VelopackAsset { Version = Velopack.SemanticVersion.Parse("26.11.2") };
                dialog = new UpdateDialog(new UpdateInfo
                {
                    NewVersion = "26.11.2", Changelog = "Example release notes", SetupAssetSize = 1,
                    SetupAssetUrl = packageUpdate ? "https://example.test/package.nupkg" : "https://example.test/setup.exe",
                    Sha256ChecksumAssetUrl = packageUpdate ? null : "https://example.test/SHA256SUMS.txt",
                    PackageUpdate = packageUpdate ? new Velopack.UpdateInfo(release, false) : null,
                    RequiresPerUserMigration = migrationRequired
                });
                Assert.Equal(migrationRequired ? Visibility.Collapsed : Visibility.Visible,
                    ((Button)dialog.FindName("InstallUpdateButton")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((Button)dialog.FindName("GetUpdateButton")).Visibility);
                Assert.Equal(progress, Loc.Format("UpdateDialog_Status_PackageProgress", 50));
                if (migrationRequired)
                {
                    var instructions = (TextBlock)dialog.FindName("UpdateStatusTextBlock");
                    Assert.Equal(TextWrapping.Wrap, instructions.TextWrapping);
                    Assert.Equal(Loc.Get("UpdateDialog_Status_MigrationRequired"), instructions.Text);
                    Assert.Contains(language == "de" ? "Adminrechte" : "administrator rights", instructions.Text);
                    Assert.Equal(Visibility.Visible, ((Button)dialog.FindName("ViewOnGitHubButton")).Visibility);
                    Assert.Equal(Visibility.Visible, ((Grid)dialog.FindName("UpdateProgressPanel")).Visibility);
                    Assert.Equal(Visibility.Collapsed, ((ProgressBar)dialog.FindName("UpdateProgressBar")).Visibility);
                }
            }
            finally
            {
                dialog?.Close();
                (settings.UpdateDialogWidth, settings.UpdateDialogHeight, settings.UpdateDialogLeft,
                    settings.UpdateDialogTop, settings.IsUpdateDialogMaximized) = previousBounds;
                LocalizationService.Instance.CurrentLanguage = previousLanguage;
                ThemeService.Instance.SelectTheme(previousTheme, save: false);
                Application.Current.ShutdownMode = previousShutdown;
            }
        });
    }
}
