using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Extractor;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;
using Reepax.Services.Verification;
using Xunit;

namespace Reepax.Tests;

public class ChecksumVerificationTests : IDisposable
{
    private readonly string _testDir;

    public ChecksumVerificationTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "Reepax_ChecksumTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void CalculateHashes_CalculatesAccurateSha256_Md5_Crc32()
    {
        var service = ChecksumVerificationService.Instance;
        var filePath = Path.Combine(_testDir, "test_hash.txt");
        var content = "Reepax High-Performance Hash Verification";
        File.WriteAllBytes(filePath, new UTF8Encoding(false).GetBytes(content));

        // Expected hashes
        using var sha256 = SHA256.Create();
        var expectedSha256 = Convert.ToHexString(sha256.ComputeHash(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

        using var md5 = MD5.Create();
        var expectedMd5 = Convert.ToHexString(md5.ComputeHash(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

        var calculatedSha256 = service.CalculateSha256(filePath);
        var calculatedMd5 = service.CalculateMd5(filePath);
        var calculatedCrc32 = service.CalculateCrc32Hex(filePath);

        Assert.Equal(expectedSha256, calculatedSha256);
        Assert.Equal(expectedMd5, calculatedMd5);
        Assert.Equal(8, calculatedCrc32.Length);
    }

    [Fact]
    public void IsHtmlErrorPage_DetectsHtmlAndCloudflareErrorPages()
    {
        var service = ChecksumVerificationService.Instance;

        var htmlFile = Path.Combine(_testDir, "error.html");
        File.WriteAllText(htmlFile, "<!DOCTYPE html><html><head><title>404 Not Found</title></head><body>Error</body></html>");

        var cloudflareFile = Path.Combine(_testDir, "cloudflare.html");
        File.WriteAllText(cloudflareFile, "<html><head><title>Attention Required! | Cloudflare</title></head><body>error code: 1020</body></html>");

        var binaryFile = Path.Combine(_testDir, "data.bin");
        File.WriteAllBytes(binaryFile, new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00 });

        Assert.True(service.IsHtmlErrorPage(htmlFile));
        Assert.True(service.IsHtmlErrorPage(cloudflareFile));
        Assert.False(service.IsHtmlErrorPage(binaryFile));
    }

    [Fact]
    public void IsHtmlErrorPage_AllowsValidHtmlAndXmlFilesWithoutError()
    {
        var service = ChecksumVerificationService.Instance;

        var validHtmlFile = Path.Combine(_testDir, "valid_page.html");
        File.WriteAllText(validHtmlFile, "<!DOCTYPE html><html><head><title>Welcome to MyApp</title></head><body>Content</body></html>");

        var validXmlFile = Path.Combine(_testDir, "valid_data.xml");
        File.WriteAllText(validXmlFile, "<?xml version=\"1.0\" encoding=\"utf-8\"?><root><item>Hello</item></root>");

        var validSvgFile = Path.Combine(_testDir, "valid_icon.svg");
        File.WriteAllText(validSvgFile, "<?xml version=\"1.0\"?><svg width=\"100\" height=\"100\"><circle cx=\"50\" cy=\"50\" r=\"40\"/></svg>");

        Assert.False(service.IsHtmlErrorPage(validHtmlFile));
        Assert.False(service.IsHtmlErrorPage(validXmlFile));
        Assert.False(service.IsHtmlErrorPage(validSvgFile));
    }

    [Fact]
    public void IsHtmlErrorPage_DetectsXmlErrorResponse_OnArchiveOrMedia()
    {
        var service = ChecksumVerificationService.Instance;

        var s3ErrorFile = Path.Combine(_testDir, "archive.zip");
        File.WriteAllText(s3ErrorFile, "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>AccessDenied</Code><Message>Access Denied</Message></Error>");

        Assert.True(service.IsHtmlErrorPage(s3ErrorFile));
    }

    [Fact]
    public async Task VerifyFileIntegrity_WhenExpectedChecksumMatches_ReturnsValid()
    {
        var service = ChecksumVerificationService.Instance;
        var filePath = Path.Combine(_testDir, "valid_part.bin");
        var data = new byte[1024];
        Random.Shared.NextBytes(data);
        File.WriteAllBytes(filePath, data);

        using var sha256 = SHA256.Create();
        var expectedSha256 = Convert.ToHexString(sha256.ComputeHash(data)).ToLowerInvariant();

        var item = new DownloadItem
        {
            FileName = "valid_part.bin",
            SaveFilePath = filePath,
            TotalBytes = data.Length,
            ExpectedChecksum = expectedSha256
        };

        var result = await service.VerifyFileIntegrityAsync(item);

        Assert.True(result.IsValid);
        Assert.Equal(expectedSha256, result.CalculatedChecksum);
    }

    [Fact]
    public async Task VerifyFileIntegrity_WhenChecksumMismatches_ReturnsInvalid()
    {
        var service = ChecksumVerificationService.Instance;
        var filePath = Path.Combine(_testDir, "corrupted_part.bin");
        File.WriteAllBytes(filePath, new byte[] { 1, 2, 3, 4, 5 });

        var item = new DownloadItem
        {
            FileName = "corrupted_part.bin",
            SaveFilePath = filePath,
            TotalBytes = 5,
            ExpectedChecksum = "0000000000000000000000000000000000000000000000000000000000000000" // Wrong SHA256
        };

        var result = await service.VerifyFileIntegrityAsync(item);

        Assert.False(result.IsValid);
        Assert.True(result.ErrorMessage?.Contains("Prüfsummen-Abweichung") == true || result.ErrorMessage?.Contains("Checksum mismatch") == true);
    }

    [Fact]
    public async Task VerifyFileIntegrity_WhenFileSizeMismatchesTotalBytes_ReturnsInvalid()
    {
        var service = ChecksumVerificationService.Instance;
        var filePath = Path.Combine(_testDir, "truncated.bin");
        File.WriteAllBytes(filePath, new byte[500]); // 500 bytes on disk

        var item = new DownloadItem
        {
            FileName = "truncated.bin",
            SaveFilePath = filePath,
            TotalBytes = 1000 // Server reported 1000 bytes
        };

        var result = await service.VerifyFileIntegrityAsync(item);

        Assert.False(result.IsValid);
        Assert.True(result.ErrorMessage?.Contains("Dateigrößen-Diskrepanz") == true || result.ErrorMessage?.Contains("File size mismatch") == true);
    }

    [Fact]
    public async Task VerifyFileIntegrity_WhenArchiveIsCorrupted_ReturnsInvalid()
    {
        var service = ChecksumVerificationService.Instance;
        var filePath = Path.Combine(_testDir, "corrupted_archive.zip");
        // Write corrupt bytes that start with zip magic bytes but have broken central directory
        var corruptBytes = new byte[256];
        corruptBytes[0] = 0x50;
        corruptBytes[1] = 0x4B;
        corruptBytes[2] = 0x03;
        corruptBytes[3] = 0x04;
        File.WriteAllBytes(filePath, corruptBytes);

        var item = new DownloadItem
        {
            FileName = "corrupted_archive.zip",
            SaveFilePath = filePath,
            TotalBytes = 256
        };

        var result = await service.VerifyFileIntegrityAsync(item);

        Assert.False(result.IsValid);
        Assert.True(result.ErrorMessage?.Contains("Archiv-Integritätsprüfung") == true || result.ErrorMessage?.Contains("Archive integrity check") == true);
    }

    [Fact]
    public async Task VerifyFileIntegrity_WhenZipArchiveIsValid_ReturnsValid()
    {
        var service = ChecksumVerificationService.Instance;
        var filePath = Path.Combine(_testDir, "valid_archive.zip");

        using (var zip = ZipFile.Open(filePath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("inner.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.WriteLine("Valid file content");
        }

        var fileLength = new FileInfo(filePath).Length;

        var item = new DownloadItem
        {
            FileName = "valid_archive.zip",
            SaveFilePath = filePath,
            TotalBytes = fileLength
        };

        var result = await service.VerifyFileIntegrityAsync(item);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(true, true, 0, false)]
    [InlineData(true, false, 0, false)]
    [InlineData(true, false, 1, false)]
    [InlineData(false, false, 0, false)]
    [InlineData(true, true, 0, true)]
    [InlineData(true, false, 1, true)]
    public async Task DownloadCompletion_BlocksExtractionUntilAllIntegrityChecksFinish(
        bool enableVerification, bool checksumMatches, int maxRetries, bool browserNative)
    {
        var settings = SettingsService.Instance.Settings;
        var previousVerification = settings.EnableChecksumVerification;
        var previousRetries = settings.MaxAutoRetryOnCorruption;
        using var releaseVerification = new ManualResetEventSlim(false);
        var verificationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completionApplied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var extractionFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var package = CreateArchivePackage();
        var archive = package.Items[0];
        archive.IsVerifyingIntegrity = true;
        var data = Encoding.UTF8.GetBytes("Integrity verification content");
        var filePath = Path.Combine(_testDir, "data.bin");
        File.WriteAllBytes(filePath, data);
        var item = new DownloadItem
        {
            FileName = "data.bin",
            SaveFilePath = filePath,
            TotalBytes = data.Length,
            DownloadedBytes = data.Length,
            Status = DownloadStatus.Completed,
            ExpectedChecksum = checksumMatches ? Convert.ToHexString(SHA256.HashData(data)) : new string('0', 64)
        };
        package.Items.Add(item);
        package.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DownloadPackage.IsExtracting) && !package.IsExtracting && package.IsExtracted)
                extractionFinished.TrySetResult(true);
        };
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DownloadItem.IsVerifyingIntegrity) && !item.IsVerifyingIntegrity)
                completionApplied.TrySetResult(true);
            if (e.PropertyName == nameof(DownloadItem.StatusMessage) && item.StatusMessage == Loc.Get("Status_VerifyingChecksum"))
            {
                verificationStarted.TrySetResult(true);
                releaseVerification.Wait();
            }
        };
        var queue = new QueueManager(subscribeToDownloadEvents: false);
        queue.Packages.Clear();
        queue.Packages.Add(package);
        Task completion = Task.CompletedTask;
        bool shouldExtract = !enableVerification || checksumMatches;

        try
        {
            settings.EnableChecksumVerification = enableVerification;
            settings.MaxAutoRetryOnCorruption = maxRetries;
            if (browserNative)
            {
                item.Status = DownloadStatus.InBrowser;
                var windows = (Dictionary<int, DownloadItem>)typeof(QueueManager).GetField("_windowItems",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(queue)!;
                windows.Add(1, item);
                completion = completionApplied.Task;
                queue.OnBrowserNativeDownloadCompleted(1, true, null, filePath);
            }
            else
            {
                completion = queue.HandleDownloadCompleted(item);
            }
            if (enableVerification)
            {
                await verificationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(item.IsVerifyingIntegrity);
                Assert.False(queue.IsItemActive(item));
                Assert.False(package.AreDownloadsCompleted);
                Assert.False(package.CheckIsFullyCompleted());
                Assert.False(ArchiveExtractionService.Instance.CanExtractPackage(package));
                Assert.True(queue.HasActiveDownloads());
                Assert.True(PostDownloadActionService.CheckHasActiveWork(new[] { package }));
                typeof(QueueManager).GetMethod("CheckPendingExtractions",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(queue, null);
                Assert.False(ArchiveExtractionService.Instance.IsPackageExtracting(package.Id));
                Assert.False(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package, force: true, forceReextract: true));
                Assert.False(File.Exists(Path.Combine(_testDir, "payload.txt")));
                var persistence = new DownloadPersistenceService(Path.Combine(_testDir, "downloads.json"));
                persistence.SaveDownloads(new[] { package });
                var restored = Assert.Single(persistence.LoadDownloads());
                Assert.All(restored.Items, saved => Assert.Equal(DownloadStatus.Paused, saved.Status));
            }
            releaseVerification.Set();
            await completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(item.IsVerifyingIntegrity);
            Assert.False(ArchiveExtractionService.Instance.CanExtractPackage(package));
            Assert.False(File.Exists(Path.Combine(_testDir, "payload.txt")));
            await queue.HandleDownloadCompleted(archive).WaitAsync(TimeSpan.FromSeconds(10));

            if (shouldExtract)
            {
                await extractionFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(DownloadStatus.Completed, item.Status);
                Assert.Equal(enableVerification ? Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() : null, item.CalculatedChecksum);
                Assert.Equal("Archive content", File.ReadAllText(Path.Combine(_testDir, "payload.txt")));
            }
            else
            {
                Assert.Equal(maxRetries > 0 ? DownloadStatus.Queued : DownloadStatus.Failed, item.Status);
                Assert.Equal(maxRetries > 0 ? 1 : 0, item.RetryCount);
                Assert.False(string.IsNullOrWhiteSpace(item.LastVerificationError));
                Assert.Equal(maxRetries == 0, File.Exists(filePath));
                Assert.False(File.Exists(Path.Combine(_testDir, "payload.txt")));
            }
        }
        finally
        {
            releaseVerification.Set();
            await completion.WaitAsync(TimeSpan.FromSeconds(10));
            if (ArchiveExtractionService.Instance.IsPackageExtracting(package.Id))
                await extractionFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            settings.EnableChecksumVerification = previousVerification;
            settings.MaxAutoRetryOnCorruption = previousRetries;
        }
    }

    [Fact]
    public async Task Extraction_IgnoresDisabledItemsWithPendingIntegrityChecks()
    {
        var package = CreateArchivePackage();
        package.Items.Add(new DownloadItem { IsEnabled = false, IsVerifyingIntegrity = true });

        Assert.True(package.AreDownloadsCompleted);
        Assert.True(ArchiveExtractionService.Instance.CanExtractPackage(package));
        Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package, force: true));
        Assert.True(package.IsFullyCompleted);
    }

    [Fact]
    public async Task Extraction_WhenCompletionStatusArrivesAfterVerificationFailure_RemainsBlocked()
    {
        var package = CreateArchivePackage();
        var item = package.Items[0];
        item.IsVerifyingIntegrity = true;
        item.Status = DownloadStatus.Failed;
        item.IsVerifyingIntegrity = false;
        item.Status = DownloadStatus.Completed;

        Assert.False(item.IsVerifyingIntegrity);
        Assert.False(package.AreDownloadsCompleted);
        Assert.False(ArchiveExtractionService.Instance.CanExtractPackage(package));
        Assert.False(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package, force: true));
    }

    [Fact]
    public async Task DownloadCompletion_WhenVerificationUpdateThrows_BlocksExtractionAndReleasesGuard()
    {
        var settings = SettingsService.Instance.Settings;
        var previousVerification = settings.EnableChecksumVerification;
        var package = CreateArchivePackage();
        var item = package.Items[0];
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DownloadItem.StatusMessage) && item.StatusMessage == Loc.Get("Status_VerifyingChecksum"))
                throw new InvalidOperationException("Verification update failed");
        };
        var queue = new QueueManager(subscribeToDownloadEvents: false);
        try
        {
            settings.EnableChecksumVerification = true;
            await queue.HandleDownloadCompleted(item).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(DownloadStatus.Failed, item.Status);
            Assert.False(item.IsVerifyingIntegrity);
            Assert.False(ArchiveExtractionService.Instance.CanExtractPackage(package));
            Assert.Equal("Verification update failed", item.LastVerificationError);
        }
        finally
        {
            settings.EnableChecksumVerification = previousVerification;
        }
    }

    private DownloadPackage CreateArchivePackage()
    {
        var archivePath = Path.Combine(_testDir, "archive.zip");
        using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("payload.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("Archive content");
        }
        var package = new DownloadPackage
        {
            Name = "Integrity package",
            IsCustomName = true,
            SaveDirectory = _testDir,
            AutoExtractArchives = true,
            AutoPar2Repair = false
        };
        package.Items.Add(new DownloadItem
        {
            FileName = "archive.zip",
            SaveFilePath = archivePath,
            Status = DownloadStatus.Completed
        });
        return package;
    }

    [Fact]
    public void DisplayChecksum_PrioritizesCalculatedOverExpected()
    {
        var item = new DownloadItem();
        Assert.Null(item.DisplayChecksum);

        item.ExpectedChecksum = "expected_hash_123";
        Assert.Equal("expected_hash_123", item.DisplayChecksum);

        item.CalculatedChecksum = "calculated_sha256_456";
        Assert.Equal("calculated_sha256_456", item.DisplayChecksum);
    }

    [Fact]
    public void DownloadPersistenceService_PersistsAndRestores_CalculatedChecksum()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_ChecksumPersistTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var downloadsFile = Path.Combine(testDir, "downloads.json");

        try
        {
            var persistenceService = new DownloadPersistenceService(downloadsFile);

            var item = new DownloadItem
            {
                Id = Guid.NewGuid(),
                FileName = "test_checksum_item.dat",
                TotalBytes = 1024,
                CalculatedChecksum = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                ExpectedChecksum = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
            };

            var package = new DownloadPackage
            {
                Id = Guid.NewGuid(),
                Name = "Checksum Package",
                SaveDirectory = testDir
            };
            package.Items.Add(item);

            persistenceService.SaveDownloads(new List<DownloadPackage> { package });

            var loaded = persistenceService.LoadDownloads();
            Assert.NotEmpty(loaded);
            var loadedItem = loaded[0].Items[0];
            Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", loadedItem.CalculatedChecksum);
            Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", loadedItem.DisplayChecksum);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }
}
