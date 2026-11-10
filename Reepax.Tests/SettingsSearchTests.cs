using Reepax.Services.Localization;
using Reepax.Services.Settings;
using Reepax.ViewModels;

namespace Reepax.Tests;

public class SettingsSearchTests
{
    private static readonly string[] Keys =
    {
        "Settings_Theme_Title", "Settings_Language_Title", "Settings_AccentColor_Title",
        "Settings_SpeedLimit_Title", "Settings_ConcurrentDownloads_Title", "Settings_Connections_Title",
        "Settings_Card_ArchivePasswords_Title", "Settings_AutoPar2Repair_Title", "Settings_LowResource_Title",
        "Settings_MinimizeToTrayOnClose_Title", "Settings_EnableLogging_Title", "Settings_Sound_Completion_Title"
    };

    private static SettingsSearchIndex CreateIndex() => new(Keys.Select(key => new SettingsSearchEntry(key,
        SettingsCategory.DownloadConnections, key[..^6] + "_Subtitle", Array.Empty<string>())));

    [Theory]
    [InlineData("the", "Settings_Theme_Title")]
    [InlineData("hem", "Settings_Theme_Title")]
    [InlineData("eme", "Settings_Theme_Title")]
    [InlineData("design", "Settings_Theme_Title")]
    [InlineData("pra", "Settings_Language_Title")]
    [InlineData(" SPEED   LIMIT ", "Settings_SpeedLimit_Title")]
    [InlineData("downloadgeschwindigkeit", "Settings_SpeedLimit_Title")]
    [InlineData("connections per", "Settings_Connections_Title")]
    [InlineData("  number\tof\nconnections  ", "Settings_Connections_Title")]
    [InlineData("passwoerter", "Settings_Card_ArchivePasswords_Title")]
    [InlineData("PASSWÖRTER", "Settings_Card_ArchivePasswords_Title")]
    [InlineData("passworter", "Settings_Card_ArchivePasswords_Title")]
    [InlineData("protected archives", "Settings_Card_ArchivePasswords_Title")]
    public void Search_FindsContiguousTextInBothLanguages(string query, string expected)
    {
        Assert.Equal(expected, CreateIndex().Search(query).First().Key);
    }

    [Theory]
    [InlineData("Settings_Theme_Title", "Theme")]
    [InlineData("Settings_Theme_Title", "Design")]
    [InlineData("Settings_Language_Title", "Sprache")]
    public void Search_RefinesResultsDuringTypingAndRestoresThemDuringDeletion(string key, string title)
    {
        var index = CreateIndex();
        var previous = index.Search(title[..1]).Select(entry => entry.Key).ToHashSet();
        for (int length = 1; length <= title.Length; length++)
        {
            var current = index.Search(title[..length]).Select(entry => entry.Key).ToHashSet();
            Assert.Contains(key, current);
            Assert.True(current.IsSubsetOf(previous));
            previous = current;
        }
        for (int length = title.Length; length > 0; length--)
        {
            var current = index.Search(title[..length]).Select(entry => entry.Key).ToHashSet();
            Assert.Contains(key, current);
            Assert.True(previous.IsSubsetOf(current));
            previous = current;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("???")]
    [InlineData("thm")]
    [InlineData("theme design")]
    [InlineData("design theme")]
    [InlineData("speed limit limit")]
    [InlineData("number connections")]
    [InlineData("bandwidth")]
    [InlineData("hotkeys")]
    [InlineData("downloadgeschwindikeit")]
    [InlineData("change the theme")]
    [InlineData("how do I")]
    [InlineData("ExampleUnknownPrivateToken")]
    public void Search_RejectsGapsTyposAliasesAndTextSpanningDifferentTranslations(string? query)
    {
        Assert.Empty(CreateIndex().Search(query));
    }

    [Theory]
    [InlineData("ausschließen", "ausschliessen")]
    [InlineData("größer", "groesser")]
    [InlineData("größer", "grosser")]
    [InlineData("wählen", "waehlen")]
    [InlineData("für", "fuer")]
    [InlineData("fu\u0308r", "FÜR")]
    public void Search_EquatesUmlautsAndTheirSpellingsInVisibleOptions(string option, string query)
    {
        var entry = new SettingsSearchEntry("Settings_Theme_Title", SettingsCategory.General, "", Array.Empty<string>())
        {
            OptionTexts = new[] { option }
        };
        Assert.Same(entry, Assert.Single(new SettingsSearchIndex(new[] { entry }).Search(query)));
    }

    [Fact]
    public void Search_RanksExactTitlesThenPrefixesThenSubstringsThenDetails()
    {
        var exact = new SettingsSearchEntry("Settings_Sound_Volume_InlineTitle", SettingsCategory.General, "", Array.Empty<string>());
        var completion = new SettingsSearchEntry("Settings_Sound_Completion_Title", SettingsCategory.General, "", Array.Empty<string>());
        var failure = new SettingsSearchEntry("Settings_Sound_Error_Title", SettingsCategory.General, "", Array.Empty<string>());
        var details = new SettingsSearchEntry("Settings_Theme_Title", SettingsCategory.General, "Settings_Sound_Volume_Subtitle", Array.Empty<string>());
        var volumeTitle = new SettingsSearchEntry("Settings_Sound_Volume_Title", SettingsCategory.General, "", Array.Empty<string>());
        var index = new SettingsSearchIndex(new[] { details, volumeTitle, failure, completion, exact });
        Assert.Equal(new[] { exact, volumeTitle, details }, index.Search("volume"));
        Assert.Equal(new[] { volumeTitle, completion, failure }, index.Search("sound"));
    }

    [Fact]
    public void Search_SortsEqualMatchesByTheDisplayedTitleInEitherLanguage()
    {
        var entries = new[]
        {
            new SettingsSearchEntry("Settings_Language_Title", SettingsCategory.General, "", new[] { "PostDownload_Action_Sleep" }),
            new SettingsSearchEntry("Settings_Theme_Title", SettingsCategory.General, "", new[] { "PostDownload_Action_Sleep" })
        };
        string previous = LocalizationService.Instance.CurrentLanguage;
        try
        {
            var index = new SettingsSearchIndex(entries);
            foreach (string language in new[] { "en", "de" })
            {
                LocalizationService.Instance.CurrentLanguage = language;
                var expected = entries.OrderBy(entry => entry.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
                Assert.Equal(expected, index.Search("standby"));
            }
        }
        finally { LocalizationService.Instance.CurrentLanguage = previous; }
    }

    [Fact]
    public void Search_KeepsIndividualOptionLabelsAndDescriptionsSeparate()
    {
        var entry = new SettingsSearchEntry("Settings_PostDownload_Title", SettingsCategory.General,
            "Settings_PostDownload_Subtitle", new[] { "PostDownload_Action_Shutdown", "PostDownload_Action_Sleep" });
        var index = new SettingsSearchIndex(new[] { entry, entry });
        Assert.Same(entry, Assert.Single(index.Search("Shut down PC")));
        Assert.Same(entry, Assert.Single(index.Search("PC herunterfahren")));
        Assert.Same(entry, Assert.Single(index.Search("Sleep (Standby)")));
        Assert.Same(entry, Assert.Single(index.Search(Strings_en.Map[entry.DescriptionKey])));
        Assert.Empty(index.Search("PC Sleep"));
        Assert.Empty(index.Search("herunterfahren Sleep"));
        Assert.Empty(index.Search(new string('x', 10000)));
    }

    [Fact]
    public void Search_HandlesUnicodeAndIncompletePastedCharacters()
    {
        var index = CreateIndex();
        Assert.Equal("Settings_SpeedLimit_Title", index.Search("\U0001F50A speed").First().Key);
        Assert.Equal("Settings_SpeedLimit_Title", index.Search(new string((char)0xD800, 1) + "speed").First().Key);
        Assert.Empty(index.Search(new string(' ', 255) + "\U0001F50A"));
    }
}
