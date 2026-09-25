using System.ComponentModel;
using Reepax.Services.Localization;
using Xunit;

namespace Reepax.Tests;

public class LocalizationTests
{
    [Fact]
    public void LocalizationService_DefaultLanguageIsEnglish()
    {
        var loc = LocalizationService.Instance;
        Assert.Equal("en", loc.CurrentLanguage);
        Assert.Equal("Queued", loc["Status_Queued"]);
        Assert.Equal("Pause", loc["Common_Pause"]);
        Assert.Equal("Settings", loc["StatusBar_Settings_ToolTip"]);
    }

    [Fact]
    public void LocalizationService_CanSwitchBetweenEnglishAndGerman()
    {
        var loc = LocalizationService.Instance;

        try
        {
            // Switch to German
            loc.CurrentLanguage = "de";
            Assert.Equal("de", loc.CurrentLanguage);
            Assert.Equal("In Warteschlange", loc["Status_Queued"]);
            Assert.Equal("Pausieren", loc["Common_Pause"]);
            Assert.Equal("Einstellungen", loc["StatusBar_Settings_ToolTip"]);

            // Switch back to English
            loc.CurrentLanguage = "en";
            Assert.Equal("en", loc.CurrentLanguage);
            Assert.Equal("Queued", loc["Status_Queued"]);
            Assert.Equal("Pause", loc["Common_Pause"]);
            Assert.Equal("Settings", loc["StatusBar_Settings_ToolTip"]);
        }
        finally
        {
            loc.CurrentLanguage = "en";
        }
    }

    [Fact]
    public void LocalizationService_FiresPropertyChangedOnLanguageChange()
    {
        var loc = LocalizationService.Instance;
        bool indexerChanged = false;
        bool allPropertiesChanged = false;

        PropertyChangedEventHandler handler = (s, e) =>
        {
            if (e.PropertyName == "Item[]")
                indexerChanged = true;
            if (string.IsNullOrEmpty(e.PropertyName))
                allPropertiesChanged = true;
        };

        loc.PropertyChanged += handler;
        try
        {
            loc.CurrentLanguage = loc.CurrentLanguage == "en" ? "de" : "en";
            Assert.True(indexerChanged);
            Assert.True(allPropertiesChanged);
        }
        finally
        {
            loc.PropertyChanged -= handler;
            loc.CurrentLanguage = "en";
        }
    }

    [Fact]
    public void LocalizationService_LocFormatWorksAcrossLanguages()
    {
        var loc = LocalizationService.Instance;

        try
        {
            loc.CurrentLanguage = "en";
            var enResult = Loc.Format("Package_HosterMultiFilesName", "Mega", 3);
            Assert.Equal("Mega Package (3 files)", enResult);

            loc.CurrentLanguage = "de";
            var deResult = Loc.Format("Package_HosterMultiFilesName", "Mega", 3);
            Assert.Equal("Mega Paket (3 Dateien)", deResult);
        }
        finally
        {
            loc.CurrentLanguage = "en";
        }
    }

    [Fact]
    public void LocalizationService_FallbackReturnsKeyIfUnknown()
    {
        var result = Loc.Get("NonExistent_UniqueKey_XYZ");
        Assert.Equal("NonExistent_UniqueKey_XYZ", result);
    }

    [Fact]
    public void LocalizationService_TabsAndSettingsKeysExistInBothLanguages()
    {
        var keys = new[]
        {
            "Tab_Downloads",
            "Tab_Settings",
            "QuickSettings_Title",
            "QuickSettings_OpenAllSettings",
            "Settings_Category_General",
            "Settings_Category_Downloads",
            "Settings_Category_Extraction",
            "Settings_Category_Notifications",
            "Settings_Category_Appearance",
            "Settings_Card_DownloadDir_Title",
            "Settings_Card_DownloadDir_Desc",
            "Settings_Card_OpenFolder",
            "Settings_Card_Appearance_ThemeTitle",
            "Settings_Card_Appearance_ThemeDesc",
            "Settings_Card_Appearance_AccentTitle",
            "Settings_Card_Appearance_AccentDesc",
            "Settings_Card_Notifications_Hint",
            "Settings_Extensions_Title",
            "Settings_Extensions_Subtitle",
            "Settings_Extensions_OpenFolder"
        };

        var loc = LocalizationService.Instance;

        try
        {
            loc.CurrentLanguage = "en";
            foreach (var key in keys)
            {
                var val = loc[key];
                Assert.False(string.IsNullOrWhiteSpace(val), $"Key {key} returned empty in EN");
                Assert.NotEqual(key, val); // should not fallback to key name
            }

            loc.CurrentLanguage = "de";
            foreach (var key in keys)
            {
                var val = loc[key];
                Assert.False(string.IsNullOrWhiteSpace(val), $"Key {key} returned empty in DE");
                Assert.NotEqual(key, val); // should not fallback to key name
            }
        }
        finally
        {
            loc.CurrentLanguage = "en";
        }
    }

    [Fact]
    public void LocalizationService_EditPackageKeysExistInBothLanguages()
    {
        var keys = new[]
        {
            "Menu_EditPackage",
            "Dialog_EditPackageTitle",
            "Dialog_Button_SavePackage",
            "Status_PackageUpdated"
        };

        var loc = LocalizationService.Instance;

        try
        {
            loc.CurrentLanguage = "en";
            foreach (var key in keys)
            {
                var val = loc[key];
                Assert.False(string.IsNullOrWhiteSpace(val), $"Key {key} returned empty in EN");
                Assert.NotEqual(key, val);
            }

            loc.CurrentLanguage = "de";
            foreach (var key in keys)
            {
                var val = loc[key];
                Assert.False(string.IsNullOrWhiteSpace(val), $"Key {key} returned empty in DE");
                Assert.NotEqual(key, val);
            }
        }
        finally
        {
            loc.CurrentLanguage = "en";
        }
    }
}
