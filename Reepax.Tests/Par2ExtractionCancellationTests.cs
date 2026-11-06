using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Extractor;
using Reepax.Services.Localization;
using Reepax.Services.Verification;
using Xunit;

namespace Reepax.Tests;

public sealed class Par2ExtractionCancellationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CancelDuringPar2_StopsNativeProcessWithoutCompletionAndAllowsRetry(bool repairStage, bool manual)
    {
        await using var fixture = await PackagePipelineFixture.CreateAsync(repairStage ? "repairable" : "intact",
            payloadLength: 4 * 1024 * 1024);
        if (manual)
        {
            fixture.AutoStartExtraction = false;
            QueueManager.Instance.StopQueue();
        }
        string independentBackup = fixture.Archive.SaveFilePath + ".1";
        File.WriteAllBytes(independentBackup, fixture.Unaffected);
        await using var checkpoint = new NativeCheckpoint(repairStage);
        Task<bool>? extraction = null;
        try
        {
            await fixture.DownloadRepairFilesAsync();
            await fixture.DownloadArchiveAsync();
            extraction = manual
                ? ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(fixture.Package, force: true)
                : await fixture.NextExtractionAsync();
            Process process = await checkpoint.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(process.HasExited);
            Assert.True(ArchiveExtractionService.Instance.IsPackageExtracting(fixture.Package.Id));
            Assert.Contains(fixture.Package.Id, Par2RepairService.Instance.GetActiveRepairs().Keys);

            if (manual) ArchiveExtractionService.Instance.CancelAllExtractions();
            else ArchiveExtractionService.Instance.CancelPackageExtraction(fixture.Package.Id);

            Assert.False(await extraction.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(process.HasExited);
            Assert.False(ArchiveExtractionService.Instance.IsPackageExtracting(fixture.Package.Id));
            Assert.DoesNotContain(fixture.Package.Id, Par2RepairService.Instance.GetActiveRepairs().Keys);
            Assert.False(fixture.Package.IsExtracting);
            Assert.False(fixture.Package.IsExtracted);
            Assert.False(fixture.Package.IsFullyCompleted);
            Assert.Equal(Loc.Get("Status_Paused"), fixture.Package.StatusMessage);
            Assert.All(fixture.Package.NextTaskSteps, step => Assert.Equal(NextTaskStepState.Pending, step.State));
            fixture.EvaluateCompletion();
            Assert.Equal(0, fixture.NotificationCount);
            Assert.Equal(0, fixture.CountdownCount);
            Assert.Equal(fixture.PreviousOutput, File.ReadAllBytes(fixture.OutputPath));
            Assert.Equal(fixture.ServedArchive, File.ReadAllBytes(fixture.Archive.SaveFilePath!));
            Assert.Equal(fixture.Unaffected, File.ReadAllBytes(independentBackup));
            fixture.AssertUnaffectedFiles();
            using (File.Open(fixture.Archive.SaveFilePath!, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }

            await checkpoint.DisposeAsync();
            extraction = ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(fixture.Package, force: true);
            Assert.True(await extraction.WaitAsync(TimeSpan.FromSeconds(30)));
            await fixture.CountdownStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(fixture.OriginalArchive, File.ReadAllBytes(fixture.Archive.SaveFilePath!));
            Assert.Equal(fixture.Payload, File.ReadAllBytes(fixture.OutputPath));
            Assert.Equal(fixture.Unaffected, File.ReadAllBytes(independentBackup));
            fixture.AssertUnaffectedFiles();
            Assert.True(fixture.Package.IsFullyCompleted);
            Assert.Equal(1, fixture.NotificationCount);
            Assert.Equal(1, fixture.CountdownCount);
            Assert.Empty(fixture.EarlyCompletion);
            Assert.All(fixture.Package.NextTaskSteps, step => Assert.Equal(NextTaskStepState.Done, step.State));
        }
        finally
        {
            ArchiveExtractionService.Instance.CancelPackageExtraction(fixture.Package.Id);
            await checkpoint.DisposeAsync();
            if (extraction != null) await extraction.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class NativeCheckpoint : IAsyncDisposable
    {
        private readonly Par2RepairService _service = Par2RepairService.Instance;
        private readonly Action<Process, bool>? _previous;
        private Process? _process;
        private int _disposed;
        internal TaskCompletionSource<Process> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal NativeCheckpoint(bool repairStage)
        {
            _previous = _service.ProcessStarted;
            _service.ProcessStarted = (process, repairing) =>
            {
                if (repairing != repairStage || _process != null) return;
                try
                {
                    _process = Process.GetProcessById(process.Id);
                    // Freeze only this invocation's child, before requesting cancellation.
                    int status = NtSuspendProcess(_process.Handle);
                    if (status != 0) throw new InvalidOperationException("The repair test process could not be suspended.");
                    Started.TrySetResult(_process);
                }
                catch (Exception error) { Started.TrySetException(error); }
            };
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _service.ProcessStarted = _previous;
            if (_process == null) return;
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { _process.Dispose(); }
        }

        [DllImport("ntdll.dll", ExactSpelling = true)]
        private static extern int NtSuspendProcess(IntPtr processHandle);
    }
}
