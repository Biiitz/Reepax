using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Services.Extractor;
using Reepax.Services.Storage;
using SharpCompress.Archives;
using SharpCompress.Readers;
using Xunit;

namespace Reepax.Tests;

public partial class ArchivePasswordTests
{
    [Theory]
    [InlineData(" Key ")]
    [InlineData("  ")]
    public async Task ExactPasswords_SurviveStorageAndUnlockArchive(string password)
    {
        var path = Path.Combine(_tempDirectory, "exact.json");
        var storage = new SettingsService(path);
        Assert.True(storage.RememberArchivePassword(password));
        Assert.False(storage.RememberArchivePassword(password));
        storage.SaveSettings();
        Assert.Equal(password, Assert.Single(new SettingsService(path).GetArchivePasswordEntries()).Password);
        SettingsService.Instance.ReplaceArchivePasswords(new[] { password });
        var archive = Path.Combine(_tempDirectory, "exact.zip");
        File.WriteAllBytes(archive, CreateEncryptedZip(password, "payload.txt", "sample payload"));
        Assert.True(await ArchiveExtractionService.Instance.ExtractArchiveAsync(archive, Path.Combine(_tempDirectory, "output")));
        Assert.Equal("sample payload", File.ReadAllText(Path.Combine(_tempDirectory, "output", "payload.txt")));
    }

    [Fact]
    public async Task IncorrectPrompt_CanBeRetriedAndOnlySuccessfulPasswordIsRemembered()
    {
        SettingsService.Instance.ReplaceArchivePasswords(Array.Empty<string>());
        var archive = Path.Combine(_tempDirectory, "retry.zip");
        File.WriteAllBytes(archive, CreateEncryptedZip(" Key ", "payload.txt", "sample payload"));
        var original = ArchiveExtractionService.PasswordRetryPromptHandler;
        ArchiveExtractionService.PasswordPromptHandler = null;
        int prompts = 0;
        try
        {
            ArchiveExtractionService.PasswordRetryPromptHandler = (_, retry, _) =>
            {
                Assert.Equal(prompts > 0, retry);
                return Task.FromResult<(string?, bool)>((++prompts == 1 ? "WrongKey" : " Key ", true));
            };
            Assert.True(await ArchiveExtractionService.Instance.ExtractArchiveAsync(archive, Path.Combine(_tempDirectory, "output")));
            Assert.Equal(2, prompts);
            var stored = Assert.Single(SettingsService.Instance.GetArchivePasswordEntries());
            Assert.Equal(" Key ", stored.Password);
            Assert.Equal(1, stored.UsageCount);
            Assert.Equal(1, stored.SuccessCount);
            Assert.NotNull(stored.LastSuccessAt);
        }
        finally { ArchiveExtractionService.PasswordRetryPromptHandler = original; }
    }

    [Fact]
    public async Task SuccessfulPassword_IsPreferredAndIncorrectExplicitPasswordFallsBack()
    {
        SettingsService.Instance.ReplaceArchivePasswords(new[] { "WrongKey", "KnownKey" });
        var token = SettingsService.Instance.RecordArchivePasswordAttempt("KnownKey")!;
        SettingsService.Instance.RecordArchivePasswordSuccess(token);
        var archive = Path.Combine(_tempDirectory, "priority.zip");
        File.WriteAllBytes(archive, CreateEncryptedZip("KnownKey", "payload.txt", "sample payload"));
        Assert.True(await ArchiveExtractionService.Instance.ExtractArchiveAsync(archive, Path.Combine(_tempDirectory, "preferred")));
        Assert.Equal(0, SettingsService.Instance.GetArchivePasswordEntries()[0].UsageCount);
        Assert.True(await ArchiveExtractionService.Instance.ExtractArchiveAsync(archive, Path.Combine(_tempDirectory, "fallback"),
            explicitPassword: "WrongKey"));
        var entries = SettingsService.Instance.GetArchivePasswordEntries();
        Assert.Equal(1, entries[0].UsageCount);
        Assert.Equal(3, entries[1].SuccessCount);
    }

    [Fact]
    public async Task WeakEncryptionHeaderMatch_DoesNotAcceptIncorrectPayload()
    {
        var archive = Path.Combine(_tempDirectory, "collision.zip");
        File.WriteAllBytes(archive, CreateEncryptedZip("KnownKey", "payload.txt", new string('a', 2 * 1024 * 1024)));
        // This candidate passes the traditional ZIP header's one-byte password check.
        const string collision = "Candidate266";
        using (var legacy = ArchiveFactory.OpenArchive(archive, new ReaderOptions { Password = collision }))
        using (var stream = legacy.Entries.Single().OpenEntryStream())
            Assert.Equal(32, stream.Read(new byte[32], 0, 32));
        SettingsService.Instance.ReplaceArchivePasswords(new[] { collision, "KnownKey" });
        var output = Path.Combine(_tempDirectory, "output");
        Directory.CreateDirectory(output);
        var destination = Path.Combine(output, "payload.txt");
        File.WriteAllText(destination, "original content");
        ArchiveExtractionService.PasswordPromptHandler = _ => throw new InvalidOperationException("Unexpected prompt.");
        Assert.True(await ArchiveExtractionService.Instance.ExtractArchiveAsync(archive, output,
            progressCallback: value =>
            {
                if (value < 100) Assert.Equal("original content", File.ReadAllText(destination));
            }));
        Assert.Equal(new string('a', 2 * 1024 * 1024), File.ReadAllText(destination));
        var statistics = SettingsService.Instance.GetArchivePasswordEntries();
        Assert.Equal(1, statistics[0].UsageCount);
        Assert.Equal(0, statistics[0].SuccessCount);
        Assert.Equal(1, statistics[1].SuccessCount);
        Assert.Empty(Directory.GetDirectories(_tempDirectory, ".archive-work-*", SearchOption.AllDirectories));
    }

    [Fact]
    public void SmallEncryptedEntry_ProbeRejectsMatchingHeaderWithIncorrectCrc()
    {
        var archive = Path.Combine(_tempDirectory, "small-collision.zip");
        File.WriteAllBytes(archive, CreateEncryptedZip("KnownKey", "payload.txt", "sample payload"));
        Assert.False(ArchiveExtractionService.TryTestArchivePassword(archive, "Candidate266", out bool encrypted));
        Assert.True(encrypted);
        Assert.True(ArchiveExtractionService.TryTestArchivePassword(archive, "KnownKey", out _));
    }

    [Theory]
    [InlineData("headers.rar")]
    [InlineData("content.7z")]
    [InlineData("headers.7z")]
    public async Task EncryptedFixtures_ExtractWithExactPassword(string fixture)
    {
        CopyArchiveFixtures();
        SettingsService.Instance.ReplaceArchivePasswords(new[] { "WrongKey", " Fixture Key " });
        ArchiveExtractionService.PasswordPromptHandler = _ => throw new InvalidOperationException("Unexpected prompt.");
        var output = Path.Combine(_tempDirectory, "output");
        Assert.True(await ArchiveExtractionService.Instance.ExtractArchiveAsync(Path.Combine(_tempDirectory, fixture), output));
        var payload = File.ReadAllBytes(Path.Combine(output, "payload.txt"));
        Assert.Equal("verified sample payload", System.Text.Encoding.UTF8.GetString(payload));
        Assert.Equal(1, SettingsService.Instance.GetArchivePasswordEntries()[1].SuccessCount);
        Assert.Empty(Directory.GetDirectories(_tempDirectory, ".archive-work-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task MissingEncryptedVolume_DoesNotPromptOrTouchDestination()
    {
        CopyArchiveFixtures();
        File.Delete(Path.Combine(_tempDirectory, "volumes.part2.rar"));
        ArchiveExtractionService.PasswordPromptHandler = _ => throw new InvalidOperationException("Unexpected prompt.");
        string? status = null;
        Assert.False(await ArchiveExtractionService.Instance.ExtractArchiveAsync(
            Path.Combine(_tempDirectory, "volumes.part1.rar"), Path.Combine(_tempDirectory, "output"), value => status = value));
        Assert.Contains("volumes.part2.rar", status);
        Assert.False(Directory.Exists(Path.Combine(_tempDirectory, "output")));
    }

    [Fact]
    public async Task EncryptedSplitEntry_ReplacesExistingFileWithExactOriginalBytes()
    {
        CopyArchiveFixtures();
        var archive = Path.Combine(_tempDirectory, "volumes.part1.rar");
        SettingsService.Instance.ReplaceArchivePasswords(new[] { " Fixture Key " });
        ArchiveExtractionService.PasswordPromptHandler = _ => throw new InvalidOperationException("Unexpected prompt.");
        var target = Path.Combine(_tempDirectory, "output");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "payload.txt"), "original content");
        string? status = null;
        Assert.True(await ArchiveExtractionService.Instance.ExtractArchiveAsync(archive, target, value => status = value));
        Assert.Equal(Reepax.Services.Localization.Loc.Get("Status_ExtractionCompleted"), status);
        Assert.Equal(Enumerable.Range(0, 16384).Select(index => (byte)index).ToArray(), File.ReadAllBytes(Path.Combine(target, "payload.txt")));
        var statistics = Assert.Single(SettingsService.Instance.GetArchivePasswordEntries());
        Assert.Equal(1, statistics.UsageCount);
        Assert.Equal(1, statistics.SuccessCount);
        Assert.Empty(Directory.GetDirectories(_tempDirectory, ".archive-work-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CorruptedEncryptedPackage_PreservesArchivePartsDespiteAutomaticDeletion()
    {
        CopyArchiveFixtures();
        var damagedPath = Path.Combine(_tempDirectory, "volumes.part2.rar");
        var damaged = File.ReadAllBytes(damagedPath);
        damaged[damaged.Length / 2] ^= 0x40;
        File.WriteAllBytes(damagedPath, damaged);
        SettingsService.Instance.ReplaceArchivePasswords(new[] { " Fixture Key " });
        ArchiveExtractionService.PasswordPromptHandler = _ => Task.FromResult<(string?, bool)>((null, false));
        var package = new Reepax.Models.DownloadPackage
        {
            Name = "Sample package", SaveDirectory = _tempDirectory, AutoExtractArchives = true,
            DeleteArchiveAfterExtraction = true, AutoPar2Repair = false
        };
        var parts = Directory.GetFiles(_tempDirectory, "volumes.part*.rar");
        foreach (var path in parts)
            package.Items.Add(new Reepax.Models.DownloadItem
            {
                FileName = Path.GetFileName(path), SaveFilePath = path,
                Status = Reepax.Models.DownloadStatus.Completed, IsEnabled = true
            });
        Assert.False(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package, force: true));
        Assert.True(ExtractionErrorClassifier.IsExtractionErrorStatus(package.StatusMessage));
        Assert.All(parts, path => Assert.True(File.Exists(path)));
        Assert.False(package.IsExtracted);
    }

    [Fact]
    public async Task EncryptedExtraction_RespectsCancellationWithoutPublishing()
    {
        CopyArchiveFixtures();
        SettingsService.Instance.ReplaceArchivePasswords(new[] { " Fixture Key " });
        using var cancellation = new CancellationTokenSource();
        var output = Path.Combine(_tempDirectory, "output");
        Assert.False(await ArchiveExtractionService.Instance.ExtractArchiveAsync(Path.Combine(_tempDirectory, "headers.7z"),
            output, progressCallback: value => { if (value > 0) cancellation.Cancel(); }, cancellationToken: cancellation.Token));
        Assert.False(File.Exists(Path.Combine(output, "payload.txt")));
        Assert.Equal(0, Assert.Single(SettingsService.Instance.GetArchivePasswordEntries()).SuccessCount);
        Assert.Empty(Directory.GetDirectories(_tempDirectory, ".archive-work-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task EncryptedExtraction_PausesBeforePublishingAndCanResume()
    {
        CopyArchiveFixtures();
        SettingsService.Instance.ReplaceArchivePasswords(new[] { " Fixture Key " });
        using var pause = new ManualResetEventSlim(true);
        using var cancellation = new CancellationTokenSource();
        var paused = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = Path.Combine(_tempDirectory, "output");
        bool pauseRequested = false;
        var operation = ArchiveExtractionService.Instance.ExtractArchiveAsync(Path.Combine(_tempDirectory, "headers.7z"),
            output, progressCallback: _ =>
            {
                if (!pauseRequested) { pauseRequested = true; pause.Reset(); }
            }, pauseEvent: pause, cancellationToken: cancellation.Token,
            pauseStateChanged: value => { if (value) paused.TrySetResult(true); });
        try
        {
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(operation.IsCompleted);
            Assert.False(File.Exists(Path.Combine(output, "payload.txt")));
            pause.Set();
            Assert.True(await operation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("verified sample payload", File.ReadAllText(Path.Combine(output, "payload.txt")));
        }
        finally
        {
            cancellation.Cancel();
            pause.Set();
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task CancelledEncryptedExtraction_PreservesExistingFilesAndDoesNotRememberPassword()
    {
        SettingsService.Instance.ReplaceArchivePasswords(Array.Empty<string>());
        var archive = Path.Combine(_tempDirectory, "cancel.zip");
        File.WriteAllBytes(archive, CreateEncryptedZip("KnownKey", "payload.txt", new string('a', 2 * 1024 * 1024)));
        var output = Path.Combine(_tempDirectory, "output");
        Directory.CreateDirectory(output);
        var destination = Path.Combine(output, "payload.txt");
        File.WriteAllText(destination, "original content");
        ArchiveExtractionService.PasswordPromptHandler = _ => Task.FromResult<(string?, bool)>(("KnownKey", true));
        using var cancellation = new CancellationTokenSource();
        Assert.False(await ArchiveExtractionService.Instance.ExtractArchiveAsync(archive, output,
            progressCallback: value => { if (value > 0) cancellation.Cancel(); }, cancellationToken: cancellation.Token));
        Assert.Equal("original content", File.ReadAllText(destination));
        Assert.Empty(SettingsService.Instance.GetArchivePasswordEntries());
        Assert.Empty(Directory.GetDirectories(_tempDirectory, ".archive-work-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task PasswordPrompt_CancellationStopsWaiting()
    {
        SettingsService.Instance.ReplaceArchivePasswords(Array.Empty<string>());
        var archive = Path.Combine(_tempDirectory, "cancel-prompt.zip");
        File.WriteAllBytes(archive, CreateEncryptedZip("KnownKey", "payload.txt", "sample payload"));
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ArchiveExtractionService.PasswordPromptHandler = _ =>
        {
            entered.SetResult(true);
            return new TaskCompletionSource<(string?, bool)>().Task;
        };
        var operation = ArchiveExtractionService.Instance.ExtractArchiveAsync(archive, Path.Combine(_tempDirectory, "output"),
            cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        Assert.False(await operation.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task PackageArchives_ReuseSharedSessionPasswordWithoutOpeningPrompts()
    {
        SettingsService.Instance.ReplaceArchivePasswords(Array.Empty<string>());
        var package = new Reepax.Models.DownloadPackage
        {
            Name = "Sample package", SaveDirectory = _tempDirectory, AutoExtractArchives = true,
            DeleteArchiveAfterExtraction = false, AutoPar2Repair = false
        };
        foreach (var name in new[] { "first", "second" })
        {
            var path = Path.Combine(_tempDirectory, name + ".zip");
            File.WriteAllBytes(path, CreateEncryptedZip("KnownKey", name + ".txt", "sample payload"));
            package.Items.Add(new Reepax.Models.DownloadItem
            {
                FileName = name + ".zip", SaveFilePath = path, Status = Reepax.Models.DownloadStatus.Completed, IsEnabled = true
            });
        }
        ArchiveExtractionService.Instance.AddPackageArchivePassword(package, "*", "KnownKey", remember: false);
        int prompts = 0;
        ArchiveExtractionService.PasswordPromptHandler = _ =>
        {
            prompts++;
            throw new InvalidOperationException("Unexpected prompt.");
        };
        Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package, force: true));
        Assert.Equal(0, prompts);
        Assert.Equal("sample payload", File.ReadAllText(Path.Combine(_tempDirectory, "first.txt")));
        Assert.Equal("sample payload", File.ReadAllText(Path.Combine(_tempDirectory, "second.txt")));
        Assert.Empty(SettingsService.Instance.GetArchivePasswordEntries());
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("payload.txt:alternate")]
    public async Task EncryptedEntry_CannotPublishUnsafePaths(string entryName)
    {
        var archive = Path.Combine(_tempDirectory, "unsafe.zip");
        File.WriteAllBytes(archive, CreateEncryptedZip("KnownKey", entryName, "sample payload"));
        SettingsService.Instance.ReplaceArchivePasswords(new[] { "KnownKey" });
        ArchiveExtractionService.PasswordPromptHandler = _ => Task.FromResult<(string?, bool)>((null, false));
        Assert.False(await ArchiveExtractionService.Instance.ExtractArchiveAsync(archive, Path.Combine(_tempDirectory, "output")));
        Assert.False(File.Exists(Path.Combine(_tempDirectory, "outside.txt")));
        Assert.Empty(Directory.GetDirectories(_tempDirectory, ".archive-work-*", SearchOption.AllDirectories));
    }

    [Fact]
    public void PublishFailure_RollsBackPreviouslyReplacedFiles()
    {
        var target = Path.Combine(_tempDirectory, "output");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "first.txt"), "original content");
        File.WriteAllText(Path.Combine(target, "locked.txt"), "locked content");
        using (var workspace = new ArchiveExtractionWorkspace(target))
        {
            File.WriteAllText(Path.Combine(workspace.OutputDirectory, "first.txt"), "replacement content");
            File.WriteAllText(Path.Combine(workspace.OutputDirectory, "locked.txt"), "replacement content");
            using var locked = new FileStream(Path.Combine(target, "locked.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.ThrowsAny<IOException>(workspace.Publish);
            Assert.Equal("original content", File.ReadAllText(Path.Combine(target, "first.txt")));
            Assert.Equal("locked content", File.ReadAllText(Path.Combine(target, "locked.txt")));
        }
        Assert.Empty(Directory.GetDirectories(_tempDirectory, ".archive-work-*", SearchOption.AllDirectories));
    }

    private void CopyArchiveFixtures()
    {
        var assembly = typeof(ArchivePasswordTests).Assembly;
        const string prefix = "Reepax.Tests.Fixtures.Archives.";
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith(prefix, StringComparison.Ordinal)))
        {
            using var source = assembly.GetManifestResourceStream(name)!;
            using var output = File.Create(Path.Combine(_tempDirectory, name[prefix.Length..]));
            source.CopyTo(output);
        }
    }
}
