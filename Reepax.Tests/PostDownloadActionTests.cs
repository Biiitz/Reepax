using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;
using Reepax.Services.Verification;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

[Collection("PostDownloadActionTests")]
public class PostDownloadActionTests : IDisposable
{
    public PostDownloadActionTests()
    {
        // Ensure test environment is flagged
        DownloadPersistenceService.IsTestEnvironment = true;
        PostDownloadActionService.Instance.CancelCountdown(manual: true);
        PostDownloadActionService.Instance.CurrentAction = PostDownloadAction.None;
    }

    public void Dispose()
    {
        PostDownloadActionService.Instance.CancelCountdown(manual: true);
        PostDownloadActionService.Instance.CurrentAction = PostDownloadAction.None;
    }

    [Fact]
    public void AppSettings_PostDownloadAction_DefaultValue_IsNone()
    {
        var settings = new AppSettings();
        Assert.Equal(PostDownloadAction.None, settings.PostDownloadAction);
    }

    [Fact]
    public void AppSettings_PostDownloadAction_JsonRoundTrip()
    {
        var original = new AppSettings
        {
            PostDownloadAction = PostDownloadAction.Shutdown
        };

        string json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(PostDownloadAction.Shutdown, deserialized.PostDownloadAction);
    }

    [Fact]
    public void PostDownloadAction_CheckHasActiveWork_RunningOrQueuedItems_ReturnsTrue()
    {
        var pkg = new DownloadPackage
        {
            Id = Guid.NewGuid(),
            Name = "Test Package",
            Status = DownloadStatus.Downloading,
            IsEnabled = true
        };
        var item = new DownloadItem
        {
            Id = Guid.NewGuid(),
            FileName = "file.part1.rar",
            Status = DownloadStatus.Downloading,
            IsEnabled = true
        };
        pkg.Items.Add(item);

        var packages = new List<DownloadPackage> { pkg };
        bool hasActive = PostDownloadActionService.CheckHasActiveWork(packages);

        Assert.True(hasActive);
    }

    [Fact]
    public void PostDownloadAction_CheckHasActiveWork_PausedItems_ReturnsFalse()
    {
        // User scenario: e.g. 2 downloads, 1 running and 1 paused.
        // Once the running one completes, only the paused one remains.
        // Paused items must NOT be counted as active work!
        var pkgCompleted = new DownloadPackage
        {
            Id = Guid.NewGuid(),
            Name = "Finished Package",
            Status = DownloadStatus.Completed,
            IsEnabled = true
        };
        var itemCompleted = new DownloadItem
        {
            Id = Guid.NewGuid(),
            FileName = "data.iso",
            Status = DownloadStatus.Completed,
            IsEnabled = true,
            TotalBytes = 1000,
            DownloadedBytes = 1000
        };
        pkgCompleted.Items.Add(itemCompleted);

        var pkgPaused = new DownloadPackage
        {
            Id = Guid.NewGuid(),
            Name = "Paused Package",
            Status = DownloadStatus.Paused,
            IsEnabled = true
        };
        var itemPaused = new DownloadItem
        {
            Id = Guid.NewGuid(),
            FileName = "other.iso",
            Status = DownloadStatus.Paused,
            IsEnabled = true
        };
        pkgPaused.Items.Add(itemPaused);

        var packages = new List<DownloadPackage> { pkgCompleted, pkgPaused };
        bool hasActive = PostDownloadActionService.CheckHasActiveWork(packages);

        Assert.False(hasActive);
    }

    [Fact]
    public void PostDownloadAction_CheckHasActiveWork_ExtractingPackage_ReturnsTrue()
    {
        var pkg = new DownloadPackage
        {
            Id = Guid.NewGuid(),
            Name = "Extracting Package",
            Status = DownloadStatus.Completed,
            IsExtracting = true,
            IsEnabled = true
        };
        var item = new DownloadItem
        {
            Id = Guid.NewGuid(),
            FileName = "data.part1.rar",
            Status = DownloadStatus.Completed,
            IsEnabled = true
        };
        pkg.Items.Add(item);

        var packages = new List<DownloadPackage> { pkg };
        bool hasActive = PostDownloadActionService.CheckHasActiveWork(packages);

        Assert.True(hasActive);
    }

    [Fact]
    public void PostDownloadAction_CheckHasActiveWork_CompletedPackage_ReturnsFalse()
    {
        var pkg = new DownloadPackage
        {
            Id = Guid.NewGuid(),
            Name = "Completed Package",
            Status = DownloadStatus.Completed,
            IsEnabled = true
        };
        var item = new DownloadItem
        {
            Id = Guid.NewGuid(),
            FileName = "movie.mkv",
            Status = DownloadStatus.Completed,
            IsEnabled = true,
            TotalBytes = 2000,
            DownloadedBytes = 2000
        };
        pkg.Items.Add(item);

        var packages = new List<DownloadPackage> { pkg };
        bool hasActive = PostDownloadActionService.CheckHasActiveWork(packages);

        Assert.False(hasActive);
    }

    [Fact]
    public void PostDownloadAction_StartCountdown_SetsIsCountdownActiveAnd10Seconds()
    {
        var service = PostDownloadActionService.Instance;
        service.CurrentAction = PostDownloadAction.Shutdown;

        service.StartCountdown();

        Assert.True(service.IsCountdownActive);
        Assert.Equal(10, service.RemainingSeconds);

        service.CancelCountdown(manual: true);
        Assert.False(service.IsCountdownActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Countdown_RestartsAfterTheLastCleanupAndAnyNewWorkFinishes(bool addWork)
    {
        var allowTicks = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<PostDownloadAction>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new PostDownloadActionService(token => allowTicks.Task.WaitAsync(token));
        var packages = QueueManager.Instance.Packages;
        DownloadPackage[] previousPackages = [];
        void Finished()
        {
            var action = service.CurrentAction;
            // Intercept the selected action at the countdown boundary.
            service.CurrentAction = PostDownloadAction.None;
            finished.TrySetResult(action);
        }

        try
        {
            await OnUiAsync(() =>
            {
                previousPackages = packages.ToArray();
                packages.Clear();
                packages.Add(CreateCompletedPackage());
                service.CountdownFinished += Finished;
                service.CurrentAction = PostDownloadAction.Sleep;
                service.NotifyWorkStarted();
                service.Evaluate(packages);
                Assert.True(service.IsCountdownActive);

                using var first = service.SuspendCountdown();
                using var last = service.SuspendCountdown();
                Assert.False(service.IsCountdownActive);
                Assert.Equal(PostDownloadAction.Sleep, service.CurrentAction);
                service.StartCountdown();
                Assert.False(service.IsCountdownActive);

                var newWork = CreateCompletedPackage();
                if (addWork)
                {
                    newWork.Items[0].Status = DownloadStatus.Queued;
                    packages.Add(newWork);
                }

                first.Dispose();
                service.Evaluate(packages);
                Assert.False(service.IsCountdownActive);
                Assert.False(finished.Task.IsCompleted);
                last.Dispose();

                if (addWork)
                {
                    Assert.False(service.IsCountdownActive);
                    newWork.Items[0].Status = DownloadStatus.Completed;
                    service.Evaluate(packages);
                }

                Assert.True(service.IsCountdownActive);
                Assert.Equal(10, service.RemainingSeconds);
                Assert.False(finished.Task.IsCompleted);
            });

            allowTicks.SetResult();
            Assert.Equal(PostDownloadAction.Sleep, await finished.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(service.IsCountdownActive);
            Assert.Equal(PostDownloadAction.None, service.CurrentAction);
        }
        finally
        {
            await OnUiAsync(() =>
            {
                service.CancelCountdown();
                service.CurrentAction = PostDownloadAction.None;
                service.CountdownFinished -= Finished;
                packages.Clear();
                foreach (var previous in previousPackages) packages.Add(previous);
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Countdown_ExplicitCancellationDuringCleanupClearsThePendingRestart(bool deselectAction)
    {
        await OnUiAsync(() =>
        {
            var service = new PostDownloadActionService(Timeout.InfiniteTimeSpan);
            var packages = QueueManager.Instance.Packages;
            var previousPackages = packages.ToArray();
            try
            {
                packages.Clear();
                packages.Add(CreateCompletedPackage());
                service.CurrentAction = PostDownloadAction.Sleep;
                service.NotifyWorkStarted();
                service.Evaluate(packages);
                Assert.True(service.IsCountdownActive);

                using (service.SuspendCountdown())
                {
                    if (deselectAction) service.CurrentAction = PostDownloadAction.None;
                    else service.CancelCountdown();
                    Assert.Equal(PostDownloadAction.None, service.CurrentAction);
                    service.CurrentAction = PostDownloadAction.Sleep;
                }

                service.Evaluate(packages);
                Assert.False(service.IsCountdownActive);
            }
            finally
            {
                service.CancelCountdown();
                service.CurrentAction = PostDownloadAction.None;
                packages.Clear();
                foreach (var previous in previousPackages) packages.Add(previous);
            }
        });
    }

    [Fact]
    public void PostDownloadAction_CancelCountdown_Manual_ResetsActionToNone()
    {
        var service = PostDownloadActionService.Instance;
        service.CurrentAction = PostDownloadAction.Sleep;
        service.StartCountdown();

        Assert.True(service.IsCountdownActive);

        // User clicks "Abbrechen"
        service.CancelCountdown(manual: true);

        Assert.False(service.IsCountdownActive);
        Assert.Equal(PostDownloadAction.None, service.CurrentAction);
    }

    [Fact]
    public void PostDownloadAction_CancelCountdown_Automatic_PreservesAction()
    {
        var service = PostDownloadActionService.Instance;
        service.CurrentAction = PostDownloadAction.ExitApp;
        service.StartCountdown();

        Assert.True(service.IsCountdownActive);

        // A new download starts or is resumed while countdown is running
        service.CancelCountdown(manual: false);

        Assert.False(service.IsCountdownActive);
        // Action remains armed for when new work finishes!
        Assert.Equal(PostDownloadAction.ExitApp, service.CurrentAction);
    }

    [Fact]
    public void PostDownloadAction_Evaluate_TriggersCountdownWhenAllActiveWorkFinished()
    {
        var service = PostDownloadActionService.Instance;
        service.CurrentAction = PostDownloadAction.Shutdown;

        var pkg = new DownloadPackage
        {
            Id = Guid.NewGuid(),
            Name = "Eval Package",
            Status = DownloadStatus.Downloading,
            IsEnabled = true
        };
        var item = new DownloadItem
        {
            Id = Guid.NewGuid(),
            FileName = "file.zip",
            Status = DownloadStatus.Downloading,
            IsEnabled = true
        };
        pkg.Items.Add(item);

        var packages = new List<DownloadPackage> { pkg };

        // 1. While running, evaluate records active work
        service.Evaluate(packages);
        Assert.False(service.IsCountdownActive);

        // 2. Package finishes 100%
        item.Status = DownloadStatus.Completed;
        item.TotalBytes = 5000;
        item.DownloadedBytes = 5000;
        pkg.Status = DownloadStatus.Completed;

        // 3. Evaluate after completion starts the 10-second countdown!
        service.Evaluate(packages);

        Assert.True(service.IsCountdownActive);
        Assert.Equal(10, service.RemainingSeconds);

        service.CancelCountdown(manual: true);
    }

    [Fact]
    public void PostDownloadAction_Evaluate_DoesNotTriggerWhenActionIsNone()
    {
        var service = PostDownloadActionService.Instance;
        service.CurrentAction = PostDownloadAction.None;
        service.NotifyWorkStarted();

        var pkg = new DownloadPackage
        {
            Id = Guid.NewGuid(),
            Name = "None Package",
            Status = DownloadStatus.Completed,
            IsEnabled = true
        };
        var item = new DownloadItem
        {
            Id = Guid.NewGuid(),
            FileName = "file.zip",
            Status = DownloadStatus.Completed,
            IsEnabled = true,
            TotalBytes = 100,
            DownloadedBytes = 100
        };
        pkg.Items.Add(item);

        service.Evaluate(new List<DownloadPackage> { pkg });

        Assert.False(service.IsCountdownActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Evaluate_CompletedPackageDoesNotHideAnotherPackageFailure(bool processingFailure)
    {
        var service = PostDownloadActionService.Instance;
        var failed = CreateCompletedPackage();
        if (processingFailure) failed.SetNextTaskFailed("Extract");
        else
        {
            failed.AutoExtractArchives = false;
            failed.Items[0].Status = DownloadStatus.Failed;
            failed.Status = DownloadStatus.Failed;
        }
        service.CurrentAction = PostDownloadAction.Sleep;
        service.NotifyWorkStarted();

        service.Evaluate(new[] { CreateCompletedPackage(), failed });

        Assert.False(service.IsCountdownActive);
        Assert.Equal(PostDownloadAction.Sleep, service.CurrentAction);
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("disabled")]
    [InlineData("skipped")]
    public void Evaluate_CompletedWorkStillAllowsPausedOrDisabledPackages(string state)
    {
        var service = PostDownloadActionService.Instance;
        var ignored = CreateCompletedPackage();
        ignored.ResetNextTasks();
        if (state == "disabled") ignored.IsEnabled = false;
        else if (state == "skipped") ignored.Items[0].IsEnabled = false;
        else ignored.Items[0].Status = DownloadStatus.Paused;
        ignored.Status = DownloadStatus.Paused;
        service.CurrentAction = PostDownloadAction.Sleep;
        service.NotifyWorkStarted();

        service.Evaluate(new[] { CreateCompletedPackage(), ignored });

        Assert.True(service.IsCountdownActive);
    }

    [Fact]
    public void Evaluate_FailureCancelsCountdownAndSuccessfulRetryStartsItAgain()
    {
        var service = PostDownloadActionService.Instance;
        var package = CreateCompletedPackage();
        service.CurrentAction = PostDownloadAction.Sleep;
        service.NotifyWorkStarted();
        service.Evaluate(new[] { package });
        Assert.True(service.IsCountdownActive);

        package.SetNextTaskFailed("Extract");
        service.Evaluate(new[] { package });

        Assert.False(service.IsCountdownActive);
        Assert.Equal(PostDownloadAction.Sleep, service.CurrentAction);
        package.SetNextTaskDone("Extract");
        service.Evaluate(new[] { package });
        Assert.True(service.IsCountdownActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Countdown_RechecksCurrentPackagesBeforeFinishingWithoutAnotherEvaluation(bool addFailure)
    {
        var service = new PostDownloadActionService(TimeSpan.FromMilliseconds(20));
        var packages = new List<DownloadPackage> { CreateCompletedPackage() };
        var outcome = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ticks = new List<int>();
        bool reachedBoundary = false;
        void Tick(int seconds)
        {
            ticks.Add(seconds);
            if (seconds != 0) return;
            reachedBoundary = true;
            if (!addFailure) return;
            var failed = CreateCompletedPackage();
            failed.SetNextTaskFailed("Extract");
            packages.Add(failed);
        }
        void Finished()
        {
            // Suppress the action even if the regression causes an incorrect completion.
            service.CurrentAction = PostDownloadAction.None;
            outcome.TrySetResult(true);
        }
        void Cancelled(bool manual) => outcome.TrySetResult(false);
        try
        {
            await OnUiAsync(() =>
            {
                service.CancelCountdown();
                service.CurrentAction = PostDownloadAction.None;
                service.CountdownTick += Tick;
                service.CountdownFinished += Finished;
                service.CountdownCancelled += Cancelled;
                service.CurrentAction = PostDownloadAction.Sleep;
                service.NotifyWorkStarted();
                service.Evaluate(packages);
            });

            Assert.Equal(!addFailure, await outcome.Task.WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.True(reachedBoundary);
            Assert.Equal(Enumerable.Range(0, 11).Reverse(), ticks);
            Assert.Equal(addFailure ? 2 : 1, packages.Count);
            Assert.False(service.IsCountdownActive);
            Assert.Equal(addFailure ? PostDownloadAction.Sleep : PostDownloadAction.None, service.CurrentAction);
        }
        finally
        {
            await OnUiAsync(() =>
            {
                service.CancelCountdown();
                service.CurrentAction = PostDownloadAction.None;
                service.CountdownTick -= Tick;
                service.CountdownFinished -= Finished;
                service.CountdownCancelled -= Cancelled;
            });
        }
    }

    [Theory]
    [InlineData("resume")]
    [InlineData("cancel")]
    [InlineData("deselect")]
    public async Task Countdown_DuringOverlappingPar2Repairs_ResumesOnlyWhenStillArmed(string choice)
    {
        var actions = PostDownloadActionService.Instance;
        var packages = QueueManager.Instance.Packages;
        var package = CreateCompletedPackage();
        package.AutoExtractArchives = false;
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSecond = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousPackages = Array.Empty<DownloadPackage>();
        Task<bool>? first = null;
        Task<bool>? second = null;

        Task<bool> TrackRepair(TaskCompletionSource started, TaskCompletionSource<bool> finish) =>
            Par2RepairService.Instance.RunTrackedPackageOperationAsync(package, () =>
            {
                started.SetResult();
                return finish.Task;
            });

        try
        {
            await OnUiAsync(() =>
            {
                previousPackages = packages.ToArray();
                packages.Clear();
                packages.Add(package);
                actions.CurrentAction = PostDownloadAction.Sleep;
                actions.NotifyWorkStarted();
                actions.Evaluate(packages);
                Assert.True(actions.IsCountdownActive);
            });

            first = TrackRepair(firstStarted, finishFirst);
            second = TrackRepair(secondStarted, finishSecond);
            await Task.WhenAll(firstStarted.Task, secondStarted.Task).WaitAsync(TimeSpan.FromSeconds(10));
            await OnUiAsync(() =>
            {
                Assert.False(actions.IsCountdownActive);
                Assert.Equal(PostDownloadAction.Sleep, actions.CurrentAction);
                actions.StartCountdown();
                actions.Evaluate(packages);
                Assert.False(actions.IsCountdownActive);
            });

            finishFirst.SetCanceled();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(10)));
            await OnUiAsync(() =>
            {
                Assert.True(PostDownloadActionService.CheckHasActiveWork(packages));
                Assert.False(actions.IsCountdownActive);
                if (choice == "resume") return;

                if (choice == "cancel") actions.CancelCountdown();
                else actions.CurrentAction = PostDownloadAction.None;
                Assert.Equal(PostDownloadAction.None, actions.CurrentAction);
                actions.CurrentAction = PostDownloadAction.Sleep;
            });

            finishSecond.SetResult(true);
            Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(10)));
            await OnUiAsync(() =>
            {
                Assert.False(PostDownloadActionService.CheckHasActiveWork(packages));
                Assert.Equal(choice == "resume", actions.IsCountdownActive);
                Assert.Equal(PostDownloadAction.Sleep, actions.CurrentAction);
            });
        }
        finally
        {
            finishFirst.TrySetResult(false);
            finishSecond.TrySetResult(false);
            try
            {
                var pending = new[] { first, second }.OfType<Task>();
                await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException) { }
            finally
            {
                await OnUiAsync(() =>
                {
                    actions.CancelCountdown();
                    actions.CurrentAction = PostDownloadAction.None;
                    packages.Clear();
                    foreach (var previous in previousPackages) packages.Add(previous);
                });
            }
        }
    }

    [Fact]
    public async Task Countdown_RechecksManualPar2BeforeExecutingAction()
    {
        var directory = Directory.CreateTempSubdirectory("PostDownloadPar2_");
        var repairs = Par2RepairService.Instance;
        var previousHook = repairs.ProcessStarted;
        var advance = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verificationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseVerification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstOutcome = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<PostDownloadAction>(TaskCreationOptions.RunContinuationsAsynchronously);
        var actions = new PostDownloadActionService(token => advance.Task.WaitAsync(token));
        Task? repair = null;

        void Finished()
        {
            var chosenAction = actions.CurrentAction;
            actions.CurrentAction = PostDownloadAction.None;
            firstOutcome.TrySetResult(true);
            finished.TrySetResult(chosenAction);
        }

        void Cancelled(bool manual)
        {
            if (!manual) firstOutcome.TrySetResult(false);
        }

        try
        {
            byte[] original = Enumerable.Range(0, 80).Select(i => (byte)(i * 3)).ToArray();
            byte[] damaged = original.ToArray();
            damaged[0] ^= 1;
            byte[] independent = [9, 8, 7, 6];
            string payload = Path.Combine(directory.FullName, "payload.bin");
            string unrelated = Path.Combine(directory.FullName, "unrelated.bin");
            File.WriteAllBytes(payload, original);
            string parity = await Par2RepairServiceTests.CreateParityAsync(directory.FullName, payload);
            File.WriteAllBytes(payload, damaged);
            File.WriteAllBytes(payload + ".1", independent);
            File.WriteAllBytes(unrelated, independent);

            var package = new DownloadPackage { SaveDirectory = directory.FullName, AutoExtractArchives = false };
            foreach (string file in new[] { payload, parity })
            {
                var size = new FileInfo(file).Length;
                package.Items.Add(new DownloadItem { PackageId = package.Id, FileName = Path.GetFileName(file),
                    SaveFilePath = file, Status = DownloadStatus.Completed, TotalBytes = size, DownloadedBytes = size });
            }
            package.RecalculateAggregates(force: true);
            Assert.True(package.IsFullyCompleted);
            DownloadPackage[] packages = [package];
            var model = new MainViewModel();
            repairs.ProcessStarted = (_, repairing) =>
            {
                if (!repairing)
                {
                    verificationStarted.TrySetResult();
                    releaseVerification.Task.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
                }
            };

            await OnUiAsync(() =>
            {
                actions.CountdownFinished += Finished;
                actions.CountdownCancelled += Cancelled;
                actions.CurrentAction = PostDownloadAction.Sleep;
                actions.NotifyWorkStarted();
                actions.Evaluate(packages);
                Assert.True(actions.IsCountdownActive);
            });

            repair = Task.Run(() => model.Par2RepairPackageCommand.ExecuteAsync(package));
            await verificationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            advance.SetResult();
            Assert.False(await firstOutcome.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(PostDownloadActionService.CheckHasActiveWork(packages));
            Assert.False(finished.Task.IsCompleted);
            Assert.Equal(PostDownloadAction.Sleep, actions.CurrentAction);
            Assert.Equal(damaged, File.ReadAllBytes(payload));

            releaseVerification.SetResult();
            await repair.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(Loc.Get("Status_Par2Repaired"), model.StatusSummary);
            Assert.Equal(original, File.ReadAllBytes(payload));
            Assert.Equal(independent, File.ReadAllBytes(payload + ".1"));
            Assert.Equal(independent, File.ReadAllBytes(unrelated));
            Assert.False(PostDownloadActionService.CheckHasActiveWork(packages));

            await OnUiAsync(() => actions.Evaluate(packages));
            Assert.Equal(PostDownloadAction.Sleep, await finished.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            releaseVerification.TrySetResult();
            try
            {
                if (repair != null) await repair.WaitAsync(TimeSpan.FromSeconds(20));
            }
            finally
            {
                repairs.ProcessStarted = previousHook;
                await OnUiAsync(() =>
                {
                    actions.CancelCountdown();
                    actions.CurrentAction = PostDownloadAction.None;
                    actions.CountdownFinished -= Finished;
                    actions.CountdownCancelled -= Cancelled;
                });
                directory.Delete(recursive: true);
            }
        }
    }

    private static async Task OnUiAsync(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.CheckAccess())
        {
            action();
            return;
        }
        // Drain earlier UI notifications before changing countdown observers.
        await dispatcher.InvokeAsync(action, System.Windows.Threading.DispatcherPriority.Background)
            .Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static DownloadPackage CreateCompletedPackage()
    {
        var package = new DownloadPackage { Name = "Example package", AutoExtractArchives = true, IsEnabled = true };
        package.Items.Add(new DownloadItem { PackageId = package.Id, FileName = "payload.zip", Status = DownloadStatus.Completed,
            TotalBytes = 100, DownloadedBytes = 100, IsEnabled = true });
        package.EnsureNextTaskSteps();
        package.SetNextTaskDone("Extract");
        package.Status = DownloadStatus.Completed;
        return package;
    }

    [Theory]
    [InlineData(PostDownloadAction.None, "PostDownload_Action_None")]
    [InlineData(PostDownloadAction.Shutdown, "PostDownload_Action_Shutdown")]
    [InlineData(PostDownloadAction.Sleep, "PostDownload_Action_Sleep")]
    [InlineData(PostDownloadAction.ExitApp, "PostDownload_Action_ExitApp")]
    public void PostDownloadAction_GetActionDisplayName_ReturnsExpectedTranslations(PostDownloadAction action, string locKey)
    {
        var service = PostDownloadActionService.Instance;
        var displayName = service.GetActionDisplayName(action);
        var expected = Loc.Get(locKey);

        Assert.Equal(expected, displayName);
    }
}
