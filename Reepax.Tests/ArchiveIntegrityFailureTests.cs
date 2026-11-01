using System;
using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Services.Extractor;
using Xunit;

namespace Reepax.Tests;

public sealed class ArchiveIntegrityFailureTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ArchiveIntegrity_").FullName;

    [Theory]
    [InlineData("changed-byte")]
    [InlineData("truncated")]
    [InlineData("wrong-length")]
    [InlineData("duplicate")]
    [InlineData("case-collision")]
    [InlineData("traversal")]
    public async Task InvalidArchive_NeverPublishesOrReportsComplete(string fault)
    {
        string archivePath = Path.Combine(_root, "archive.zip");
        string target = Path.Combine(_root, "output");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "payload.txt"), "original");
        byte[] payload = Encoding.UTF8.GetBytes("sample bytes for exact archive verification");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            using (var output = archive.CreateEntry("payload.txt", CompressionLevel.NoCompression).Open()) output.Write(payload);
            if (fault is "duplicate" or "case-collision" or "traversal")
            {
                string name = fault == "duplicate" ? "payload.txt" : fault == "case-collision" ? "PAYLOAD.txt" : "../outside.txt";
                using var output = archive.CreateEntry(name).Open();
                output.Write(payload);
            }
        }
        byte[] bytes = File.ReadAllBytes(archivePath);
        if (fault == "changed-byte") bytes[Array.IndexOf(bytes, payload[0], 40)] ^= 1;
        if (fault == "truncated") Array.Resize(ref bytes, bytes.Length - 12);
        if (fault == "wrong-length")
        {
            int central = Enumerable.Range(0, bytes.Length - 4).First(index => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index, 4)) == 0x02014b50);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(central + 24, 4), (uint)payload.Length + 1);
        }
        File.WriteAllBytes(archivePath, bytes);
        double progress = 0;
        bool result = await ArchiveExtractionService.Instance.ExtractArchiveAsync(archivePath, target, progressCallback: value => progress = value);
        Assert.False(result);
        Assert.True(progress < 100);
        Assert.Equal("original", File.ReadAllText(Path.Combine(target, "payload.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
        Assert.True(File.Exists(archivePath));
        Assert.Empty(Directory.GetDirectories(target, ".archive-work-*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("tar")]
    public async Task ValidArchive_PreservesEmptyFilesAndDirectories(string format)
    {
        string archivePath = Path.Combine(_root, "archive." + format);
        string target = Path.Combine(_root, "output");
        if (format == "zip")
        {
            using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
            archive.CreateEntry("empty/directory/");
            archive.CreateEntry("empty-file.txt");
            using var writer = new StreamWriter(archive.CreateEntry("payload.txt").Open());
            writer.Write("exact content");
        }
        else
        {
            using var archive = new TarWriter(File.Create(archivePath));
            archive.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "."));
            archive.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "./empty/directory/"));
            archive.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "./empty-file.txt") { DataStream = new MemoryStream() });
            archive.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "./payload.txt") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("exact content")) });
        }
        Assert.True(await ArchiveExtractionService.Instance.ExtractArchiveAsync(archivePath, target));
        Assert.True(Directory.Exists(Path.Combine(target, "empty", "directory")));
        Assert.Equal(0, new FileInfo(Path.Combine(target, "empty-file.txt")).Length);
        Assert.Equal("exact content", File.ReadAllText(Path.Combine(target, "payload.txt")));
        Assert.Empty(Directory.GetDirectories(target, ".archive-work-*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("short")]
    [InlineData("extra")]
    [InlineData("cancel")]
    public void ExtractedFileReadBack_DetectsWriteDamageOrCancellation(string fault)
    {
        string path = Path.Combine(_root, "written.bin");
        using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var output = new ArchiveEntryOutputStream(file, _ => { });
        output.Write(new byte[] { 1, 2, 3, 4 });
        if (fault == "bytes") { file.Position = 1; file.WriteByte(9); }
        if (fault == "short") file.SetLength(3);
        if (fault == "extra") file.WriteByte(9);
        file.Flush(flushToDisk: true);
        using var cancellation = new CancellationTokenSource();
        if (fault == "cancel") cancellation.Cancel();
        if (fault == "cancel")
            Assert.ThrowsAny<OperationCanceledException>(() => output.VerifyWrittenBytes(file.SafeFileHandle, cancellation.Token));
        else
            Assert.Throws<InvalidDataException>(() => output.VerifyWrittenBytes(file.SafeFileHandle, cancellation.Token));
    }

    [Fact]
    public void ArchiveDestination_SupportsAVolumeRootWithoutWritingToIt()
    {
        string volume = Path.GetPathRoot(_root)!;
        Assert.Equal(Path.Combine(volume, "payload.bin"), ArchiveExtractionService.GetSafeArchiveDestination(volume, "payload.bin"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
