using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Reepax.Models;
using Reepax.Services.Storage;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

public partial class DialogUiTests
{
    [Fact]
    public void GlobalStatistics_WorkerProgressOnlyPublishesOnDispatcher()
    {
        RunInSta(() =>
        {
            var items = Enumerable.Range(0, 1000).Select(index => new DownloadItem
            {
                Status = index == 0 ? DownloadStatus.Downloading : DownloadStatus.Paused, TotalBytes = 1000
            }).ToArray();
            var package = new DownloadPackage { IsCustomName = true, AutoPar2Repair = false };
            package.AddItems(items);
            bool previousTestMode = DownloadPersistenceService.IsTestEnvironment;
            int uiThread = Environment.CurrentManagedThreadId;
            int publications = 0;
            package.PropertyChanged += (_, _) =>
            {
                Assert.Equal(uiThread, Environment.CurrentManagedThreadId);
                publications++;
            };
            // Wait on the aggregation clock instead of relying on a scheduler delay.
            long waitStarted = Environment.TickCount64;
            Assert.True(SpinWait.SpinUntil(() => Environment.TickCount64 - waitStarted >= 310, TimeSpan.FromSeconds(5)));
            try
            {
                DownloadPersistenceService.IsTestEnvironment = false;
                long revision = package.AggregateInputRevision;
                Assert.True(Task.Run(() => items[0].UpdateProgress(500, 1000, 100)).Wait(TimeSpan.FromSeconds(5)));
                Assert.Equal(0, package.DownloadedBytes);
                Assert.Equal(0, publications);
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(500, package.DownloadedBytes);
                Assert.True(publications > 0);
                Assert.InRange(package.AggregateInputRevision - revision, 1, 12);
                Assert.False(package.IsDirty);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                DownloadPersistenceService.IsTestEnvironment = previousTestMode;
            }
        });
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(10000)]
    public void GlobalStatistics_RecurringUiWorkIsBoundedDuringConcurrentTransfers(int count)
    {
        RunInSta(() =>
        {
            using var transfer = new ProgressTransfer(1, count: 2);
            var viewModel = new MainViewModel();
            var previousPackages = viewModel.Packages.ToArray();
            var settings = SettingsService.Instance.Settings;
            var previousAction = settings.PostDownloadAction;
            var package = new DownloadPackage { IsCustomName = true, AutoPar2Repair = false, HasCompletedNotified = true };
            package.AddItems(Enumerable.Range(0, count).Select(_ => new DownloadItem
            {
                Status = DownloadStatus.Paused, TotalBytes = 1024, DownloadedBytes = 256
            }).Concat(transfer.Items));
            var dispatcher = Dispatcher.CurrentDispatcher;
            int uiThread = Environment.CurrentManagedThreadId;
            bool published = false;
            void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName != nameof(DownloadPackage.DownloadedBytes)) return;
                published = true;
                Assert.Equal(uiThread, Environment.CurrentManagedThreadId);
            }
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            var clock = Stopwatch.StartNew();
            double lastTick = 0, maxGap = 0;
            int ticks = 0;
            timer.Tick += (_, _) =>
            {
                double now = clock.Elapsed.TotalMilliseconds;
                if (ticks++ > 0) maxGap = Math.Max(maxGap, now - lastTick);
                lastTick = now;
                viewModel.RecalculateGlobalStats();
                if (transfer.Controls.All(control => control.Blocked.Task.IsCompleted))
                    foreach (var control in transfer.Controls) control.Release.TrySetResult();
            };
            try
            {
                settings.PostDownloadAction = PostDownloadAction.None;
                viewModel.Packages.Clear();
                viewModel.Packages.Add(package);
                transfer.Items[0].Status = DownloadStatus.Downloading;
                viewModel.RecalculateGlobalStats();
                long revision = package.AggregateInputRevision;
                for (int tick = 0; tick < 30; tick++) viewModel.RecalculateGlobalStats();
                Assert.Equal(revision, package.AggregateInputRevision);
                const int repetitions = 100;
                var fullClock = Stopwatch.StartNew();
                for (int tick = 0; tick < repetitions; tick++) package.RecalculateAggregates(force: true);
                double fullMs = fullClock.Elapsed.TotalMilliseconds;
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                var cachedClock = Stopwatch.StartNew();
                for (int tick = 0; tick < repetitions; tick++) viewModel.RecalculateGlobalStats();
                double cachedMs = cachedClock.Elapsed.TotalMilliseconds;
                long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                transfer.Items[0].Status = DownloadStatus.Paused;
                using var process = Process.GetCurrentProcess();
                var cpuBefore = process.TotalProcessorTime;
                long managedBefore = GC.GetTotalMemory(false);
                clock.Restart();
                package.PropertyChanged += OnChanged;
                timer.Start();
                transfer.Start();
                PumpProgressUntil(Task.WhenAll(transfer.Downloads));
                dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                viewModel.RecalculateGlobalStats();
                timer.Stop();
                double elapsed = clock.Elapsed.TotalSeconds;
                double cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
                long managedDelta = GC.GetTotalMemory(false) - managedBefore;
                Assert.True(published);
                Assert.True(ticks > 0);
                Assert.All(transfer.Items, item =>
                {
                    Assert.Equal(DownloadStatus.Completed, item.Status);
                    Assert.Equal(ProgressTransfer.Data, File.ReadAllBytes(item.SaveFilePath!));
                });
                Assert.Equal(0, viewModel.ActiveDownloadsCount);
                Assert.Equal(count + 2, viewModel.TotalDownloadsCount);
                Assert.Equal(count * 256L + 2L * ProgressTransfer.Data.Length, viewModel.DownloadedBytes);
                Console.WriteLine($"Statistics load: files={count + 2}; full-package={fullMs:F2} ms/{repetitions}; cached-global={cachedMs:F2} ms/{repetitions}; UI-allocated={allocated}; transfer-CPU={cpuMs:F2} ms; managed-delta={managedDelta}; controlled-stream={2 * ProgressTransfer.Data.Length / elapsed / 1024:F1} KiB/s; UI-max-gap={maxGap:F2} ms; UI-ticks={ticks}.");
            }
            finally
            {
                timer.Stop();
                package.PropertyChanged -= OnChanged;
                viewModel.Packages.Clear();
                foreach (var previous in previousPackages) viewModel.Packages.Add(previous);
                settings.PostDownloadAction = previousAction;
            }
        });
    }
}
