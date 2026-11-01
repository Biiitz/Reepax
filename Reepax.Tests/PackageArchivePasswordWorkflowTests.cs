using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Reepax.Models;
using Reepax.Services.Extractor;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;
using Xunit;

namespace Reepax.Tests;

public partial class ArchivePasswordTests
{
    [Fact]
    public async Task PackagePasswordWait_DoesNotExtractEarlierArchivesOrRepeatUnchangedTrials()
    {
        var package = CreatePasswordWorkflowPackage(("first", "FirstKey"), ("second", "SecondKey"));
        SettingsService.Instance.ReplaceArchivePasswords(new[] { "FirstKey" });
        ArchiveExtractionService.PasswordPromptHandler = _ => throw new InvalidOperationException("Unexpected prompt.");
        Assert.False(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package));
        Assert.True(package.RequiresArchivePassword);
        Assert.False(package.IsExtracting);
        Assert.False(package.IsFullyCompleted);
        Assert.True(PostDownloadActionService.CheckHasActiveWork(new[] { package }));
        Assert.False(File.Exists(Path.Combine(_tempDirectory, "first.txt")));
        Assert.False(File.Exists(Path.Combine(_tempDirectory, "second.txt")));
        Assert.All(package.Items, item => Assert.True(File.Exists(item.SaveFilePath)));
        var attempts = SettingsService.Instance.GetArchivePasswordEntries().Single().UsageCount;
        for (int index = 0; index < 3; index++)
            Assert.False(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package));
        SettingsService.Instance.RecordArchivePasswordAttempt("FirstKey");
        Assert.False(ArchiveExtractionService.Instance.CanRetryPackagePasswords(package));
        Assert.Equal(attempts + 1, SettingsService.Instance.GetArchivePasswordEntries().Single().UsageCount);
        Assert.Equal(0, SettingsService.Instance.GetArchivePasswordEntries().Single().SuccessCount);
        Assert.Empty(Directory.GetDirectories(_tempDirectory, ".archive-work-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task NewSavedPassword_ResumesWaitingPackageAndVerifiesEachArchive()
    {
        var package = CreatePasswordWorkflowPackage(("first", "FirstKey"), ("second", "SecondKey"));
        SettingsService.Instance.ReplaceArchivePasswords(new[] { "FirstKey" });
        Assert.False(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package));
        SettingsService.Instance.RememberArchivePassword("SecondKey");
        Assert.True(ArchiveExtractionService.Instance.CanRetryPackagePasswords(package));
        Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package));
        Assert.False(package.RequiresArchivePassword);
        Assert.True(package.IsExtracted);
        Assert.Equal("first payload", File.ReadAllText(Path.Combine(_tempDirectory, "first.txt")));
        Assert.Equal("second payload", File.ReadAllText(Path.Combine(_tempDirectory, "second.txt")));
        Assert.All(SettingsService.Instance.GetArchivePasswordEntries(), entry => Assert.Equal(1, entry.SuccessCount));
        var candidate = ArchiveExtractionService.Instance.GetPackagePasswordCandidates(package, "first.zip")
            .Single(candidate => candidate.Password == "FirstKey");
        Assert.Equal(ArchivePasswordOutcome.Verified, candidate.Outcome);
    }

    [Fact]
    public async Task ScopedManualPasswords_AreNotTriedAgainstOtherArchives()
    {
        var package = CreatePasswordWorkflowPackage(("first", "FirstKey"), ("second", "SecondKey"));
        SettingsService.Instance.ReplaceArchivePasswords(Array.Empty<string>());
        ArchiveExtractionService.Instance.AddPackageArchivePassword(package, "first.zip", "FirstKey", remember: false);
        Assert.False(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package, force: true));
        Assert.Empty(ArchiveExtractionService.Instance.GetPackagePasswordCandidates(package, "second.zip"));
        ArchiveExtractionService.Instance.AddPackageArchivePassword(package, "second.zip", "SecondKey", remember: false);
        Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package, force: true));
        Assert.Empty(SettingsService.Instance.GetArchivePasswordEntries());
    }

    [Fact]
    public async Task LateArchiveCorruption_RollsBackWholePackageAndDoesNotRememberPassword()
    {
        CopyDecoderFixture("compressed");
        var damaged = Directory.GetFiles(_tempDirectory, "compressed.part*.rar").OrderBy(path => path).Last();
        var bytes = File.ReadAllBytes(damaged);
        bytes[bytes.Length / 2] ^= 0x40;
        File.WriteAllBytes(damaged, bytes);
        var package = CreatePasswordWorkflowPackage(("first", " Fixture Key "));
        foreach (var path in Directory.GetFiles(_tempDirectory, "compressed.part*.rar"))
            package.Items.Add(CompletedArchiveItem(path));
        File.WriteAllText(Path.Combine(_tempDirectory, "first.txt"), "existing first");
        File.WriteAllText(Path.Combine(_tempDirectory, "payload.bin"), "existing payload");
        SettingsService.Instance.ReplaceArchivePasswords(Array.Empty<string>());
        ArchiveExtractionService.Instance.AddPackageArchivePassword(package, "*", " Fixture Key ", remember: true);
        Assert.False(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package, force: true));
        Assert.False(package.IsExtracted);
        Assert.Equal("existing first", File.ReadAllText(Path.Combine(_tempDirectory, "first.txt")));
        Assert.Equal("existing payload", File.ReadAllText(Path.Combine(_tempDirectory, "payload.bin")));
        Assert.False(File.Exists(Path.Combine(_tempDirectory, "companion.bin")));
        Assert.Empty(SettingsService.Instance.GetArchivePasswordEntries());
        Assert.All(package.Items, item => Assert.True(File.Exists(item.SaveFilePath)));
        Assert.Empty(Directory.GetDirectories(_tempDirectory, ".archive-work-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SessionPassword_IsRememberedOnlyAfterSuccessfulPublication()
    {
        var package = CreatePasswordWorkflowPackage(("first", " Key "));
        SettingsService.Instance.ReplaceArchivePasswords(Array.Empty<string>());
        ArchiveExtractionService.Instance.AddPackageArchivePassword(package, "first.zip", " Key ", remember: true);
        var check = await ArchiveExtractionService.Instance.CheckPackagePasswordsAsync(package, CancellationToken.None);
        Assert.True(check.Ready);
        Assert.Empty(SettingsService.Instance.GetArchivePasswordEntries());
        Assert.False(File.Exists(Path.Combine(_tempDirectory, "first.txt")));
        Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package, force: true));
        var entry = Assert.Single(SettingsService.Instance.GetArchivePasswordEntries());
        Assert.Equal(" Key ", entry.Password);
        Assert.Equal(1, entry.UsageCount);
        Assert.Equal(1, entry.SuccessCount);
    }

    [Fact]
    public async Task ManualPasswordWait_BlocksCompletionUntilCancelled()
    {
        var package = CreatePasswordWorkflowPackage(("first", "KnownKey"));
        package.AutoExtractArchives = false;
        SettingsService.Instance.ReplaceArchivePasswords(Array.Empty<string>());
        Assert.False(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package, force: true));
        Assert.True(package.RequiresArchivePassword);
        Assert.True(PostDownloadActionService.CheckHasActiveWork(new[] { package }));
        ArchiveExtractionService.Instance.CancelPackageExtraction(package);
        Assert.False(package.RequiresArchivePassword);
        Assert.False(ArchiveExtractionService.Instance.CanRetryPackagePasswords(package));
        Assert.False(PostDownloadActionService.CheckHasActiveWork(new[] { package }));
        SettingsService.Instance.RememberArchivePassword("KnownKey");
        Assert.False(ArchiveExtractionService.Instance.CanRetryPackagePasswords(package));
        Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package, force: true));
    }

    [Fact]
    public async Task CancellationImmediatelyAfterStart_DoesNotPublishOrReportSuccess()
    {
        var package = CreatePasswordWorkflowPackage(("first", "KnownKey"));
        SettingsService.Instance.ReplaceArchivePasswords(new[] { "KnownKey" });
        package.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(DownloadPackage.IsExtracting) && package.IsExtracting)
                ArchiveExtractionService.Instance.CancelPackageExtraction(package);
        };
        Assert.False(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package));
        Assert.False(package.IsExtracted);
        Assert.False(package.IsExtracting);
        Assert.False(ArchiveExtractionService.Instance.CanRetryPackagePasswords(package));
        Assert.False(File.Exists(Path.Combine(_tempDirectory, "first.txt")));
        Assert.Empty(Directory.GetDirectories(_tempDirectory, ".archive-work-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task FailedReextraction_DoesNotKeepPreviousSuccessOrIncrementSuccessCounter()
    {
        var package = CreatePasswordWorkflowPackage(("first", "KnownKey"));
        SettingsService.Instance.ReplaceArchivePasswords(new[] { "KnownKey" });
        Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package));
        string output = Path.Combine(_tempDirectory, "first.txt");
        File.SetAttributes(output, FileAttributes.ReadOnly);
        try
        {
            Assert.False(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package, force: true, forceReextract: true));
            Assert.False(package.IsExtracted);
            Assert.Equal("first payload", File.ReadAllText(output));
            Assert.Equal(1, Assert.Single(SettingsService.Instance.GetArchivePasswordEntries()).SuccessCount);
        }
        finally { File.SetAttributes(output, FileAttributes.Normal); }
    }

    [Fact]
    public void PackagePublicationFailure_RestoresEarlierDestination()
    {
        string first = Path.Combine(_tempDirectory, "first");
        string second = Path.Combine(_tempDirectory, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(Path.Combine(second, "payload.txt"));
        File.WriteAllText(Path.Combine(first, "payload.txt"), "original");
        using (var workspace = new PackageExtractionWorkspace(new[] { first, second }))
        {
            File.WriteAllText(Path.Combine(workspace.GetStagingDirectory(first), "payload.txt"), "replacement");
            File.WriteAllText(Path.Combine(workspace.GetStagingDirectory(second), "payload.txt"), "second payload");
            Assert.Throws<IOException>(() => workspace.Publish(Array.Empty<string>(), CancellationToken.None));
            Assert.Equal("original", File.ReadAllText(Path.Combine(first, "payload.txt")));
        }
        Assert.Empty(Directory.GetDirectories(_tempDirectory, ".archive-work-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ExtractedFile_CannotOverwriteItsOwnSourceArchive()
    {
        string path = Path.Combine(_tempDirectory, "source.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("source.zip").Open())) writer.Write("replacement");
        byte[] original = File.ReadAllBytes(path);
        var package = new DownloadPackage { Name = "Sample package", SaveDirectory = _tempDirectory, AutoExtractArchives = true, AutoPar2Repair = false };
        package.Items.Add(CompletedArchiveItem(path));
        Assert.False(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(package.IsExtracted);
    }

    [Fact]
    public void SessionPasswords_AreExcludedFromPackageSerialization()
    {
        var package = new DownloadPackage { Name = "Sample package" };
        const string password = "SessionSampleKey742!";
        ArchiveExtractionService.Instance.AddPackageArchivePassword(package, "*", password, remember: false);
        package.RequiresArchivePassword = true;
        string json = JsonSerializer.Serialize(package, new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.IgnoreCycles });
        Assert.DoesNotContain(password, json);
        Assert.DoesNotContain(nameof(DownloadPackage.ArchivePasswordState), json);
        Assert.DoesNotContain(nameof(DownloadPackage.RequiresArchivePassword), json);
    }

    [Fact]
    public async Task ExplicitPasswordTest_ChecksNewInputBeforeAnAcceptedCandidate()
    {
        var package = CreatePasswordWorkflowPackage(("first", "KnownKey"));
        SettingsService.Instance.ReplaceArchivePasswords(Array.Empty<string>());
        ArchiveExtractionService.Instance.AddPackageArchivePassword(package, "first.zip", "KnownKey", remember: false);
        Assert.True((await ArchiveExtractionService.Instance.CheckPackagePasswordsAsync(package, CancellationToken.None)).Ready);
        ArchiveExtractionService.Instance.AddPackageArchivePassword(package, "first.zip", "WrongKey", remember: false, retry: true);
        Assert.True((await ArchiveExtractionService.Instance.CheckPackagePasswordsAsync(package, CancellationToken.None)).Ready);
        var rows = ArchiveExtractionService.Instance.GetPackagePasswordCandidates(package, "first.zip");
        Assert.Equal(ArchivePasswordOutcome.NotConfirmed, rows.Single(row => row.Password == "WrongKey").Outcome);
        Assert.Equal(ArchivePasswordOutcome.Accepted, rows.Single(row => row.Password == "KnownKey").Outcome);
    }

    [Fact]
    public async Task UnencryptedZip_WithChangedPayloadCannotReplaceExistingOutput()
    {
        string path = Path.Combine(_tempDirectory, "integrity.zip");
        const string payload = "original integrity marker 0102";
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("payload.txt", CompressionLevel.NoCompression).Open()))
            writer.Write(payload);
        byte[] bytes = File.ReadAllBytes(path);
        int offset = bytes.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(payload));
        Assert.True(offset > 0);
        bytes[offset] ^= 0x20;
        File.WriteAllBytes(path, bytes);
        string target = Path.Combine(_tempDirectory, "output");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "payload.txt"), "existing content");
        Assert.False(await ArchiveExtractionService.Instance.ExtractArchiveAsync(path, target));
        Assert.Equal("existing content", File.ReadAllText(Path.Combine(target, "payload.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitingPackage_CanMoveWithoutLosingAttemptHistory(bool sourceOutsideTarget)
    {
        var package = CreatePasswordWorkflowPackage(("first", "KnownKey"));
        if (sourceOutsideTarget)
        {
            package.SaveDirectory = Path.Combine(_tempDirectory, "previous-target");
            Directory.CreateDirectory(package.SaveDirectory);
        }
        SettingsService.Instance.ReplaceArchivePasswords(new[] { "WrongKey" });
        Assert.False(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package));
        string destination = Path.Combine(_tempDirectory, "moved");
        Directory.CreateDirectory(destination);
        foreach (var item in package.Items)
        {
            string moved = Path.Combine(destination, item.FileName);
            Assert.StartsWith(Path.GetFullPath(_tempDirectory) + Path.DirectorySeparatorChar, Path.GetFullPath(moved));
            File.Move(item.SaveFilePath!, moved);
            item.SaveFilePath = moved;
        }
        package.SaveDirectory = destination;
        SettingsService.Instance.RememberArchivePassword("KnownKey");
        Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package));
        Assert.Equal("first payload", File.ReadAllText(Path.Combine(destination, "first.txt")));
        Assert.Equal(1, SettingsService.Instance.GetArchivePasswordEntries().Single(entry => entry.Password == "WrongKey").UsageCount);
    }

    [Fact]
    public async Task AcceptedSavedPassword_IsPrioritizedForOtherArchivesInThePackage()
    {
        var package = CreatePasswordWorkflowPackage(("first", "KnownKey"), ("second", "KnownKey"));
        SettingsService.Instance.ReplaceArchivePasswords(new[] { "WrongKey", "KnownKey" });
        Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package));
        var entries = SettingsService.Instance.GetArchivePasswordEntries();
        Assert.Equal(1, entries.Single(entry => entry.Password == "WrongKey").UsageCount);
        Assert.Equal(2, entries.Single(entry => entry.Password == "KnownKey").SuccessCount);
    }

    [Fact]
    public async Task RepairedSource_InvalidationAllowsPreviouslyUnconfirmedPasswordToBeRetried()
    {
        var package = CreatePasswordWorkflowPackage(("first", "KnownKey"));
        SettingsService.Instance.ReplaceArchivePasswords(new[] { "WrongKey" });
        Assert.False(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package));
        string path = package.Items.Single().SaveFilePath!;
        long length = new FileInfo(path).Length;
        DateTime timestamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllBytes(path, CreateEncryptedZip("WrongKey", "first.txt", "first payload"));
        File.SetLastWriteTimeUtc(path, timestamp);
        Assert.Equal(length, new FileInfo(path).Length);
        ArchiveExtractionService.Instance.InvalidatePackageArchivePasswords(package);
        Assert.True(ArchiveExtractionService.Instance.CanRetryPackagePasswords(package));
        Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package));
        Assert.Equal("first payload", File.ReadAllText(Path.Combine(_tempDirectory, "first.txt")));
        Assert.Equal(2, Assert.Single(SettingsService.Instance.GetArchivePasswordEntries()).UsageCount);
    }

    [Fact]
    public async Task ArchivesWithEqualNamesInDifferentDirectories_HaveIndependentPasswords()
    {
        var package = CreatePasswordWorkflowPackage();
        SettingsService.Instance.ReplaceArchivePasswords(Array.Empty<string>());
        foreach (string directory in new[] { "left", "right" })
        {
            string parent = Path.Combine(_tempDirectory, directory);
            Directory.CreateDirectory(parent);
            string path = Path.Combine(parent, "same.zip");
            string password = directory + " key";
            File.WriteAllBytes(path, CreateEncryptedZip(password, directory + ".txt", directory + " payload"));
            package.Items.Add(CompletedArchiveItem(path));
            ArchiveExtractionService.Instance.AddPackageArchivePassword(package, Path.Combine(directory, "same.zip"), password, remember: false);
        }
        ArchiveExtractionService.Instance.InvalidatePackageArchivePasswords(package);
        Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package));
        foreach (string directory in new[] { "left", "right" })
        {
            Assert.Equal(directory + " payload", File.ReadAllText(Path.Combine(_tempDirectory, directory + ".txt")));
            var row = Assert.Single(ArchiveExtractionService.Instance.GetPackagePasswordCandidates(package, Path.Combine(directory, "same.zip")));
            Assert.Equal(directory + " key", row.Password);
            Assert.Equal(ArchivePasswordOutcome.Verified, row.Outcome);
        }
    }

    private DownloadPackage CreatePasswordWorkflowPackage(params (string Name, string Password)[] archives)
    {
        var package = new DownloadPackage
        {
            Name = "Sample package", SaveDirectory = _tempDirectory, AutoExtractArchives = true,
            AutoPar2Repair = false, IsEnabled = true
        };
        foreach (var (name, password) in archives)
        {
            string path = Path.Combine(_tempDirectory, name + ".zip");
            File.WriteAllBytes(path, CreateEncryptedZip(password, name + ".txt", name + " payload"));
            package.Items.Add(CompletedArchiveItem(path));
        }
        return package;
    }

    private static DownloadItem CompletedArchiveItem(string path) => new()
    {
        FileName = Path.GetFileName(path), SaveFilePath = path, Status = DownloadStatus.Completed, IsEnabled = true
    };
}
