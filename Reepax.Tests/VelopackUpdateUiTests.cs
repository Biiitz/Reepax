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
    [InlineData("en", ThemeCatalog.DefaultId, "Preparing update: 50%")]
    [InlineData("de", ThemeCatalog.MacOSId, "Update wird vorbereitet: 50%")]
    public void VelopackUpdateDialog_OffersInstallForVerifiedPackageMetadata(string language, string theme, string progress)
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
                    SetupAssetUrl = "https://example.test/package.nupkg", PackageUpdate = new Velopack.UpdateInfo(release, false)
                });
                Assert.Equal(Visibility.Visible, ((Button)dialog.FindName("InstallUpdateButton")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((Button)dialog.FindName("GetUpdateButton")).Visibility);
                Assert.Equal(progress, Loc.Format("UpdateDialog_Status_PackageProgress", 50));
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
