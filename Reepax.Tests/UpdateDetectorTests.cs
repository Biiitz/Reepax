using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Extractor;
using Xunit;

namespace Reepax.Tests;

public class UpdateDetectorTests
{
    [Theory]
    [InlineData("The_Blood_of_Dawnwalker_Update_from_v1.0.1_to_v1.0.2-Release.rar", true)]
    [InlineData("The.Blood.of.Dawnwalker.Update.v1.0.2-Release.rar", true)]
    [InlineData("Elden.Ring.Shadow.of.the.Erdtree.Update.1.13.2-Edition.rar", true)]
    [InlineData("God.of.War.Ragnarok.Patch.v1.0.1-Release.iso", true)]
    [InlineData("Black.Myth.Wukong.Hotfix.v1.0.8-Patch.7z", true)]
    [InlineData("The Blood of Dawnwalker Updates", true)]
    [InlineData("The Blood of Dawnwalker - Updates", true)]
    [InlineData("https://rapidgator.net/file/123/The_Blood_of_Dawnwalker_Update_from_v1.0.1_to_v1.0.2-Release.rar.html", true)]
    [InlineData("Cyberpunk.2077.v2.1.Update-Release.rar", true)]
    [InlineData("Grand.Theft.Auto.V.rar", false)]
    [InlineData("Cyberpunk.2077.part01.rar", false)]
    [InlineData("Setup.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsUpdate_DetectsUpdatePatterns(string? input, bool expected)
    {
        var result = UpdateDetector.IsUpdate(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("The_Blood_of_Dawnwalker_Update_from_v1.0.1_to_v1.0.2-Release.rar", "The Blood of Dawnwalker")]
    [InlineData("The.Blood.of.Dawnwalker.Update.v1.0.2-Release.rar", "The Blood of Dawnwalker")]
    [InlineData("The Blood of Dawnwalker Updates", "The Blood of Dawnwalker")]
    [InlineData("The Blood of Dawnwalker - Updates", "The Blood of Dawnwalker")]
    [InlineData("Elden.Ring.Shadow.of.the.Erdtree.Update.1.13.2-Edition.rar", "Elden Ring Shadow of the Erdtree")]
    [InlineData("God.of.War.Ragnarok.Patch.v1.0.1-Release.iso", "God of War Ragnarok")]
    [InlineData("Filecrypt - The Blood of Dawnwalker Updates", "The Blood of Dawnwalker")]
    [InlineData("www.mysite.com_The_Blood_of_Dawnwalker_Update_from_v1.0.1_to_v1.0.2-Release.rar", "The Blood of Dawnwalker")]
    public void ExtractGameName_ExtractsCleanGameTitle(string input, string expectedGame)
    {
        var result = UpdateDetector.ExtractGameName(input);
        Assert.Equal(expectedGame, result);
    }

    [Theory]
    [InlineData("The_Blood_of_Dawnwalker_Update_from_v1.0.1_to_v1.0.2-Release.rar", "The Blood of Dawnwalker - Updates")]
    [InlineData("The Blood of Dawnwalker Updates", "The Blood of Dawnwalker - Updates")]
    [InlineData("Filecrypt - The Blood of Dawnwalker Updates", "The Blood of Dawnwalker - Updates")]
    public void GetUpdatePackageName_FormatsCorrectly(string input, string expected)
    {
        var result = UpdateDetector.GetUpdatePackageName(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("The_Blood_of_Dawnwalker_Update_from_v1.0.1_to_v1.0.2-Release.rar", "The_Blood_of_Dawnwalker_Update_from_v1.0.1_to_v1.0.2-Release")]
    [InlineData("The_Blood_of_Dawnwalker_Update_from_v1.0.1_to_v1.0.2-Release.part01.rar", "The_Blood_of_Dawnwalker_Update_from_v1.0.1_to_v1.0.2-Release")]
    [InlineData(@"C:\Downloads\The_Blood_of_Dawnwalker_Update_from_v1.0.1_to_v1.0.2-Release.zip", "The_Blood_of_Dawnwalker_Update_from_v1.0.1_to_v1.0.2-Release")]
    public void GetExtractionSubfolder_ReturnsCleanSubfolder(string input, string expected)
    {
        var result = UpdateDetector.GetExtractionSubfolder(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void PackageGrouper_WithUpdateLink_NamesPackageWithDashUpdatesAndDisablesHostResolver()
    {
        var links = new List<ExtractedLink>
        {
            new()
            {
                Url = "https://rapidgator.net/file/123",
                RawFileName = "The_Blood_of_Dawnwalker_Update_from_v1.0.1_to_v1.0.2-Patch.rar",
                Hoster = new HosterInfo { DisplayName = "Rapidgator" }
            }
        };

        var packages = PackageGrouper.GroupLinksIntoPackages(links, @"C:\Downloads", autoResolveHostLinks: true);

        Assert.Single(packages);
        var pkg = packages[0];
        Assert.Equal("The Blood of Dawnwalker - Updates", pkg.Name);
        Assert.False(pkg.AutoResolveHostLinks); // Host auto-resolver must be disabled for updates
    }

    [Fact]
    public void LinkMetadataResolver_ExtractFileNameFromHtml_ExtractsFileNameAndContainerTitle()
    {
        // 1. From Page Title
        var html1 = "<html><head><title>Download file The_Blood_of_Dawnwalker_Update_from_v1.0.1_to_v1.0.2-Patch.rar</title></head><body></body></html>";
        var name1 = LinkMetadataResolverService.ExtractFileNameFromHtml(html1, "https://rapidgator.net/file/123");
        Assert.Equal("The_Blood_of_Dawnwalker_Update_from_v1.0.1_to_v1.0.2-Patch.rar", name1);

        // 2. From og:title
        var html2 = "<html><head><meta property=\"og:title\" content=\"The_Blood_of_Dawnwalker_Update_from_v1.0.1_to_v1.0.2-Patch.rar\" /></head><body></body></html>";
        var name2 = LinkMetadataResolverService.ExtractFileNameFromHtml(html2, "https://hoster.com/file");
        Assert.Equal("The_Blood_of_Dawnwalker_Update_from_v1.0.1_to_v1.0.2-Patch.rar", name2);

        // 3. From Heading (H2)
        var html3 = "<html><body><h2 class=\"title\">The_Blood_of_Dawnwalker_Update_from_v1.0.1_to_v1.0.2-Patch.rar</h2></body></html>";
        var name3 = LinkMetadataResolverService.ExtractFileNameFromHtml(html3, "https://ddownload.com/xyz");
        Assert.Equal("The_Blood_of_Dawnwalker_Update_from_v1.0.1_to_v1.0.2-Patch.rar", name3);
    }

    [Fact]
    public void TryUpdatePackageName_RenamesGenericFilecryptPackageToGameUpdates()
    {
        var package = new DownloadPackage
        {
            Name = "Filecrypt Package (2 files)",
            SaveDirectory = @"C:\Downloads\Filecrypt Package (2 files)",
            AutoResolveHostLinks = true
        };

        var item1 = new DownloadItem
        {
            FileName = "The_Blood_of_Dawnwalker_Update_from_v1.0.1_to_v1.0.2-Patch.rar",
            OriginalUrl = "https://filecrypt.cc/Container/123"
        };
        package.Items.Add(item1);

        LinkMetadataResolverService.TryUpdatePackageName(package);

        Assert.Equal("The Blood of Dawnwalker - Updates", package.Name);
        Assert.False(package.AutoResolveHostLinks);
        Assert.EndsWith("The Blood of Dawnwalker - Updates", package.SaveDirectory);
    }

    [Theory]
    [InlineData("Filecrypt Package (2 files)", true)]
    [InlineData("Filecrypt Paket (2 Dateien)", true)]
    [InlineData("Rapidgator Package (5 files)", true)]
    [InlineData("DDownload Download (2026-09-05 1800)", true)]
    [InlineData("Download Paket", true)]
    [InlineData("Download Package", true)]
    [InlineData("Download_Paket_2026", true)]
    [InlineData("The Blood of Dawnwalker - Updates", false)]
    [InlineData("The Blood of Dawnwalker", false)]
    [InlineData("Elden Ring", false)]
    public void IsGenericOrCrypticName_DetectsGenericNamesAccurately(string name, bool expected)
    {
        var actual = PackageGrouper.IsGenericOrCrypticName(name);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DownloadPackage_ItemRename_AutomaticallyRenamesPackageToGameUpdates()
    {
        var package = new DownloadPackage
        {
            Name = "Filecrypt Package (2 files)",
            SaveDirectory = @"C:\Downloads\Filecrypt Package (2 files)",
            AutoResolveHostLinks = true
        };

        var item = new DownloadItem
        {
            FileName = "download_file",
            OriginalUrl = "https://filecrypt.cc/Container/123",
            SaveFilePath = @"C:\Downloads\Filecrypt Package (2 files)\download_file"
        };
        package.Items.Add(item);

        // When download starts and item is renamed from probe/interception:
        item.Rename("The_Blood_of_Dawnwalker_Update_from_v1.0.1_to_v1.0.2-Patch.rar");

        Assert.Equal("The Blood of Dawnwalker - Updates", package.Name);
        Assert.False(package.AutoResolveHostLinks);
        Assert.EndsWith("The Blood of Dawnwalker - Updates", package.SaveDirectory);
        Assert.EndsWith("The_Blood_of_Dawnwalker_Update_from_v1.0.1_to_v1.0.2-Patch.rar", item.SaveFilePath);
    }
}
