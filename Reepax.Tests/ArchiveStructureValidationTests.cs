using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Extractor;
using Reepax.Services.Storage;
using Reepax.Services.Verification;
using Xunit;

namespace Reepax.Tests;

public sealed class ArchiveStructureValidationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ArchiveStructure_").FullName;

    [Theory]
    [InlineData("sample.tar")]
    [InlineData("sample.part1.rar")]
    public async Task CorruptedSecondTarHeader_IsRejectedByStructureAndCompletionVerification(string fileName)
    {
        string path = CreateDamagedTar(fileName);
        Assert.Throws<InvalidDataException>(() =>
        {
            using var reader = new TarReader(File.OpenRead(path));
            while (reader.GetNextEntry() != null) { }
        });
        var verifier = new ChecksumVerificationService();
        var result = await verifier.VerifyFileIntegrityAsync(new DownloadItem
        {
            FileName = fileName, SaveFilePath = path, TotalBytes = new FileInfo(path).Length
        });
        Assert.False(verifier.ValidateArchiveStructure(path, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.False(result.IsValid);
        Assert.Null(result.CalculatedChecksum);
    }

    [Theory]
    [InlineData(TarEntryFormat.V7)]
    [InlineData(TarEntryFormat.Ustar)]
    [InlineData(TarEntryFormat.Pax)]
    [InlineData(TarEntryFormat.Gnu)]
    public async Task ValidTarFormats_RemainAccepted(TarEntryFormat format)
    {
        string path = CreateTar(format);
        var verifier = new ChecksumVerificationService();
        Assert.True(verifier.ValidateArchiveStructure(path, out var error), error);
        var result = await verifier.VerifyFileIntegrityAsync(new DownloadItem { FileName = "sample.tar", SaveFilePath = path });
        Assert.True(result.IsValid, result.ErrorMessage);
    }

    [Fact]
    public async Task DamagedTarCompletion_DoesNotMarkCompletedOrStartExtraction()
    {
        var settings = SettingsService.Instance.Settings;
        bool previousVerification = settings.EnableChecksumVerification;
        int previousRetries = settings.MaxAutoRetryOnCorruption;
        var queue = new QueueManager(subscribeToDownloadEvents: false);
        try
        {
            settings.EnableChecksumVerification = true;
            settings.MaxAutoRetryOnCorruption = 0;
            string path = CreateDamagedTar("sample.tar");
            var package = new DownloadPackage
            {
                Name = "Sample", IsCustomName = true, SaveDirectory = _root,
                AutoExtractArchives = true, AutoPar2Repair = false
            };
            var item = new DownloadItem
            {
                FileName = "sample.tar", SaveFilePath = path, PackageId = package.Id,
                Status = DownloadStatus.Completed, TotalBytes = new FileInfo(path).Length
            };
            package.Items.Add(item);
            queue.Packages.Add(package);
            await queue.HandleDownloadCompleted(item).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(DownloadStatus.Failed, item.Status);
            Assert.False(item.IsVerifyingIntegrity);
            Assert.False(string.IsNullOrWhiteSpace(item.LastVerificationError));
            Assert.Null(item.CalculatedChecksum);
            Assert.False(ArchiveExtractionService.Instance.CanExtractPackage(package));
            Assert.False(package.IsExtracted);
            Assert.False(File.Exists(Path.Combine(_root, "first.txt")));
            Assert.True(File.Exists(path));
        }
        finally
        {
            queue.StopQueue();
            settings.EnableChecksumVerification = previousVerification;
            settings.MaxAutoRetryOnCorruption = previousRetries;
        }
    }

    [Fact]
    public async Task ExpectedChecksum_StillVerifiesExactBytesIndependentlyOfArchiveContents()
    {
        string path = CreateDamagedTar("sample.tar");
        string expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        var result = await new ChecksumVerificationService().VerifyFileIntegrityAsync(new DownloadItem
        {
            FileName = "sample.tar", SaveFilePath = path, ExpectedChecksum = expected
        });
        Assert.True(result.IsValid, result.ErrorMessage);
        Assert.Equal(expected, result.CalculatedChecksum);
    }

    [Fact]
    public void DamagedMultipartRarHeader_IsRejected()
    {
        string path = CopyFixture("DecoderArchives", "plain.part1.rar", false);
        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1a, 0x07, 0x01, 0x00 }, bytes[..8]);
        bytes[8] ^= 1;
        File.WriteAllBytes(path, bytes);
        Assert.False(new ChecksumVerificationService().ValidateArchiveStructure(path, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void EmptyZipWithComment_IsAValidArchive()
    {
        string path = Path.Combine(_root, "empty.zip");
        using (ZipFile.Open(path, ZipArchiveMode.Create)) { }
        byte[] bytes = File.ReadAllBytes(path);
        var comment = Encoding.ASCII.GetBytes(new string('x', 256));
        bytes[20] = 0;
        bytes[21] = 1;
        File.WriteAllBytes(path, bytes.Concat(comment).ToArray());
        using (var independent = ZipFile.OpenRead(path)) Assert.Empty(independent.Entries);
        Assert.True(new ChecksumVerificationService().ValidateArchiveStructure(path, out var error), error);
    }

    [Theory]
    [InlineData("Archives", "headers.rar", true)]
    [InlineData("Archives", "headers.7z", true)]
    [InlineData("Archives", "content.7z", true)]
    [InlineData("Archives", "volumes.part1.rar", true)]
    [InlineData("Archives", "volumes.part1.rar", false)]
    [InlineData("DecoderArchives", "plain.part1.rar", false)]
    [InlineData("DecoderArchives", "plain.part1.rar", true)]
    [InlineData("DecoderArchives", "compressed.part1.rar", false)]
    [InlineData("DecoderArchives", "compressed.part1.rar", true)]
    [InlineData("DecoderArchives", "solid.part1.rar", false)]
    [InlineData("DecoderArchives", "solid.part1.rar", true)]
    public async Task ValidEncryptedOrIncompleteArchive_RemainsDownloadable(string group, string name, bool allParts)
    {
        string path = CopyFixture(group, name, allParts);
        var verifier = new ChecksumVerificationService();
        Assert.True(verifier.ValidateArchiveStructure(path, out var error), error);
        var result = await verifier.VerifyFileIntegrityAsync(new DownloadItem
        {
            FileName = name, SaveFilePath = path, TotalBytes = new FileInfo(path).Length
        });
        Assert.True(result.IsValid, result.ErrorMessage);
        Assert.NotNull(result.CalculatedChecksum);
    }

    private string CreateDamagedTar(string fileName)
    {
        string path = CreateTar(fileName: fileName);
        byte[] bytes = File.ReadAllBytes(path);
        bytes[1024 + 148] = (byte)'9';
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string CreateTar(TarEntryFormat format = TarEntryFormat.Ustar, string fileName = "sample.tar")
    {
        string path = Path.Combine(_root, fileName);
        using var writer = new TarWriter(File.Create(path), format);
        foreach (var name in new[] { "first.txt", "second.txt" })
        {
            using var payload = new MemoryStream(Encoding.ASCII.GetBytes("sample"));
            TarEntry entry = format switch
            {
                TarEntryFormat.V7 => new V7TarEntry(TarEntryType.V7RegularFile, name),
                TarEntryFormat.Pax => new PaxTarEntry(TarEntryType.RegularFile, name),
                TarEntryFormat.Gnu => new GnuTarEntry(TarEntryType.RegularFile, name),
                _ => new UstarTarEntry(TarEntryType.RegularFile, name)
            };
            entry.DataStream = payload;
            writer.WriteEntry(entry);
        }
        return path;
    }

    private string CopyFixture(string group, string name, bool allParts)
    {
        var assembly = typeof(ArchiveStructureValidationTests).Assembly;
        string prefix = "Reepax.Tests.Fixtures." + group + ".";
        foreach (var resource in assembly.GetManifestResourceNames().Where(resource => resource.StartsWith(prefix, StringComparison.Ordinal)))
        {
            string file = resource[prefix.Length..];
            if (!allParts && file != name) continue;
            using var source = assembly.GetManifestResourceStream(resource)!;
            using var target = File.Create(Path.Combine(_root, file));
            source.CopyTo(target);
        }
        return Path.Combine(_root, name);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
