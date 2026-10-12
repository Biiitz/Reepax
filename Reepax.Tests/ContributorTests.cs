using System;
using System.Linq;
using Reepax.Models;
using Reepax.Services.Localization;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

public class ContributorTests
{
    [Fact]
    public void ContributorInfo_BiiitzHasCrown_AndCorrectProfileUrl()
    {
        var vm = new MainViewModel();
        Assert.Single(vm.Contributors);
        var biiitz = vm.Contributors.First();

        Assert.Equal("Biiitz", biiitz.Username);
        Assert.Equal("https://github.com/Biiitz", biiitz.ProfileUrl);
        Assert.True(biiitz.HasCrown);
    }

    [Fact]
    public void ContributorsNotice_ExplicitlyRequiresMergedPRInBothLanguages()
    {
        var loc = LocalizationService.Instance;

        loc.CurrentLanguage = "de";
        var noticeDe = loc.Get("About_Contributors_Notice");
        Assert.Contains("gemerged", noticeDe, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("offizieller GitHub-Contributor", noticeDe, StringComparison.OrdinalIgnoreCase);

        loc.CurrentLanguage = "en";
        var noticeEn = loc.Get("About_Contributors_Notice");
        Assert.Contains("merged", noticeEn, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("official GitHub contributor", noticeEn, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContributorsTitle_IsSimplyContributorsWithoutHallOfFame()
    {
        var loc = LocalizationService.Instance;
        loc.CurrentLanguage = "de";
        Assert.Equal("Contributors", loc.Get("About_Contributors_Title"));
        loc.CurrentLanguage = "en";
        Assert.Equal("Contributors", loc.Get("About_Contributors_Title"));
    }

    [Fact]
    public void ContributorCrown_HasLocalizedToolTipInBothLanguages()
    {
        var loc = LocalizationService.Instance;
        loc.CurrentLanguage = "de";
        Assert.Equal("Projektgründer & Hauptentwickler", loc.Get("About_Contributor_CrownToolTip"));
        loc.CurrentLanguage = "en";
        Assert.Equal("Project Founder & Lead Maintainer", loc.Get("About_Contributor_CrownToolTip"));
    }

    [Fact]
    public void ContributeButton_HasLocalizedTextInBothLanguages()
    {
        var loc = LocalizationService.Instance;
        loc.CurrentLanguage = "de";
        Assert.Equal("Auf GitHub mitwirken ↗", loc.Get("About_Community_ContributeButton"));
        loc.CurrentLanguage = "en";
        Assert.Equal("Contribute on GitHub ↗", loc.Get("About_Community_ContributeButton"));
    }
}
