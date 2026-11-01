using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using Reepax.Services.Extractor;
using Reepax.Services.Storage;
using SharpCompress.Archives;
using SharpCompress.Readers;
using SharpCompress.Readers.Rar;
using Xunit;

namespace Reepax.Tests;

public partial class ArchivePasswordTests
{
    public static IEnumerable<object[]> DecoderReadCases()
    {
        foreach (var family in new[] { "volumes", "compressed", "solid", "plain" })
        foreach (var mode in Enumerable.Range(0, 4))
        foreach (var size in new[] { 1, 17, 8191 })
            yield return new object[] { family, mode, size };
    }

    [Theory]
    [MemberData(nameof(DecoderReadCases))]
    public async Task MultipartDecoder_ReturnsOriginalBytesAcrossReadBoundaries(string family, int mode, int readSize)
    {
        var path = CopyDecoderFixture(family);
        var options = new ReaderOptions { Password = " Fixture Key " };
        bool asynchronous = mode % 2 != 0;
        if (mode < 2)
        {
            using var archive = ArchiveFactory.OpenArchive(path, options);
            int count = 0;
            foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
            {
                await using var stream = asynchronous ? await entry.OpenEntryStreamAsync() : entry.OpenEntryStream();
                await AssertDecoderBytesAsync(stream, entry.Key!, family, readSize, asynchronous);
                count++;
            }
            Assert.Equal(family == "volumes" ? 1 : 2, count);
            return;
        }

        var inputs = Directory.GetFiles(_tempDirectory, family + ".part*.rar")
            .OrderBy(path => path, StringComparer.Ordinal).Select(File.OpenRead).ToArray();
        try
        {
            using var reader = RarReader.OpenReader(inputs.Select(input => new FragmentedArchiveStream(input)), options);
            var asyncReader = (IAsyncReader)reader;
            int count = 0;
            while (asynchronous ? await asyncReader.MoveToNextEntryAsync() : reader.MoveToNextEntry())
            {
                await using var stream = asynchronous ? await asyncReader.OpenEntryStreamAsync() : reader.OpenEntryStream();
                await AssertDecoderBytesAsync(stream, reader.Entry.Key!, family, readSize, asynchronous);
                count++;
            }
            Assert.Equal(family == "volumes" ? 1 : 2, count);
        }
        finally
        {
            foreach (var input in inputs) input.Dispose();
        }
    }

    [Theory]
    [InlineData("compressed")]
    [InlineData("solid")]
    public async Task EncryptedMultipartExtraction_PublishesOnlyOriginalBytes(string family)
    {
        var path = CopyDecoderFixture(family);
        SettingsService.Instance.ReplaceArchivePasswords(new[] { "WrongKey", " Fixture Key " });
        ArchiveExtractionService.PasswordPromptHandler = _ => throw new InvalidOperationException("Unexpected prompt.");
        var target = Path.Combine(_tempDirectory, "output");
        Assert.True(await ArchiveExtractionService.Instance.ExtractArchiveAsync(path, target));
        Assert.Equal(CreateDecoderPayload(24593), File.ReadAllBytes(Path.Combine(target, "payload.bin")));
        Assert.Equal(CreateDecoderPayload(18017), File.ReadAllBytes(Path.Combine(target, "companion.bin")));
        Assert.Equal(1, SettingsService.Instance.GetArchivePasswordEntries()[1].SuccessCount);
        Assert.Empty(Directory.GetDirectories(_tempDirectory, ".archive-work-*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("compressed", false, false)]
    [InlineData("compressed", true, false)]
    [InlineData("solid", false, false)]
    [InlineData("solid", true, false)]
    [InlineData("compressed", false, true)]
    [InlineData("solid", false, true)]
    public async Task DamagedEncryptedVolume_CannotPublishOrCountAsSuccess(string family, bool truncate, bool damageFinalPart)
    {
        var path = CopyDecoderFixture(family);
        var damaged = damageFinalPart
            ? Directory.GetFiles(_tempDirectory, family + ".part*.rar").OrderBy(path => path, StringComparer.Ordinal).Last()
            : Path.Combine(_tempDirectory, family + ".part2.rar");
        var bytes = File.ReadAllBytes(damaged);
        if (truncate) Array.Resize(ref bytes, bytes.Length / 2);
        else bytes[bytes.Length / 2] ^= 0x40;
        File.WriteAllBytes(damaged, bytes);
        SettingsService.Instance.ReplaceArchivePasswords(new[] { " Fixture Key " });
        ArchiveExtractionService.PasswordPromptHandler = _ => Task.FromResult<(string?, bool)>((null, false));
        var target = Path.Combine(_tempDirectory, "output");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "payload.bin"), "existing content");
        File.WriteAllText(Path.Combine(target, "companion.bin"), "existing companion");
        double maximumProgress = 0;
        Assert.False(await ArchiveExtractionService.Instance.ExtractArchiveAsync(path, target,
            progressCallback: value => maximumProgress = Math.Max(maximumProgress, value)));
        Assert.Equal("existing content", File.ReadAllText(Path.Combine(target, "payload.bin")));
        Assert.Equal("existing companion", File.ReadAllText(Path.Combine(target, "companion.bin")));
        Assert.Equal(0, Assert.Single(SettingsService.Instance.GetArchivePasswordEntries()).SuccessCount);
        Assert.True(maximumProgress < 100);
        Assert.True(File.Exists(path));
        Assert.Equal(bytes, File.ReadAllBytes(damaged));
        Assert.Empty(Directory.GetDirectories(_tempDirectory, ".archive-work-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task MultipartReader_CancellationIsObservedBeforeReading()
    {
        var path = CopyDecoderFixture("volumes");
        using var archive = ArchiveFactory.OpenArchive(path, new ReaderOptions { Password = " Fixture Key " });
        await using var stream = await Assert.Single(archive.Entries).OpenEntryStreamAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await stream.ReadAsync(new byte[17].AsMemory(), cancellation.Token));
    }

    private string CopyDecoderFixture(string family)
    {
        var assembly = typeof(ArchivePasswordTests).Assembly;
        string prefix = family == "volumes" ? "Reepax.Tests.Fixtures.Archives." : "Reepax.Tests.Fixtures.DecoderArchives.";
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix + family + ".part", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(resources);
        foreach (var name in resources)
        {
            using var source = assembly.GetManifestResourceStream(name)!;
            using var output = File.Create(Path.Combine(_tempDirectory, name[prefix.Length..]));
            source.CopyTo(output);
        }
        return Path.Combine(_tempDirectory, family + ".part1.rar");
    }

    private static async Task AssertDecoderBytesAsync(Stream input, string entryName, string family, int readSize, bool asynchronous)
    {
        using var output = new MemoryStream();
        var buffer = new byte[readSize + 4];
        while (true)
        {
            Assert.Equal(0, asynchronous ? await input.ReadAsync(Memory<byte>.Empty) : input.Read(Array.Empty<byte>(), 0, 0));
            Array.Fill(buffer, (byte)0x5A);
            int read = asynchronous
                ? await input.ReadAsync(buffer.AsMemory(2, readSize))
                : input.Read(buffer, 2, readSize);
            Assert.InRange(read, 0, readSize);
            Assert.Equal(0x5A, buffer[0]);
            Assert.Equal(0x5A, buffer[^1]);
            if (read == 0) break;
            output.Write(buffer, 2, read);
        }
        byte[] expected = family == "volumes"
            ? Enumerable.Range(0, 16384).Select(index => (byte)index).ToArray()
            : CreateDecoderPayload(entryName == "payload.bin" ? 24593 : 18017);
        Assert.Equal(expected, output.ToArray());
        Assert.Equal(0, asynchronous ? await input.ReadAsync(buffer.AsMemory()) : input.Read(buffer, 0, buffer.Length));
    }

    private static byte[] CreateDecoderPayload(int size)
    {
        var result = new byte[size];
        Span<byte> counter = stackalloc byte[4];
        for (int offset = 0; offset < size; offset += 32)
        {
            BinaryPrimitives.WriteInt32LittleEndian(counter, offset / 32);
            SHA256.HashData(counter).AsSpan(0, Math.Min(32, size - offset)).CopyTo(result.AsSpan(offset));
        }
        return result;
    }

    private sealed class FragmentedArchiveStream(Stream source) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, Math.Min(count, 3));
        public override int Read(Span<byte> buffer) => source.Read(buffer[..Math.Min(buffer.Length, 3)]);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            source.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
        public override bool CanRead => source.CanRead;
        public override bool CanSeek => source.CanSeek;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position { get => source.Position; set => source.Position = value; }
        public override void Flush() => source.Flush();
        public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
