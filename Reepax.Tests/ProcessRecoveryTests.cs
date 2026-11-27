using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Reepax.Services.Verification;
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

    [Theory]
    [InlineData("renamed")]
    [InlineData("partial")]
    [InlineData("locked-target")]
    [InlineData("foreign-backup")]
    [InlineData("committed")]
    [InlineData("committed-changed")]
    public async Task KilledPar2Repair_RestoresOnlyItsProvenBackup(string boundary)
    {
        PrepareDestination();
        string path = Path.Combine(Downloads, "repair.bin");
        byte[] original = Data(32 * 1024, 23);
        File.WriteAllBytes(path, original);
        await Par2RepairServiceTests.CreateParityAsync(Downloads, path);
        byte[] damaged = original.ToArray();
        damaged[41] ^= 0x71;
        File.WriteAllBytes(path, damaged);
        File.WriteAllBytes(path + ".1", Unaffected);
        string unrelated = Path.Combine(Downloads, "other.bin.2");
        File.WriteAllBytes(unrelated, Unaffected);
        string journals = Path.Combine(_directory.FullName, "appdata", "Par2Recovery");
        bool committed = boundary is "committed" or "committed-changed";

        await using (var child = new RecoveryChildProcess(_directory.FullName, "par2-crash", boundary, "none", 1))
        {
            var report = await child.ReceiveAsync();
            Assert.Equal(boundary, report.Boundary);
            using var native = report.NativeProcessId == 0 ? null : System.Diagnostics.Process.GetProcessById(report.NativeProcessId);
            try
            {
                await child.KillAtBoundaryAsync();
                if (native != null) await native.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(damaged, File.ReadAllBytes(path + ".2"));
                Assert.Single(Directory.GetFiles(journals, "*.journal"));
                if (boundary == "renamed") Assert.False(File.Exists(path));
                else Assert.Equal(committed ? original : new byte[] { 9, 8, 7 }, File.ReadAllBytes(path));
            }
            finally
            {
                if (native != null)
                {
                    if (!native.HasExited) native.Kill(entireProcessTree: true);
                    await native.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
        }

        if (boundary == "locked-target")
        {
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await RecoverAsync();
                Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(path));
                Assert.Equal(damaged, File.ReadAllBytes(path + ".2"));
                Assert.Single(Directory.GetFiles(journals, "*.journal"));
            }
        }
        else if (boundary == "foreign-backup")
        {
            string retained = path + ".3";
            File.Move(path + ".2", retained);
            File.WriteAllBytes(path + ".2", damaged);
            await RecoverAsync();
            Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(path));
            Assert.Equal(damaged, File.ReadAllBytes(path + ".2"));
            Assert.Equal(damaged, File.ReadAllBytes(retained));
            Assert.Single(Directory.GetFiles(journals, "*.journal"));
            File.Move(path + ".2", path + ".4");
            File.Move(retained, path + ".2");
        }
        else if (boundary == "committed-changed")
        {
            string repaired = path + ".3";
            File.Move(path, repaired);
            File.WriteAllBytes(path, Unaffected);
            await RecoverAsync();
            Assert.Equal(Unaffected, File.ReadAllBytes(path));
            Assert.Equal(damaged, File.ReadAllBytes(path + ".2"));
            Assert.Equal(original, File.ReadAllBytes(repaired));
            Assert.Single(Directory.GetFiles(journals, "*.journal"));
            File.Move(path, path + ".4");
            File.Move(repaired, path);
        }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            await RecoverAsync();
            Assert.True(File.Exists(path), "The original PAR2 target was not restored.");
            Assert.Equal(committed ? original : damaged, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".2"));
            Assert.Equal(Unaffected, File.ReadAllBytes(path + ".1"));
            Assert.Equal(Unaffected, File.ReadAllBytes(unrelated));
            if (boundary is "partial" or "locked-target" or "foreign-backup")
                Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(Assert.Single(Directory.GetFiles(Downloads, "*.interrupted-*"))));
            if (boundary == "foreign-backup") Assert.Equal(damaged, File.ReadAllBytes(path + ".4"));
            if (boundary == "committed-changed") Assert.Equal(Unaffected, File.ReadAllBytes(path + ".4"));
            Assert.Empty(Directory.GetFiles(journals));
            AssertUnaffected();
        }

        async Task RecoverAsync()
        {
            await using var recovery = new RecoveryChildProcess(_directory.FullName, "par2-recover", "none", "none", 1);
            Assert.Equal("recovered", (await recovery.ReceiveAsync()).Boundary);
            await recovery.AssertExitedSuccessfullyAsync();
        }
    }

    [Theory]
    [InlineData("missing-partial", false)]
    [InlineData("missing-partial", true)]
    [InlineData("missing-unpublished", false)]
    [InlineData("missing-unpublished", true)]
    [InlineData("missing-committed", true)]
    public async Task KilledPar2Reconstruction_PreservesMissingOrIndependentlyCreatedTarget(string boundary, bool occupied)
    {
        PrepareDestination();
        const int blockSize = 256 * 1024;
        byte[] original = Data(64 * blockSize, 37);
        string path = Path.Combine(Downloads, "missing.bin");
        string parity = Path.Combine(Downloads, "data.par2");
        File.WriteAllBytes(path, original);
        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Par2RepairService.Instance.ResolvePar2ExecutablePath()!,
            WorkingDirectory = Downloads, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (string arg in new[] { "c", "-qq", $"-s{blockSize}", "-c64", parity, path })
            start.ArgumentList.Add(arg);
        using (var creator = System.Diagnostics.Process.Start(start)!)
        {
            try { await creator.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
            finally { if (!creator.HasExited) creator.Kill(entireProcessTree: true); }
            Assert.Equal(0, creator.ExitCode);
        }
        File.Delete(path);

        bool committed = boundary == "missing-committed";
        string journals = Path.Combine(_directory.FullName, "appdata", "Par2Recovery");
        string independent = Path.Combine(Downloads, "independent.bin");
        await using (var child = new RecoveryChildProcess(_directory.FullName, "par2-crash", boundary, "none", 1))
        {
            var report = await child.ReceiveAsync();
            Assert.Equal(boundary, report.Boundary);
            await child.KillAtBoundaryAsync();
        }
        Assert.False(File.Exists(path));
        string workspace = Assert.Single(Directory.GetDirectories(Downloads, ".par2-work-*"));
        byte[] staged = File.ReadAllBytes(Path.Combine(workspace, "missing.bin"));
        Assert.Equal(boundary == "missing-partial" ? original[..4096] : original, staged);
        if (occupied)
        {
            using var outside = File.Open(path, FileMode.CreateNew);
            outside.Write(Unaffected);
        }

        await RecoverPar2Async();
        if (occupied)
        {
            Assert.Equal(Unaffected, File.ReadAllBytes(path));
            File.Move(path, independent);
        }
        else
        {
            Assert.False(File.Exists(path));
        }

        if (committed)
        {
            Assert.Single(Directory.GetFiles(journals, "*.journal"));
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(workspace, "missing.bin")));
            await RecoverPar2Async();
            await RecoverPar2Async();
        }
        Assert.Empty(Directory.GetDirectories(Downloads, ".par2-work-*"));
        Assert.Empty(Directory.GetFiles(journals, "*.journal"));
        AssertUnaffected();
        if (!committed) Assert.True((await Par2RepairService.Instance.RepairAsync(parity)).Success);
        Assert.Equal(original, File.ReadAllBytes(path));
        if (occupied) Assert.Equal(Unaffected, File.ReadAllBytes(independent));
        AssertUnaffected();
    }

    private async Task RecoverPar2Async()
    {
        await using var recovery = new RecoveryChildProcess(_directory.FullName, "par2-recover", "none", "none", 1);
        Assert.Equal("recovered", (await recovery.ReceiveAsync()).Boundary);
        await recovery.AssertExitedSuccessfullyAsync();
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
