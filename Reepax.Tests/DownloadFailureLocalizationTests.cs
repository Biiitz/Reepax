using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.Services.Verification;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

public sealed class DownloadFailureLocalizationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("DownloadFailure_").FullName;
    private readonly string _language = LocalizationService.Instance.CurrentLanguage;

    [Theory]
    [InlineData("en", "Waiting...")]
    [InlineData("de", "Warten...")]
    public void BrowserWaiting_UsesCompactLocalizedStatusForPackagesAndItems(string language, string expected)
    {
        LocalizationService.Instance.CurrentLanguage = language;
        var package = new DownloadPackage { Name = "Example package", SaveDirectory = _root };
        package.AddItems([new DownloadItem { FileName = "payload.bin", Status = DownloadStatus.InBrowser }]);
        package.RecalculateAggregates(force: true);
        Assert.Equal(DownloadStatus.SolvingCaptcha, package.Status);
        Assert.Equal(expected, package.StatusMessage);
        Assert.Equal(expected, Loc.Format("Status_BrowserWindowWaiting", 1));
    }

    [Theory]
    [InlineData("html", DownloadFailureKind.HtmlResponse)]
    [InlineData("hash", DownloadFailureKind.ChecksumMismatch)]
    [InlineData("size", DownloadFailureKind.SizeMismatch)]
    [InlineData("archive", DownloadFailureKind.InvalidArchive)]
    public async Task Completion_RejectedPayloadReportsActualFailureWithoutSuccessfulCompletion(string scenario, DownloadFailureKind expected)
    {
        LocalizationService.Instance.CurrentLanguage = "en";
        var settings = SettingsService.Instance.Settings;
        bool verify = settings.EnableChecksumVerification;
        int retries = settings.MaxAutoRetryOnCorruption;
        string path = Path.Combine(_root, scenario == "archive" ? "payload.zip" : "payload.bin");
        byte[] bytes = scenario == "html" ? Encoding.UTF8.GetBytes("<!doctype html><html><body>Request rejected</body></html>")
            : Enumerable.Range(0, 91).Select(index => (byte)index).ToArray();
        await File.WriteAllBytesAsync(path, bytes);
        var item = new DownloadItem
        {
            FileName = Path.GetFileName(path), SaveFilePath = path, TotalBytes = bytes.Length,
            DownloadedBytes = bytes.Length, Status = DownloadStatus.Downloading,
            ExpectedChecksum = scenario == "hash" ? new string('0', 64) : null
        };
        if (scenario == "size") item.TotalBytes++;
        var package = new DownloadPackage { Name = "Example package", SaveDirectory = _root };
        package.AddItems([item]);
        var queue = new QueueManager(subscribeToDownloadEvents: false);
        queue.Packages.Add(package);
        item.IsVerifyingIntegrity = true;
        item.Status = DownloadStatus.Completed;
        try
        {
            settings.EnableChecksumVerification = true;
            settings.MaxAutoRetryOnCorruption = 0;
            await queue.HandleDownloadCompleted(item).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(DownloadStatus.Failed, item.Status);
            Assert.Equal(expected, item.FailureDetails?.Kind);
            Assert.Equal(item.FailureDetails?.Message, item.StatusMessage);
            Assert.False(item.IsVerifyingIntegrity);
            Assert.False(package.HasCompletedNotified);
            Assert.False(package.AreDownloadsCompleted);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            if (scenario != "hash") Assert.DoesNotContain("checksum", item.StatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(item.RejectedFiles);
        }
        finally
        {
            settings.EnableChecksumVerification = verify;
            settings.MaxAutoRetryOnCorruption = retries;
        }
    }

    [Fact]
    public async Task LockedFile_ReportsFileAccessWithoutRevealingThePath()
    {
        LocalizationService.Instance.CurrentLanguage = "en";
        string path = Path.Combine(_root, "locked.bin");
        await File.WriteAllBytesAsync(path, [12, 29, 41]);
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = await ChecksumVerificationService.Instance.VerifyFileIntegrityAsync(new DownloadItem
        { FileName = "locked.bin", SaveFilePath = path });
        Assert.False(result.IsValid);
        Assert.Equal(DownloadFailureKind.FileAccess, result.FailureDetails?.Kind);
        Assert.DoesNotContain(path, result.ErrorMessage);
    }

    [Fact]
    public void RetryAndTargetFailures_PreserveTheirSpecificExplanationsAcrossLanguages()
    {
        var vm = new MainViewModel();
        var package = new DownloadPackage { Name = "Example package", SaveDirectory = _root };
        var item = new DownloadItem { FileName = "payload.bin", Status = DownloadStatus.Failed };
        package.AddItems([item]);
        vm.Packages.Add(package);
        try
        {
            foreach (var (error, key) in new (Exception, string)[]
            {
                (new DownloadDestinationExistsException(), "Error_DownloadDestinationExists"),
                (new DownloadRetryBlockedException(), "Status_IntegrityRetryBlocked")
            })
            {
                LocalizationService.Instance.CurrentLanguage = "en";
                item.SetDownloadFailure(DownloadFailure.FromException(error));
                Assert.Equal(Loc.Get(key), item.StatusMessage);
                LocalizationService.Instance.CurrentLanguage = "de";
                Assert.Equal(Loc.Get(key), item.StatusMessage);
            }
        }
        finally { vm.Packages.Remove(package); }
    }

    [Fact]
    public void BackgroundFailure_LocalizesIndependentlyOfExceptionLanguageAndRefreshesWithTheUi()
    {
        var vm = new MainViewModel();
        var package = new DownloadPackage { Name = "Example package", SaveDirectory = _root };
        var item = new DownloadItem { FileName = "payload.bin" };
        package.AddItems([item]);
        vm.Packages.Add(package);
        var queue = new QueueManager(subscribeToDownloadEvents: false);
        try
        {
            LocalizationService.Instance.CurrentLanguage = "en";
            const string raw = "Zugriff verweigert; private-test-token";
            queue.OnBackgroundDownloadFailed(item, new HttpRequestException(raw, null, HttpStatusCode.Forbidden));
            Assert.Equal(DownloadStatus.Failed, item.Status);
            Assert.Equal(DownloadFailureKind.Http, item.FailureDetails?.Kind);
            Assert.Contains("HTTP 403", item.StatusMessage);
            Assert.DoesNotContain(raw, item.StatusMessage);
            Assert.DoesNotContain("private-test-token", item.ErrorMessage);
            string english = item.StatusMessage;
            LocalizationService.Instance.CurrentLanguage = "de";
            Assert.NotEqual(english, item.StatusMessage);
            Assert.Equal(Loc.Format("DownloadFailure_HttpStatus", 403), item.StatusMessage);
            LocalizationService.Instance.CurrentLanguage = "en";
            Assert.Equal(english, item.StatusMessage);
        }
        finally { vm.Packages.Remove(package); }
    }

    [Fact]
    public void Persistence_PreservesFailureCategoryAndLocalizesRestoredDetails()
    {
        LocalizationService.Instance.CurrentLanguage = "de";
        var package = new DownloadPackage { Name = "Example package", SaveDirectory = _root };
        var item = new DownloadItem { FileName = "payload.bin", Status = DownloadStatus.Failed };
        item.SetDownloadFailure(new DownloadFailure { Kind = DownloadFailureKind.HtmlResponse });
        package.AddItems([item]);
        string path = Path.Combine(_root, "downloads.json");
        using var persistence = new DownloadPersistenceService(path);
        persistence.SaveDownloads([package], sync: true);
        LocalizationService.Instance.CurrentLanguage = "en";
        var restored = Assert.Single(Assert.Single(persistence.LoadDownloads()).Items);
        Assert.Equal(DownloadStatus.Paused, restored.Status);
        Assert.Equal(DownloadFailureKind.HtmlResponse, restored.FailureDetails?.Kind);
        Assert.Equal(Loc.Get("Validation_HtmlErrorPageDetected"), restored.ErrorMessage);
        Assert.Equal(Loc.Get("Status_Paused"), restored.StatusMessage);
    }

    [Fact]
    public void LegacyFailure_UsesGenericTranslationRatherThanPersistedRawText()
    {
        LocalizationService.Instance.CurrentLanguage = "en";
        var item = new DownloadItem
        { FileName = "payload.bin", Status = DownloadStatus.Failed, StatusMessage = "Alter Fehler: private-test-token", ErrorMessage = "Zugriff verweigert" };
        var package = new DownloadPackage { Name = "Example package", SaveDirectory = _root };
        package.AddItems([item]);
        using var persistence = new DownloadPersistenceService(Path.Combine(_root, "legacy.json"));
        persistence.SaveDownloads([package], sync: true);
        var restored = Assert.Single(Assert.Single(persistence.LoadDownloads()).Items);
        Assert.Equal(DownloadFailureKind.Unknown, restored.FailureDetails?.Kind);
        Assert.Equal(Loc.Get("DownloadFailure_Unknown"), restored.ErrorMessage);
        Assert.Equal(Loc.Get("Status_Paused"), restored.StatusMessage);
        Assert.DoesNotContain("private-test-token", restored.StatusMessage);
        Assert.DoesNotContain("Zugriff verweigert", restored.ErrorMessage);
    }

    public void Dispose()
    {
        LocalizationService.Instance.CurrentLanguage = _language;
        Directory.Delete(_root, recursive: true);
    }
}
