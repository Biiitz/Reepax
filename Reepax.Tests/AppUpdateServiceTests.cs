using System;
using System.IO;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.Services.Update;
using Xunit;

namespace Reepax.Tests;

public class AppUpdateServiceTests
{
    [Fact]
    public void UpdateInfo_DirectAssetSelection_PrefersSetupForInstalledMode()
    {
        var info = new UpdateInfo
        {
            SetupAssetUrl = "https://github.com/Biiitz/Reepax/releases/download/v26.10.2/setup.exe",
            SetupAssetSize = 1000,
            PortableZipAssetUrl = "https://github.com/Biiitz/Reepax/releases/download/v26.10.2/portable.zip",
            PortableZipAssetSize = 2000
        };

        bool originalPortable = SettingsService.IsPortableMode;
        try
        {
            SettingsService.IsPortableMode = false;

            Assert.True(info.HasDirectAsset);
            Assert.Equal("https://github.com/Biiitz/Reepax/releases/download/v26.10.2/setup.exe", info.TargetDownloadUrl);
            Assert.Equal(1000, info.TargetDownloadSize);
            Assert.Equal("setup.exe", info.TargetFileName);
        }
        finally
        {
            SettingsService.IsPortableMode = originalPortable;
        }
    }

    [Fact]
    public void UpdateInfo_DirectAssetSelection_PrefersZipForPortableMode()
    {
        var info = new UpdateInfo
        {
            SetupAssetUrl = "https://github.com/Biiitz/Reepax/releases/download/v26.10.2/setup.exe",
            SetupAssetSize = 1000,
            PortableZipAssetUrl = "https://github.com/Biiitz/Reepax/releases/download/v26.10.2/portable.zip",
            PortableZipAssetSize = 2000
        };

        bool originalPortable = SettingsService.IsPortableMode;
        try
        {
            SettingsService.IsPortableMode = true;

            Assert.True(info.HasDirectAsset);
            Assert.Equal("https://github.com/Biiitz/Reepax/releases/download/v26.10.2/portable.zip", info.TargetDownloadUrl);
            Assert.Equal(2000, info.TargetDownloadSize);
            Assert.Equal("portable.zip", info.TargetFileName);
        }
        finally
        {
            SettingsService.IsPortableMode = originalPortable;
        }
    }

    [Fact]
    public void UpdateInfo_HasDirectAsset_IsFalseWhenNoAssetsProvided()
    {
        var info = new UpdateInfo();
        Assert.False(info.HasDirectAsset);
    }

    [Fact]
    public void GenerateInstallerUpdateScript_ContainsRequiredSilentParametersAndRestart()
    {
        string script = AppUpdateService.GenerateInstallerUpdateScript(
            1234,
            @"C:\Users\test\AppData\Local\Temp\setup.exe",
            @"C:\Users\test\AppData\Local\Programs\Reepax\Reepax.exe");

        Assert.Contains("/VERYSILENT", script);
        Assert.Contains("/SUPPRESSMSGBOXES", script);
        Assert.Contains("/FORCECLOSEAPPLICATIONS", script);
        Assert.Contains("--restart", script);
        Assert.Contains("$procId = 1234", script);
        Assert.Contains("Wait-Process -Id $procId", script);
    }

    [Fact]
    public void GeneratePortableUpdateScript_ContainsCopyLogicAndRestart()
    {
        string script = AppUpdateService.GeneratePortableUpdateScript(
            5678,
            @"C:\Temp\extracted",
            @"C:\ReepaxPortable",
            @"C:\ReepaxPortable\Reepax.exe");

        Assert.Contains("Copy-Item", script);
        Assert.Contains("--restart", script);
        Assert.Contains("$procId = 5678", script);
        Assert.Contains("Wait-Process -Id $procId", script);
    }

    [Fact]
    public void GenerateScripts_ProperlyEscapesSingleQuotesInPaths()
    {
        string pathWithQuote = @"C:\Games\Florian's Folder\Reepax.exe";
        string script = AppUpdateService.GenerateInstallerUpdateScript(999, @"C:\Temp\setup.exe", pathWithQuote);

        Assert.Contains(@"C:\Games\Florian''s Folder\Reepax.exe", script);
        Assert.DoesNotContain(@"Florian's Folder", script);
    }

    [Fact]
    public void ApplyUpdate_InvokesOverrideInTestEnvironment()
    {
        string? capturedPath = null;
        bool? capturedIsPortable = null;

        AppUpdateService.ApplyUpdateActionOverride = (path, isPortable) =>
        {
            capturedPath = path;
            capturedIsPortable = isPortable;
        };

        try
        {
            bool success = AppUpdateService.ApplyUpdate(@"C:\Test\fake_update.exe", isPortable: false);

            Assert.True(success);
            Assert.Equal(@"C:\Test\fake_update.exe", capturedPath);
            Assert.False(capturedIsPortable);
        }
        finally
        {
            AppUpdateService.ApplyUpdateActionOverride = null;
        }
    }

    [Theory]
    [InlineData("26.10.1", "26.10.2", true)]
    [InlineData("26.10.1", "v26.10.2", true)]
    [InlineData("26.10.1", "26.10.1", false)]
    [InlineData("26.10.2", "26.10.1", false)]
    [InlineData("26.9.15", "26.10.1", true)]
    public void IsNewerVersion_CorrectlyComparesVersions(string current, string candidate, bool expectedNewer)
    {
        bool isNewer = AppUpdateService.IsNewerVersion(current, candidate);
        Assert.Equal(expectedNewer, isNewer);
    }

    [Theory]
    [InlineData("UpdateDialog_Button_InstallUpdate")]
    [InlineData("UpdateDialog_Button_ViewOnGitHub")]
    [InlineData("UpdateDialog_Button_Cancel")]
    [InlineData("UpdateDialog_Button_Close")]
    [InlineData("UpdateDialog_Button_Later")]
    [InlineData("UpdateDialog_Status_Starting")]
    [InlineData("UpdateDialog_Status_Progress")]
    [InlineData("UpdateDialog_Status_Preparing")]
    [InlineData("UpdateDialog_Status_ReadyRestarting")]
    [InlineData("UpdateDialog_Status_Failed")]
    [InlineData("UpdateDialog_Status_Cancelled")]
    public void Localization_UpdateKeysExistInBothLanguages(string key)
    {
        var loc = LocalizationService.Instance;

        loc.CurrentLanguage = "de";
        var deValue = loc[key];
        Assert.False(string.IsNullOrWhiteSpace(deValue), $"Key '{key}' missing in German translation.");

        loc.CurrentLanguage = "en";
        var enValue = loc[key];
        Assert.False(string.IsNullOrWhiteSpace(enValue), $"Key '{key}' missing in English translation.");
    }
}
