using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Storage;
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

        var s3ErrorFile = Path.Combine(_testDir, "game.zip");
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
}
