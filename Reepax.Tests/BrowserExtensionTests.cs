using System.IO;
using System.Net;
using Reepax.Services.Browser;
using Reepax.Services.Extractor;
using Xunit;

namespace Reepax.Tests;

public class BrowserExtensionTests
{
    private static string CreateExtensionFolder(string manifest)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ReepaxExt_" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), manifest);
        return dir;
    }

    [Fact]
    public void ParseManifest_ReadsNameVersionIconAndOptionsPage()
    {
        var dir = CreateExtensionFolder(@"{
            ""manifest_version"": 3,
            ""name"": ""Test Blocker"",
            ""version"": ""1.2.3"",
            ""icons"": { ""16"": ""icon16.png"", ""128"": ""icon128.png"" },
            ""options_ui"": { ""page"": ""options.html"" }
        }");
        try
        {
            File.WriteAllText(Path.Combine(dir, "icon128.png"), "fake");

            var info = BrowserExtensionService.ParseManifest(dir, Path.Combine(dir, "manifest.json"));

            Assert.NotNull(info);
            Assert.Equal("Test Blocker", info!.Name);
            Assert.Equal("1.2.3", info.Version);
            Assert.Equal("options.html", info.OptionsPage);
            Assert.EndsWith("icon128.png", info.IconPath!);
            Assert.True(info.IsEnabled);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void ParseManifest_SupportsLegacyOptionsPageAndMissingIcons()
    {
        var dir = CreateExtensionFolder(@"{
            ""manifest_version"": 2,
            ""name"": ""Old Addon"",
            ""options_page"": ""settings/main.html""
        }");
        try
        {
            var info = BrowserExtensionService.ParseManifest(dir, Path.Combine(dir, "manifest.json"));

            Assert.NotNull(info);
            Assert.Equal("Old Addon", info!.Name);
            Assert.Equal("settings/main.html", info.OptionsPage);
            Assert.Null(info.IconPath);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void ParseManifest_ReturnsNullOnInvalidJson()
    {
        var dir = CreateExtensionFolder("{ das ist kein json !!!");
        try
        {
            var info = BrowserExtensionService.ParseManifest(dir, Path.Combine(dir, "manifest.json"));
            Assert.Null(info);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void ParseManifest_FallsBackToFolderNameForMissingName()
    {
        var dir = CreateExtensionFolder(@"{ ""manifest_version"": 3, ""version"": ""1.0"" }");
        try
        {
            var info = BrowserExtensionService.ParseManifest(dir, Path.Combine(dir, "manifest.json"));

            Assert.NotNull(info);
            Assert.Equal(info!.FolderName, info.Name);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Theory]
    [InlineData("uBlock Origin Lite", "uBlock-Origin-Lite-Chrome-Web-Store", true)]
    [InlineData("AdBlock Plus", "adblock-plus", true)]
    [InlineData("AdGuard AdBlocker", "adguard-folder", true)]
    [InlineData("Redirect Blocker", "redirect-blocker-ext", true)]
    [InlineData("Redirect Shield", "redirect-shield", true)]
    [InlineData("Pop up Blocker", "popup-blocker", true)]
    [InlineData("Custom Tool", "adguard-extension", true)]
    [InlineData("Dark Reader", "Dark-Reader-Chrome-Web-Store", false)]
    [InlineData("Tampermonkey", "tampermonkey-chrome", false)]
    [InlineData("Cookie AutoDelete", "cookie-autodelete", false)]
    [InlineData("Translator", "google-translate", false)]
    public void IsBlockerExtension_IdentifiesBlockersCorrectly(string name, string folderName, bool expected)
    {
        var ext = new BrowserExtensionInfo
        {
            Name = name,
            FolderName = folderName
        };

        Assert.Equal(expected, BrowserExtensionService.IsBlockerExtension(ext));
        Assert.Equal(expected, ext.IsBlocker);
    }

    [Fact]
    public void ParseManifest_SupportsTrailingCommasAndComments()
    {
        var jsonWithCommentsAndCommas = @"
        // Extension manifest
        {
            ""manifest_version"": 3,
            ""name"": ""Commented Extension"",
            ""version"": ""2.0.0"",
            ""permissions"": [
                ""storage"",
            ],
        }";
        var dir = CreateExtensionFolder(jsonWithCommentsAndCommas);
        try
        {
            var info = BrowserExtensionService.ParseManifest(dir, Path.Combine(dir, "manifest.json"));
            Assert.NotNull(info);
            Assert.Equal("Commented Extension", info!.Name);
            Assert.Equal("2.0.0", info.Version);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void ParseManifest_ResolvesLocalizedNameWithCaseInsensitivity()
    {
        var dir = CreateExtensionFolder(@"{
            ""manifest_version"": 3,
            ""name"": ""__MSG_extensionName__"",
            ""default_locale"": ""en""
        }");
        try
        {
            var locales = Path.Combine(dir, "_locales", "en");
            Directory.CreateDirectory(locales);
            File.WriteAllText(Path.Combine(locales, "messages.json"), @"{
                ""extensionname"": {
                    ""message"": ""Localized Extension Name""
                }
            }");

            var info = BrowserExtensionService.ParseManifest(dir, Path.Combine(dir, "manifest.json"));
            Assert.NotNull(info);
            Assert.Equal("Localized Extension Name", info!.Name);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void ParseManifest_ResolvesIconWithLeadingSlashAndSubfolder()
    {
        var dir = CreateExtensionFolder(@"{
            ""manifest_version"": 3,
            ""name"": ""Icon Test"",
            ""icons"": {
                ""128"": ""/assets/icons/128.png""
            }
        }");
        try
        {
            var iconDir = Path.Combine(dir, "assets", "icons");
            Directory.CreateDirectory(iconDir);
            var iconFile = Path.Combine(iconDir, "128.png");
            File.WriteAllText(iconFile, "dummy");

            var info = BrowserExtensionService.ParseManifest(dir, Path.Combine(dir, "manifest.json"));
            Assert.NotNull(info);
            Assert.NotNull(info!.IconPath);
            Assert.True(File.Exists(info.IconPath));
            Assert.Equal(iconFile, info.IconPath);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void ParseManifest_WorksWithNestedFolderAndManifest()
    {
        var topDir = Path.Combine(Path.GetTempPath(), "ReepaxExtNested_" + Path.GetRandomFileName());
        var subDir = Path.Combine(topDir, "extension", "dist");
        Directory.CreateDirectory(subDir);
        var manifestPath = Path.Combine(subDir, "manifest.json");
        File.WriteAllText(manifestPath, @"{
            ""manifest_version"": 3,
            ""name"": ""Nested Extension"",
            ""version"": ""3.1.4""
        }");

        try
        {
            var info = BrowserExtensionService.ParseManifest(subDir, manifestPath);
            Assert.NotNull(info);
            Assert.Equal("Nested Extension", info!.Name);
            Assert.Equal("3.1.4", info.Version);
            Assert.Equal(subDir, info.FolderPath);
        }
        finally
        {
            try { Directory.Delete(topDir, true); } catch { }
        }
    }

    [Fact]
    public void Scan_FindsExtensionsInAppDataIfPresent()
    {
        var service = BrowserExtensionService.Instance;
        service.Scan();
        foreach (var ext in service.Extensions)
        {
            System.Console.WriteLine($"Found extension: Name='{ext.Name}', Folder='{ext.FolderName}', Version='{ext.Version}', Options='{ext.OptionsPage}', Popup='{ext.PopupPage}', Icon='{ext.IconPath}', Blocker={ext.IsBlocker}");
        }
    }

    [Fact]
    public void ParseManifest_ReadsShortNameAndPopupPage()
    {
        var dir = CreateExtensionFolder(@"{
            ""manifest_version"": 3,
            ""name"": ""uBlock Origin Lite"",
            ""short_name"": ""uBO Lite"",
            ""version"": ""2026.907.2003"",
            ""action"": {
                ""default_popup"": ""popup.html""
            },
            ""options_page"": ""dashboard.html""
        }");
        try
        {
            var info = BrowserExtensionService.ParseManifest(dir, Path.Combine(dir, "manifest.json"));
            Assert.NotNull(info);
            Assert.Equal("uBlock Origin Lite", info!.Name);
            Assert.Equal("uBO Lite", info.ShortName);
            Assert.Equal("popup.html", info.PopupPage);
            Assert.Equal("dashboard.html", info.OptionsPage);
            Assert.True(info.HasPage);

            info.InstalledExtensionId = "oplbdboppnejnjiembdfpdokoepmbcap";
            Assert.Equal("chrome-extension://oplbdboppnejnjiembdfpdokoepmbcap/popup.html", info.PopupPageUrl);
            Assert.Equal("chrome-extension://oplbdboppnejnjiembdfpdokoepmbcap/dashboard.html", info.OptionsPageUrl);
            Assert.Equal("chrome-extension://oplbdboppnejnjiembdfpdokoepmbcap/popup.html", info.PopupOrOptionsUrl);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void BrowserSecurityGuard_ExemptsInternalSchemes()
    {
        var extensionUrl = "chrome-extension://oplbdboppnejnjiembdfpdokoepmbcap/web_accessible_resources/noop.js";
        var popupUrl = "chrome-extension://oplbdboppnejnjiembdfpdokoepmbcap/popup.html";
        var blobUrl = "blob:https://example.com/12345";
        var dataUrl = "data:text/javascript;base64,AAAA";

        Assert.False(Reepax.Services.AdBlock.AdBlockRustEngine.Instance.ShouldBlock(extensionUrl));
        Assert.False(Reepax.Services.AdBlock.AdBlockRustEngine.Instance.ShouldBlock(popupUrl));
        Assert.False(Reepax.Services.AdBlock.AdBlockRustEngine.Instance.ShouldBlock(blobUrl));
        Assert.False(Reepax.Services.AdBlock.AdBlockRustEngine.Instance.ShouldBlock(dataUrl));

        Assert.True(Reepax.Services.Browser.BrowserSecurityGuard.IsWhitelisted(extensionUrl));
        Assert.True(Reepax.Services.Browser.BrowserSecurityGuard.IsWhitelisted(popupUrl));
        Assert.True(Reepax.Services.Browser.BrowserSecurityGuard.IsWhitelisted(blobUrl));
        Assert.True(Reepax.Services.Browser.BrowserSecurityGuard.IsWhitelisted(dataUrl));
    }
}

public class CloudflareDetectionTests
{
    [Fact]
    public void IsCloudflareChallenge_DetectsChallengePage()
    {
        var html = "<!DOCTYPE html><html><head><title>Just a moment...</title></head><body>challenge</body></html>";
        Assert.True(FastHostResolver.IsCloudflareChallenge(HttpStatusCode.Forbidden, html));
    }

    [Fact]
    public void IsCloudflareChallenge_DoesNotFlagPagesThatMerelyEmbedTurnstile()
    {
        // Pages with embedded Turnstile widget (status 200) are NOT challenge pages
        var html = "<html><head><script src=\"https://challenges.cloudflare.com/turnstile/v0/api.js\"></script></head><body>Download</body></html>";
        Assert.False(FastHostResolver.IsCloudflareChallenge(HttpStatusCode.OK, html));
    }

    [Fact]
    public void IsCloudflareChallenge_DoesNotFlagNormalPages()
    {
        var html = "<html><head><title>FastHost - Download</title></head><body><a href=\"https://example.com/dl/x\">dl</a></body></html>";
        Assert.False(FastHostResolver.IsCloudflareChallenge(HttpStatusCode.OK, html));
        Assert.False(FastHostResolver.IsCloudflareChallenge(HttpStatusCode.NotFound, html));
    }

    [Fact]
    public void IsCloudflareChallenge_HandlesNullOrEmpty()
    {
        Assert.False(FastHostResolver.IsCloudflareChallenge(HttpStatusCode.Forbidden, null));
        Assert.False(FastHostResolver.IsCloudflareChallenge(HttpStatusCode.Forbidden, ""));
    }
}
