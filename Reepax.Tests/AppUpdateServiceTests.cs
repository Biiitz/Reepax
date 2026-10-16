using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
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
            SetupAssetUrl = "https://github.com/Biiitz/Reepax/releases/download/v26.10.4/setup.exe",
            SetupAssetSize = 1000,
            PortableZipAssetUrl = "https://github.com/Biiitz/Reepax/releases/download/v26.10.4/portable.zip",
            PortableZipAssetSize = 2000
        };

        bool originalPortable = SettingsService.IsPortableMode;
        try
        {
            SettingsService.IsPortableMode = false;

            Assert.True(info.HasDirectAsset);
            Assert.Equal("https://github.com/Biiitz/Reepax/releases/download/v26.10.4/setup.exe", info.TargetDownloadUrl);
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
            SetupAssetUrl = "https://github.com/Biiitz/Reepax/releases/download/v26.10.4/setup.exe",
            SetupAssetSize = 1000,
            PortableZipAssetUrl = "https://github.com/Biiitz/Reepax/releases/download/v26.10.4/portable.zip",
            PortableZipAssetSize = 2000
        };

        bool originalPortable = SettingsService.IsPortableMode;
        try
        {
            SettingsService.IsPortableMode = true;

            Assert.True(info.HasDirectAsset);
            Assert.Equal("https://github.com/Biiitz/Reepax/releases/download/v26.10.4/portable.zip", info.TargetDownloadUrl);
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
    [InlineData("26.10.1", "26.10.4", true)]
    [InlineData("26.10.1", "v26.10.4", true)]
    [InlineData("26.10.1", "26.10.1", false)]
    [InlineData("26.10.4", "26.10.1", false)]
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
    [InlineData("UpdateDialog_Status_Verifying")]
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

    [Fact]
    public void ParseSha256Checksums_ParsesVariousFormatsCorrectly()
    {
        string sampleShaFile = @"
# Comments should be ignored
e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  setup.exe
2c26b46b68ffc68ff99b453c1d30413413422d706483bfa0f98a5e886266e7ae *portable.zip
SHA256 (archive.zip) = fcde2b2edba56bf408601fb721fe9b5c338d10ee429ea04fae5511b68fbf8fb9
custom.exe: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
";
        var parsed = AppUpdateService.ParseSha256Checksums(sampleShaFile);

        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", parsed["setup.exe"]);
        Assert.Equal("2c26b46b68ffc68ff99b453c1d30413413422d706483bfa0f98a5e886266e7ae", parsed["portable.zip"]);
        Assert.Equal("fcde2b2edba56bf408601fb721fe9b5c338d10ee429ea04fae5511b68fbf8fb9", parsed["archive.zip"]);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", parsed["custom.exe"]);
    }

    [Fact]
    public async Task DownloadUpdateAsync_ThrowsWhenFileSizeDoesNotMatchExpected()
    {
        var testData = System.Text.Encoding.UTF8.GetBytes("Small payload");
        var handler = new TestMockHttpMessageHandler((req, ct) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(testData)
            };
            return Task.FromResult(response);
        });

        using var httpClient = new HttpClient(handler);
        var service = new AppUpdateService(httpClient, httpClient);

        var updateInfo = new UpdateInfo
        {
            SetupAssetUrl = "https://example.com/setup.exe",
            SetupAssetSize = 9999 // Intentionally expected size is much larger
        };

        SettingsService.IsPortableMode = false;

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await service.DownloadUpdateAsync(updateInfo);
        });
    }

    [Fact]
    public async Task DownloadUpdateAsync_ThrowsWhenSha256ChecksumMismatches()
    {
        var testData = System.Text.Encoding.UTF8.GetBytes("Integrity check test content");
        var handler = new TestMockHttpMessageHandler((req, ct) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(testData)
            };
            return Task.FromResult(response);
        });

        using var httpClient = new HttpClient(handler);
        var service = new AppUpdateService(httpClient, httpClient);

        var updateInfo = new UpdateInfo
        {
            SetupAssetUrl = "https://example.com/setup.exe",
            SetupAssetSize = testData.Length
        };
        updateInfo.SetExpectedSha256("setup.exe", "0000000000000000000000000000000000000000000000000000000000000000"); // Wrong hash

        SettingsService.IsPortableMode = false;

        var ex = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await service.DownloadUpdateAsync(updateInfo);
        });

        Assert.Contains("SHA-256 mismatch", ex.Message);
    }

    [Fact]
    public async Task DownloadUpdateAsync_SucceedsAndSetsVerifiedSha256WhenHashMatches()
    {
        var testData = System.Text.Encoding.UTF8.GetBytes("Authentic verified Reepax binary update content");
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var expectedHash = Convert.ToHexString(sha256.ComputeHash(testData)).ToLowerInvariant();

        var handler = new TestMockHttpMessageHandler((req, ct) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(testData)
            };
            return Task.FromResult(response);
        });

        using var httpClient = new HttpClient(handler);
        var service = new AppUpdateService(httpClient, httpClient);

        var updateInfo = new UpdateInfo
        {
            SetupAssetUrl = "https://example.com/setup.exe",
            SetupAssetSize = testData.Length
        };
        updateInfo.SetExpectedSha256("setup.exe", expectedHash);

        SettingsService.IsPortableMode = false;

        var path = await service.DownloadUpdateAsync(updateInfo);

        Assert.True(File.Exists(path));
        Assert.Equal(expectedHash, updateInfo.VerifiedSha256);
        Assert.Equal(testData, File.ReadAllBytes(path));
    }

    [Fact]
    public void GenerateInstallerUpdateScript_ContainsSha256IntegrityCheckWhenProvided()
    {
        string expectedHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        string script = AppUpdateService.GenerateInstallerUpdateScript(
            1234,
            @"C:\Temp\setup.exe",
            @"C:\Programs\Reepax\Reepax.exe",
            expectedHash);

        Assert.Contains(expectedHash, script);
        Assert.Contains("Get-FileHash", script);
        Assert.Contains("$currentHash -ne $expectedHash", script);
    }

    [Fact]
    public void VerifyAuthenticodeSignature_DoesNotCrashAndReturnsUnsignedForRegularFile()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "test_unsigned_" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            File.WriteAllBytes(tempFile, new byte[] { 0x4D, 0x5A, 0x90, 0x00 }); // Dummy MZ header
            var result = AppUpdateService.VerifyAuthenticodeSignature(tempFile);

            Assert.False(result.IsSigned);
            Assert.False(result.IsTampered);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }
}
