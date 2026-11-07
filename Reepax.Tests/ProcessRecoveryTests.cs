using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Reepax.Tests;

[Trait("Category", "Integration")]
public sealed partial class ProcessRecoveryTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("ProcessRecovery_");
    private static readonly byte[] Unaffected = Encoding.UTF8.GetBytes("independent fixture document");
    private string Downloads => Path.Combine(_directory.FullName, "downloads");

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task KilledDownload_ResumesItsDurableCheckpointInAFreshProcess(int connections)
    {
        byte[] original = Data(connections == 1 ? 1024 * 1024 : 8 * 1024 * 1024, 73);
        string final = Path.Combine(Downloads, "download.bin");
        File.WriteAllBytes(Path.Combine(_directory.FullName, "download.bin"), original);
        PrepareDestination();
        await using var source = new PipelineHttpSource(new Dictionary<string, byte[]> { ["download.bin"] = original });
        await using (var child = new RecoveryChildProcess(_directory.FullName, "download-crash", "checkpoint", source.Url("download.bin"), connections))
        {
            var state = await child.ReceiveAsync();
            Assert.Equal("checkpoint", state.Boundary);
            Assert.False(state.Completed);
            Assert.False(state.Notified);
            Assert.Null(state.Checksum);
            await child.KillAtBoundaryAsync();
        }
        Assert.False(File.Exists(final));
        Assert.True(File.Exists(final + ".part"));
        AssertUnaffected();
        using var checkpoint = JsonDocument.Parse(File.ReadAllText(final + ".part.segments"));
        var segments = checkpoint.RootElement.GetProperty("Segments").EnumerateArray().ToArray();
        Assert.Equal(connections, segments.Length);
        long committed = 0;
        using (var part = File.OpenRead(final + ".part"))
        {
            foreach (var segment in segments)
            {
                int start = segment.GetProperty("Start").GetInt32();
                int done = segment.GetProperty("Done").GetInt32();
                var bytes = new byte[done];
                part.Position = start;
                part.ReadExactly(bytes);
                Assert.Equal(original.AsSpan(start, done).ToArray(), bytes);
                committed += done;
            }
        }
        Assert.InRange(committed, 1, original.Length - 1);
        if (connections > 1) Assert.Equal(original.Length, new FileInfo(final + ".part").Length);
        int previousRequests = source.Ranges.Count;
        string expectedHash = Convert.ToHexString(SHA256.HashData(original));
        for (int recovery = 0; recovery < 2; recovery++)
        {
            await using var child = new RecoveryChildProcess(_directory.FullName, "download-recover", "none", source.Url("download.bin"), connections);
            var state = await child.ReceiveAsync();
            await child.AssertExitedSuccessfullyAsync();
            Assert.Equal("recovered", state.Boundary);
            Assert.True(state.Completed);
            Assert.Equal(original.Length, state.Downloaded);
            Assert.Equal(recovery == 0 ? committed : original.Length, state.ResumedFrom);
            Assert.Equal(expectedHash, state.Checksum, ignoreCase: true);
            Assert.Equal(original, File.ReadAllBytes(final));
            Assert.False(File.Exists(final + ".part"));
            Assert.False(File.Exists(final + ".part.segments"));
            AssertUnaffected();
            if (recovery == 0)
            {
                var resumedRanges = source.Ranges.Skip(previousRequests).ToArray();
                Assert.Contains(resumedRanges, range => segments.Any(segment =>
                    segment.GetProperty("Done").GetInt64() > 0 &&
                    range.Start == segment.GetProperty("Start").GetInt64() + segment.GetProperty("Done").GetInt64()));
                previousRequests = source.Ranges.Count;
            }
            else Assert.Equal(previousRequests, source.Ranges.Count);
        }
    }

    [Theory]
    [InlineData("BeforeOriginalMove")]
    [InlineData("OriginalMoved")]
    [InlineData("ReplacementMoved")]
    [InlineData("CommitDecided")]
    public async Task KilledArchivePublication_RecoversRealMovesAndItsDurableDecision(string boundary)
    {
        PrepareDestination();
        var previous = new Dictionary<string, byte[]> { ["payload.bin"] = Data(257, 19), ["companion.bin"] = Data(513, 23) };
        var replacement = new Dictionary<string, byte[]> { ["payload.bin"] = Data(16 * 1024, 29), ["companion.bin"] = Data(12 * 1024, 31) };
        foreach (var (name, bytes) in previous) File.WriteAllBytes(Path.Combine(Downloads, name), bytes);
        byte[] archive = Archive(replacement);
        File.WriteAllBytes(Path.Combine(_directory.FullName, "source.zip"), archive);
        await using var source = new PipelineHttpSource(new Dictionary<string, byte[]> { ["source.zip"] = archive });
        await using (var child = new RecoveryChildProcess(_directory.FullName, "archive-crash", boundary, source.Url("source.zip"), 1))
        {
            var state = await child.ReceiveAsync();
            Assert.Equal(boundary, state.Boundary);
            Assert.False(state.Completed);
            Assert.False(state.Extracted);
            Assert.False(state.Notified);
            Assert.Equal(archive.Length, state.Downloaded);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(archive)), state.Checksum, ignoreCase: true);
            await child.KillAtBoundaryAsync();
        }
        Assert.Equal(archive, File.ReadAllBytes(Path.Combine(Downloads, "source.zip")));
        AssertUnaffected();
        string[] workspaces = Directory.GetDirectories(Downloads, ".archive-work-*", SearchOption.TopDirectoryOnly);
        string workspace = Assert.Single(workspaces);
        using var journal = JsonDocument.Parse(File.ReadAllText(Path.Combine(workspace, "publication.json")));
        Assert.False(journal.RootElement.GetProperty("Committed").GetBoolean());
        int payloadIndex = journal.RootElement.GetProperty("Entries").EnumerateArray()
            .Select((entry, index) => (entry, index)).Single(value => value.entry.GetProperty("Path").GetString() == "payload.bin").index;
        string target = Path.Combine(Downloads, "payload.bin");
        string staged = Path.Combine(workspace, "output", "payload.bin");
        string backup = Path.Combine(workspace, "backup", payloadIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (boundary == "BeforeOriginalMove")
        {
            Assert.Equal(previous["payload.bin"], File.ReadAllBytes(target));
            Assert.False(File.Exists(backup));
        }
        else
        {
            Assert.Equal(previous["payload.bin"], File.ReadAllBytes(backup));
            if (boundary == "OriginalMoved") Assert.False(File.Exists(target));
            else Assert.Equal(replacement["payload.bin"], File.ReadAllBytes(target));
        }
        if (boundary is "BeforeOriginalMove" or "OriginalMoved") Assert.Equal(replacement["payload.bin"], File.ReadAllBytes(staged));
        else Assert.False(File.Exists(staged));
        if (boundary is "ReplacementMoved" or "CommitDecided")
            foreach (var (name, bytes) in replacement) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(Downloads, name)));
        if (boundary != "CommitDecided")
        {
            var retained = Directory.GetFiles(Downloads, "*", SearchOption.AllDirectories)
                .Where(path => !path.EndsWith("lease.lock", StringComparison.Ordinal)).Select(File.ReadAllBytes).ToArray();
            foreach (byte[] original in previous.Values)
                Assert.Contains(retained, bytes => bytes.SequenceEqual(original));
        }
        else
        {
            string commit = Assert.Single(Directory.GetFiles(Downloads, "commit", SearchOption.AllDirectories));
            Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(commit));
        }
        var expected = boundary == "CommitDecided" ? replacement : previous;
        for (int recovery = 0; recovery < 2; recovery++)
        {
            await using var child = new RecoveryChildProcess(_directory.FullName, "archive-recover", "none", source.Url("source.zip"), 1);
            var state = await child.ReceiveAsync();
            await child.AssertExitedSuccessfullyAsync();
            Assert.False(state.Completed);
            Assert.False(state.Extracted);
            Assert.False(state.Notified);
            foreach (var (name, bytes) in expected) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(Downloads, name)));
            Assert.Equal(archive, File.ReadAllBytes(Path.Combine(Downloads, "source.zip")));
            Assert.Empty(Directory.GetDirectories(Downloads, ".archive-work-*", SearchOption.AllDirectories));
            AssertUnaffected();
        }
        Assert.Equal(1, source.Requests["source.zip"]);
    }

    private void PrepareDestination()
    {
        Directory.CreateDirectory(Downloads);
        File.WriteAllBytes(Path.Combine(Downloads, "unaffected.bin"), Unaffected);
    }

    private void AssertUnaffected() => Assert.Equal(Unaffected, File.ReadAllBytes(Path.Combine(Downloads, "unaffected.bin")));

    private static byte[] Data(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static byte[] Archive(IReadOnlyDictionary<string, byte[]> files)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in files)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(2020, 1, 2, 0, 0, 0, TimeSpan.Zero);
                using var output = entry.Open();
                output.Write(bytes);
            }
        }
        return memory.ToArray();
    }

    public void Dispose()
    {
        string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!string.Equals(_directory.Parent?.FullName, parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected recovery test directory.");
        _directory.Delete(recursive: true);
    }
}
