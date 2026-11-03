using System.IO;
using Reepax.Models;
using Reepax.Services.Extractor;
using Reepax.Services.Storage;
using Reepax.Services.Verification;
using Xunit;

namespace Reepax.Tests;

public sealed class PackagePipelineTests
{
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
