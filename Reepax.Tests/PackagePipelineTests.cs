using System.IO;
using System.IO.Compression;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Extractor;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;
using Reepax.Services.Verification;
using Xunit;

namespace Reepax.Tests;

public sealed class PackagePipelineTests
{
    [Fact]
    public async Task LocalDownload_PlainFilesCompleteWithoutExtractionAndReleaseGlobalAction()
    {
        byte[] data = [1, 3, 5, 7];
        await using var fixture = await PackagePipelineFixture.CreateForFilesAsync(new Dictionary<string, byte[]>
        {
            ["first.bin"] = data,
            ["second.txt"] = data
        });
        var package = fixture.Package;
        package.DeleteArchiveAfterExtraction = true;
        var alreadyComplete = new DownloadPackage { HasCompletedNotified = true };
        alreadyComplete.Items.Add(new DownloadItem { FileName = "other.txt", Status = DownloadStatus.Completed });
        QueueManager.Instance.Packages.Add(alreadyComplete);

        await fixture.DownloadFileAsync(package.Items[0]);
        Assert.False(package.IsFullyCompleted);
        Assert.Equal(0, fixture.NotificationCount);
        Assert.Equal(0, fixture.CountdownCount);
        await fixture.DownloadFileAsync(package.Items[1]);

        Assert.True(package.AreDownloadsCompleted);
        Assert.True(package.IsFullyCompleted);
        Assert.False(package.IsExtracted);
        Assert.False(package.HasNextTasks);
        Assert.Empty(package.NextTaskSteps);
        Assert.Equal(Loc.Get("Status_Completed"), package.StatusMessage);
        Assert.False(fixture.HasPendingExtraction);
        foreach (var item in package.Items)
        {
            Assert.Equal(data, File.ReadAllBytes(item.SaveFilePath!));
            Assert.Equal(item.ExpectedChecksum, item.CalculatedChecksum, ignoreCase: true);
        }
        await fixture.CountdownStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        QueueManager.Instance.NotifyPackageCompletionIfEligible(package);
        fixture.EvaluateCompletion();
        Assert.Equal(1, fixture.NotificationCount);
        Assert.Equal(1, fixture.CountdownCount);
        Assert.Empty(fixture.EarlyCompletion);
        Assert.False(PostDownloadActionService.Instance.IsCountdownActive);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LocalDownload_UsesFinalArchiveNameOrDirectoryCompanion(bool responseNamesArchive)
    {
        byte[] payload = [1, 3, 5, 7];
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = archive.CreateEntry("result.bin").Open())
            entry.Write(payload);
        byte[] zip = memory.ToArray();
        await using var fixture = await PackagePipelineFixture.CreateForFilesAsync(
            new Dictionary<string, byte[]> { ["download.bin"] = responseNamesArchive ? zip : payload },
            responseNamesArchive ? new Dictionary<string, string> { ["Content-Disposition"] = "attachment; filename=example.zip" } : null);
        string archivePath = Path.Combine(fixture.Package.SaveDirectory, "example.zip");
        if (!responseNamesArchive)
            File.WriteAllBytes(archivePath, zip);

        var item = Assert.Single(fixture.Package.Items);
        await fixture.DownloadFileAsync(item);
        Assert.Equal(responseNamesArchive ? "example.zip" : "download.bin", item.FileName);
        Assert.True(await (await fixture.NextExtractionAsync()).WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.True(fixture.Package.IsFullyCompleted);
        Assert.True(fixture.Package.IsExtracted);
        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(fixture.Package.SaveDirectory, "result.bin")));
        Assert.Equal(zip, File.ReadAllBytes(archivePath));
        Assert.Equal(1, fixture.NotificationCount);
    }

    [Fact]
    public async Task QueueRestart_ExtractsCompanionAddedAfterPlainFileCompleted()
    {
        byte[] payload = [2, 4, 6, 8];
        await using var fixture = await PackagePipelineFixture.CreateForFilesAsync(
            new Dictionary<string, byte[]> { ["download.bin"] = payload });
        var package = fixture.Package;
        await fixture.DownloadFileAsync(Assert.Single(package.Items));
        Assert.True(package.IsFullyCompleted);
        Assert.Empty(package.NextTaskSteps);

        var queue = QueueManager.Instance;
        queue.StopQueue();
        using (var archive = ZipFile.Open(Path.Combine(package.SaveDirectory, "companion.zip"), ZipArchiveMode.Create))
        using (var output = archive.CreateEntry("companion.txt").Open())
            output.Write(payload);

        queue.StartQueue();
        Assert.True(await (await fixture.NextExtractionAsync()).WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.True(package.IsExtracted);
        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(package.SaveDirectory, "companion.txt")));
        Assert.Equal(1, fixture.NotificationCount);
    }

    [Theory]
    [InlineData("plain.txt", false)]
    [InlineData("missing.zip", true)]
    [InlineData("example.7z.002", true)]
    [InlineData("empty.zip", true)]
    public void Reload_LegacyPendingExtractionIsKeptOnlyForExpectedArchives(string fileName, bool expectsExtraction)
    {
        string directory = Directory.CreateTempSubdirectory("ExtractionReload_").FullName;
        try
        {
            using var storage = new DownloadPersistenceService(Path.Combine(directory, "downloads.json"));
            storage.SaveDownloadsFromDtos([new DownloadPackageDto
            {
                Id = Guid.NewGuid(), Name = "Example package", SaveDirectory = directory, IsCustomName = true,
                AutoExtractArchives = true, DeleteArchiveAfterExtraction = true, Status = DownloadStatus.Completed,
                NextTaskSteps = [new() { Key = "Extract" }, new() { Key = "Cleanup" }],
                Items = [new() { FileName = fileName, SaveFilePath = Path.Combine(directory, fileName),
                    IsEnabled = true, Status = DownloadStatus.Completed, TotalBytes = 4, DownloadedBytes = 4 }]
            }], sync: true);
            if (fileName.EndsWith(".002", StringComparison.Ordinal))
                File.WriteAllBytes(Path.Combine(directory, fileName), [1, 3, 5, 7]);
            if (fileName == "empty.zip")
                File.WriteAllBytes(Path.Combine(directory, fileName), []);

            var package = Assert.Single(storage.LoadDownloads());
            Assert.Equal(!expectsExtraction, package.IsFullyCompleted);
            Assert.Equal(!expectsExtraction, package.HasCompletedNotified);
            Assert.False(package.IsNewlyCompleted);
            Assert.False(package.IsExtracted);
            Assert.Equal(expectsExtraction, package.NextTaskSteps.Any(step => step.Key == "Extract" && step.State == NextTaskStepState.Pending));
            Assert.Equal(expectsExtraction, PostDownloadActionService.CheckHasActiveWork([package]));
            Assert.False(ArchiveExtractionService.Instance.CanExtractPackage(package));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("existing", "intact")]
    [InlineData("existing", "repairable")]
    [InlineData("imported", "intact")]
    [InlineData("imported", "repairable")]
    [InlineData("new", "intact")]
    [InlineData("new", "repairable")]
    public async Task LocalDownload_UsesPackagePar2OptionWhenGlobalDefaultIsDisabled(string origin, string scenario)
    {
        var settings = SettingsService.Instance.Settings;
        await using var fixture = await PackagePipelineFixture.CreateAsync(scenario, createPackage: directory =>
        {
            if (origin == "existing")
            {
                var existing = PackageGrouper.CreatePackage("Example package", [], directory, autoExtractArchives: true);
                settings.AutoPar2Repair = false;
                return existing;
            }

            settings.AutoPar2Repair = false;
            if (origin == "new")
                return PackageGrouper.CreatePackage("Example package", [], directory, autoExtractArchives: true);

            string exported = PackageExportImportService.ExportToJson(new DownloadPackage
            {
                Name = "Example package", AutoExtractArchives = true, AutoPar2Repair = true
            });
            return PackageExportImportService.ImportFromJson(exported);
        });

        bool repairEnabled = origin != "new";
        bool shouldExtract = repairEnabled || scenario == "intact";
        Assert.False(settings.AutoPar2Repair);
        Assert.Equal(repairEnabled, fixture.Package.AutoPar2Repair);
        await fixture.DownloadRepairFilesAsync();
        await fixture.DownloadArchiveAsync();
        Assert.Equal(shouldExtract, await (await fixture.NextExtractionAsync()).WaitAsync(TimeSpan.FromSeconds(30)));
        if (repairEnabled)
            Assert.Equal(NextTaskStepState.Done, Assert.Single(fixture.Package.NextTaskSteps, step => step.Key == "Par2").State);
        else
            Assert.DoesNotContain(fixture.Package.NextTaskSteps, step => step.Key == "Par2");

        Assert.Equal(repairEnabled ? fixture.OriginalArchive : fixture.ServedArchive, File.ReadAllBytes(fixture.Archive.SaveFilePath!));
        Assert.Equal(shouldExtract ? fixture.Payload : fixture.PreviousOutput, File.ReadAllBytes(fixture.OutputPath));
        Assert.Equal(shouldExtract, fixture.Package.IsFullyCompleted);
        Assert.Equal(shouldExtract, fixture.Package.HasCompletedNotified);
        if (shouldExtract)
        {
            await fixture.CountdownStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.All(fixture.Package.NextTaskSteps, step => Assert.Equal(NextTaskStepState.Done, step.State));
            Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(fixture.Package));
            QueueManager.Instance.NotifyPackageCompletionIfEligible(fixture.Package);
        }
        fixture.EvaluateCompletion();
        Assert.Equal(shouldExtract ? 1 : 0, fixture.NotificationCount);
        Assert.Equal(shouldExtract ? 1 : 0, fixture.CountdownCount);
        Assert.False(PostDownloadActionService.Instance.IsCountdownActive);
        Assert.Empty(fixture.EarlyCompletion);
        Assert.Equal(1, fixture.Source.Requests["archive.zip"]);
        fixture.AssertUnaffectedFiles();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviouslySkippedPar2_RemainsPendingAfterReloadUntilExplicitReextraction(bool history)
    {
        await using var fixture = await PackagePipelineFixture.CreateAsync("intact");
        SettingsService.Instance.Settings.AutoPar2Repair = false;
        fixture.AutoStartExtraction = false;
        await fixture.DownloadRepairFilesAsync();
        await fixture.DownloadArchiveAsync();

        // Recreate the state saved by the old extraction path.
        File.WriteAllBytes(fixture.OutputPath, fixture.Payload);
        fixture.Package.SetNextTaskDone("Extract");
        fixture.Package.StatusMessage = Loc.Get("Status_CompletedAndExtracted");
        Assert.Equal(NextTaskStepState.Pending, Assert.Single(fixture.Package.NextTaskSteps, step => step.Key == "Par2").State);
        using var storage = new DownloadPersistenceService(Path.Combine(fixture.Package.SaveDirectory, "downloads.json"));
        if (history) storage.SaveHistory([fixture.Package], sync: true);
        else storage.SaveDownloads([fixture.Package], sync: true);
        File.WriteAllBytes(fixture.OutputPath, fixture.PreviousOutput);
        var restored = Assert.Single(history ? storage.LoadHistory() : storage.LoadDownloads());

        Assert.True(restored.IsExtracted);
        Assert.True(restored.AutoPar2Repair);
        Assert.False(restored.IsFullyCompleted);
        Assert.Equal(NextTaskStepState.Pending, Assert.Single(restored.NextTaskSteps, step => step.Key == "Par2").State);
        Assert.Equal(NextTaskStepState.Done, Assert.Single(restored.NextTaskSteps, step => step.Key == "Extract").State);
        Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(restored));
        Assert.Equal(fixture.PreviousOutput, File.ReadAllBytes(fixture.OutputPath));
        Assert.False(restored.IsFullyCompleted);
        Assert.Equal(0, fixture.CountdownCount);

        Assert.True(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(restored, force: true, forceReextract: true));
        Assert.True(restored.IsFullyCompleted);
        Assert.All(restored.NextTaskSteps, step => Assert.Equal(NextTaskStepState.Done, step.State));
        Assert.Equal(fixture.Payload, File.ReadAllBytes(fixture.OutputPath));
        Assert.Equal(fixture.OriginalArchive, File.ReadAllBytes(fixture.Archive.SaveFilePath!));
        fixture.AssertUnaffectedFiles();
    }

    [Fact]
    public async Task LocalDownload_FailedPublicationBlocksCompletionUntilSuccessfulRetry()
    {
        await using var fixture = await PackagePipelineFixture.CreateAsync("intact");
        fixture.Package.AutoPar2Repair = false;
        fixture.Package.EnsureNextTaskSteps();
        await fixture.DownloadRepairFilesAsync();
        using (var lockedTarget = new FileStream(fixture.OutputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await fixture.DownloadArchiveAsync();
            Assert.False(await (await fixture.NextExtractionAsync()).WaitAsync(TimeSpan.FromSeconds(30)));
            fixture.EvaluateCompletion();
            Assert.Equal(fixture.OriginalArchive, File.ReadAllBytes(fixture.Archive.SaveFilePath!));
            Assert.Equal(PackagePipelineFixture.Hash(fixture.OriginalArchive), fixture.Archive.CalculatedChecksum);
            Assert.Equal(DownloadStatus.Completed, fixture.Package.Status);
            Assert.Contains(fixture.Package.NextTaskSteps, step => step.Key == "Extract" && step.State == NextTaskStepState.Failed);
            Assert.False(fixture.Package.IsFullyCompleted);
            Assert.False(fixture.Package.HasCompletedNotified);
            Assert.Equal(fixture.PreviousOutput, File.ReadAllBytes(fixture.OutputPath));
            Assert.Equal(0, fixture.NotificationCount);
            Assert.Equal(0, fixture.CountdownCount);
            Assert.Equal(PostDownloadAction.Sleep, PostDownloadActionService.Instance.CurrentAction);
            fixture.AssertUnaffectedFiles();
        }

        Assert.True(await fixture.RetryExtractionAsync().WaitAsync(TimeSpan.FromSeconds(30)));
        await fixture.CountdownStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(fixture.Payload, File.ReadAllBytes(fixture.OutputPath));
        Assert.Equal(fixture.OriginalArchive, File.ReadAllBytes(fixture.Archive.SaveFilePath!));
        Assert.True(fixture.Package.IsFullyCompleted);
        Assert.All(fixture.Package.NextTaskSteps, step => Assert.Equal(NextTaskStepState.Done, step.State));
        Assert.Equal(1, fixture.NotificationCount);
        Assert.Equal(1, fixture.CountdownCount);
        Assert.Empty(fixture.EarlyCompletion);
        Assert.Equal(1, fixture.Source.Requests["archive.zip"]);
        fixture.AssertUnaffectedFiles();
        Assert.Empty(Directory.GetDirectories(fixture.Package.SaveDirectory, ".archive-work-*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("intact")]
    [InlineData("repairable")]
    [InlineData("unrepairable")]
    [InlineData("checksum-rejected")]
    public async Task LocalDownload_ValidatesRepairsAndPublishesOnlySuccessfulPackages(string scenario)
    {
        await using var fixture = await PackagePipelineFixture.CreateAsync(scenario);
        await fixture.DownloadRepairFilesAsync();
        await fixture.DownloadArchiveAsync();

        if (scenario == "checksum-rejected")
        {
            Assert.Equal(DownloadStatus.Failed, fixture.Archive.Status);
            Assert.False(File.Exists(fixture.Archive.SaveFilePath));
            Assert.Equal(fixture.ServedArchive, File.ReadAllBytes(fixture.Archive.SaveFilePath + ".part"));
            Assert.False(fixture.HasPendingExtraction);
            Assert.DoesNotContain(fixture.Package.NextTaskSteps, step => step.State == NextTaskStepState.Done);
        }
        else
        {
            Task<bool> extraction = await fixture.NextExtractionAsync();
            bool extracted = await extraction.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(scenario != "unrepairable", extracted);
            if (scenario == "unrepairable")
            {
                Assert.Equal(fixture.ServedArchive, File.ReadAllBytes(fixture.Archive.SaveFilePath!));
                Assert.Contains(fixture.Package.NextTaskSteps, step => step.Key == "Par2" && step.State == NextTaskStepState.Failed);
                string parity = Path.Combine(fixture.Package.SaveDirectory, "archive.par2");
                var verification = await Par2RepairService.Instance.VerifyAsync(parity);
                Assert.Equal(Par2VerificationStatus.RepairNotPossible, verification.Status);
                Assert.True(verification.NeededRecoveryBlocks > 0);
            }
            else
            {
                await fixture.CountdownStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(fixture.OriginalArchive, File.ReadAllBytes(fixture.Archive.SaveFilePath!));
                Assert.Equal(fixture.Payload, File.ReadAllBytes(fixture.OutputPath));
                Assert.True(fixture.Package.IsFullyCompleted);
                Assert.True(fixture.Package.IsExtracted);
                Assert.All(fixture.Package.NextTaskSteps, step => Assert.Equal(NextTaskStepState.Done, step.State));
                Assert.Equal(1, fixture.NotificationCount);
                Assert.Equal(1, fixture.CountdownCount);
                Assert.Empty(fixture.EarlyCompletion);
                Assert.True(string.IsNullOrEmpty(fixture.Archive.CalculatedChecksum) ||
                    fixture.Archive.CalculatedChecksum == PackagePipelineFixture.Hash(fixture.OriginalArchive),
                    "A repaired file must not retain a fingerprint of discarded bytes.");
            }
        }

        if (scenario is "unrepairable" or "checksum-rejected")
        {
            fixture.EvaluateCompletion();
            Assert.False(fixture.Package.IsFullyCompleted);
            Assert.False(fixture.Package.HasCompletedNotified);
            Assert.False(fixture.Package.IsExtracted);
            Assert.Equal(fixture.PreviousOutput, File.ReadAllBytes(fixture.OutputPath));
            Assert.Equal(0, fixture.NotificationCount);
            Assert.Equal(0, fixture.CountdownCount);
            Assert.Empty(fixture.EarlyCompletion);
        }
        Assert.Equal(1, fixture.Source.Requests["archive.zip"]);
        fixture.AssertUnaffectedFiles();
        Assert.Empty(Directory.GetDirectories(fixture.Package.SaveDirectory, ".archive-work-*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalDownload_WaitsForPasswordAndAutomaticallyContinuesAfterItIsSaved(bool saveBeforeAttemptFinishes)
    {
        await using var fixture = await PackagePipelineFixture.CreateAsync("password", encrypted: true);
        SettingsService.Instance.ReplaceArchivePasswords(["Incorrect example key"]);
        bool observedWait = false;
        void ObserveWait(object? sender, System.ComponentModel.PropertyChangedEventArgs change)
        {
            if (change.PropertyName != nameof(DownloadPackage.RequiresArchivePassword) || !fixture.Package.RequiresArchivePassword) return;
            observedWait = !fixture.Package.IsFullyCompleted && !fixture.Package.HasCompletedNotified &&
                fixture.CountdownCount == 0 && fixture.PreviousOutput.SequenceEqual(File.ReadAllBytes(fixture.OutputPath));
            if (saveBeforeAttemptFinishes)
                SettingsService.Instance.RememberArchivePassword(PackagePipelineFixture.Password);
        }
        fixture.Package.PropertyChanged += ObserveWait;
        await fixture.DownloadRepairFilesAsync();
        await fixture.DownloadArchiveAsync();
        Assert.False(await (await fixture.NextExtractionAsync()).WaitAsync(TimeSpan.FromSeconds(30)));
        fixture.Package.PropertyChanged -= ObserveWait;
        Assert.True(observedWait);
        Assert.Equal(fixture.OriginalArchive, File.ReadAllBytes(fixture.Archive.SaveFilePath!));

        if (!saveBeforeAttemptFinishes)
        {
            Assert.True(fixture.Package.RequiresArchivePassword);
            Assert.Equal(0, fixture.CountdownCount);
            SettingsService.Instance.RememberArchivePassword(PackagePipelineFixture.Password);
        }
        Assert.True(await (await fixture.NextExtractionAsync()).WaitAsync(TimeSpan.FromSeconds(30)));
        await fixture.CountdownStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(fixture.Package.RequiresArchivePassword);
        Assert.True(fixture.Package.IsFullyCompleted);
        Assert.Equal(fixture.Payload, File.ReadAllBytes(fixture.OutputPath));
        Assert.Equal(fixture.OriginalArchive, File.ReadAllBytes(fixture.Archive.SaveFilePath!));
        Assert.Equal(1, fixture.Source.Requests["archive.zip"]);
        Assert.Equal(1, fixture.NotificationCount);
        Assert.Equal(1, fixture.CountdownCount);
        Assert.Empty(fixture.EarlyCompletion);
        fixture.AssertUnaffectedFiles();
        var saved = SettingsService.Instance.GetArchivePasswordEntries();
        Assert.Equal(1, saved.Single(entry => entry.Password == "Incorrect example key").UsageCount);
        Assert.Equal(0, saved.Single(entry => entry.Password == "Incorrect example key").SuccessCount);
        Assert.Equal(1, saved.Single(entry => entry.Password == PackagePipelineFixture.Password).SuccessCount);
    }
}
