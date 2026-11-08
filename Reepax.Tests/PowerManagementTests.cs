using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Reepax.Models;
using Reepax.Services.Extractor;
using Reepax.Services.Localization;
using Reepax.Services.SystemIntegration;
using Xunit;

namespace Reepax.Tests;

public sealed class PowerManagementTests
{
    [Fact]
    public async Task NativeRequest_CanBeCreatedAndClosedOnDifferentThreads()
    {
        using var request = WindowsKeepAwakeRequest.Create();
        Assert.False(request.IsInvalid);
        Assert.False(request.IsClosed);
        await Task.Run(request.Dispose);
        Assert.True(request.IsClosed);
    }

    [Fact]
    public async Task OverlappingScopes_ShareOneNativeRequestUntilTheLastOperationEnds()
    {
        var requests = new List<RecordingRequest>();
        var service = new PowerManagementService(() =>
        {
            var request = new RecordingRequest();
            requests.Add(request);
            return request;
        });
        try
        {
            using var download = service.CreateKeepAwakeScope("Example download");
            await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            {
                using var extraction = service.CreateKeepAwakeScope("Example extraction");
                extraction.Suspend();
                extraction.Suspend();
                extraction.Resume();
                extraction.Resume();
                extraction.Dispose();
                extraction.Dispose();
            })));
            var native = Assert.Single(requests);
            Assert.Equal(0, native.Disposals);
            Assert.True(service.IsKeepAwakeActive);
            download.Dispose();
            Assert.False(service.IsKeepAwakeActive);
            Assert.Equal(1, native.Disposals);
        }
        finally { service.ReleaseAll(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleaseAll_InvalidatesOldScopesWithoutReleasingANewerRequest(bool suspended)
    {
        var requests = new List<RecordingRequest>();
        var service = new PowerManagementService(() =>
        {
            var request = new RecordingRequest();
            requests.Add(request);
            return request;
        });
        try
        {
            using var old = service.CreateKeepAwakeScope("Previous operation");
            if (suspended) old.Suspend();
            service.ReleaseAll();
            Assert.False(service.IsKeepAwakeActive);
            Assert.Equal(1, Assert.Single(requests).Disposals);
            using var current = service.CreateKeepAwakeScope("Current operation");
            old.Resume();
            old.Suspend();
            old.Dispose();
            Assert.True(service.IsKeepAwakeActive);
            Assert.Equal(2, requests.Count);
            Assert.Equal(0, requests[1].Disposals);
            current.Suspend();
            Assert.False(service.IsKeepAwakeActive);
            Assert.Equal(1, requests[1].Disposals);
            current.Resume();
            Assert.True(service.IsKeepAwakeActive);
            Assert.Equal(3, requests.Count);
            current.Dispose();
            service.ReleaseAll();
            service.ReleaseAll();
            Assert.All(requests, request => Assert.Equal(1, request.Disposals));
        }
        finally { service.ReleaseAll(); }
    }

    [Fact]
    public void FailedNativeCreation_DoesNotReportSuccessAndCanRecoverWithoutLosingReferences()
    {
        int attempts = 0;
        var native = new RecordingRequest();
        var service = new PowerManagementService(() => ++attempts == 1
            ? throw new System.ComponentModel.Win32Exception(5) : native);
        try
        {
            using var first = service.CreateKeepAwakeScope("First operation");
            Assert.False(service.IsKeepAwakeActive);
            using var second = service.CreateKeepAwakeScope("Second operation");
            Assert.True(service.IsKeepAwakeActive);
            Assert.Equal(2, attempts);
            second.Dispose();
            Assert.True(service.IsKeepAwakeActive);
            Assert.Equal(0, native.Disposals);
            first.Dispose();
            Assert.False(service.IsKeepAwakeActive);
            Assert.Equal(1, native.Disposals);
        }
        finally { service.ReleaseAll(); }
    }

    [Fact]
    public async Task ReleaseFromAnotherThread_ClearsTheOriginalThreadsSleepRequirement()
    {
        var service = PowerManagementService.Instance;
        service.ReleaseAll();
        using var inspect = new ManualResetEventSlim();
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remaining = new TaskCompletionSource<PowerManagementService.ExecutionState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new Thread(() =>
        {
            try
            {
                Assert.NotEqual(0u, (uint)SetThreadExecutionState(PowerManagementService.ExecutionState.EsContinuous));
                service.AcquireKeepAwake("Example operation");
                acquired.TrySetResult();
                Assert.True(inspect.Wait(TimeSpan.FromSeconds(10)));
                remaining.TrySetResult(SetThreadExecutionState(PowerManagementService.ExecutionState.EsContinuous));
            }
            catch (Exception error)
            {
                acquired.TrySetException(error);
                remaining.TrySetException(error);
            }
            finally { SetThreadExecutionState(PowerManagementService.ExecutionState.EsContinuous); }
        }) { IsBackground = true };
        owner.SetApartmentState(ApartmentState.STA);
        owner.Start();
        try
        {
            await acquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(service.IsKeepAwakeActive);
            await Task.Run(() => service.ReleaseKeepAwake("Example finished"));
            Assert.False(service.IsKeepAwakeActive);
            inspect.Set();
            var state = await remaining.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(0u, (uint)state);
            Assert.Equal(0u, (uint)(state & (PowerManagementService.ExecutionState.EsSystemRequired |
                PowerManagementService.ExecutionState.EsAwayModeRequired | PowerManagementService.ExecutionState.EsDisplayRequired)));
        }
        finally
        {
            inspect.Set();
            Assert.True(owner.Join(TimeSpan.FromSeconds(10)), "The test caller thread did not stop.");
            service.ReleaseAll();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PausedExtraction_PreservesAnotherOperationsRequestAfterResumeOrCancellation(bool cancel, bool packageExtraction)
    {
        string root = Directory.CreateTempSubdirectory("PowerExtraction_").FullName;
        var service = PowerManagementService.Instance;
        service.ReleaseAll();
        using var pause = new ManualResetEventSlim(true);
        using var cancellation = new CancellationTokenSource();
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? extraction = null;
        DownloadPackage? package = null;
        PropertyChangedEventHandler? stateChanged = null;
        try
        {
            byte[] payload = Enumerable.Range(0, 1024 * 1024).Select(i => (byte)(i % 251)).ToArray();
            byte[] previous = [7, 8, 9];
            byte[] unrelated = [4, 5, 6];
            string archive = Path.Combine(root, "example.zip");
            string output = Directory.CreateDirectory(Path.Combine(root, "output")).FullName;
            File.WriteAllBytes(Path.Combine(output, "payload.bin"), previous);
            File.WriteAllBytes(Path.Combine(output, "unrelated.bin"), unrelated);
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            using (var stream = zip.CreateEntry("payload.bin", CompressionLevel.NoCompression).Open())
                stream.Write(payload);

            service.AcquireKeepAwake("Other active operation");
            int pauseRequested = 0;
            if (packageExtraction)
            {
                var created = new DownloadPackage
                {
                    Name = "Example", SaveDirectory = output, AutoExtractArchives = true,
                    AutoPar2Repair = false, DeleteArchiveAfterExtraction = false
                };
                package = created;
                created.Items.Add(new DownloadItem
                {
                    PackageId = created.Id, FileName = "example.zip", SaveFilePath = archive,
                    Status = DownloadStatus.Completed, TotalBytes = new FileInfo(archive).Length,
                    DownloadedBytes = new FileInfo(archive).Length
                });
                stateChanged = (_, args) =>
                {
                    if (args.PropertyName == nameof(created.StatusMessage) &&
                        created.StatusMessage == Loc.Format("Status_ExtractingProgress", 0.0.ToString("0.00")) &&
                        Interlocked.CompareExchange(ref pauseRequested, 1, 0) == 0)
                        ArchiveExtractionService.Instance.PausePackageExtraction(created.Id);
                    if (args.PropertyName == nameof(created.IsExtractionPaused) && created.IsExtractionPaused)
                        paused.TrySetResult();
                };
                created.PropertyChanged += stateChanged;
                extraction = ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(created, force: true, cancellationToken: cancellation.Token);
            }
            else extraction = ArchiveExtractionService.Instance.ExtractArchiveAsync(archive, output,
                statusCallback: status =>
                {
                    if (status == Loc.Get("Status_ExtractionPaused")) paused.TrySetResult();
                }, progressCallback: progress =>
                {
                    if (Interlocked.CompareExchange(ref pauseRequested, 1, 0) == 0) pause.Reset();
                }, pauseEvent: pause, cancellationToken: cancellation.Token);
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(service.IsKeepAwakeActive);
            if (package != null)
            {
                if (cancel) ArchiveExtractionService.Instance.CancelPackageExtraction(package.Id);
                else ArchiveExtractionService.Instance.ResumePackageExtraction(package.Id);
            }
            else if (cancel) cancellation.Cancel();
            else pause.Set();
            Assert.Equal(!cancel, await extraction.WaitAsync(TimeSpan.FromSeconds(15)));
            if (package != null)
            {
                Assert.False(package.IsExtracting);
                Assert.False(package.IsExtractionPaused);
                Assert.Equal(!cancel, package.IsExtracted);
            }
            Assert.True(service.IsKeepAwakeActive);
            Assert.Equal(cancel ? previous : payload, File.ReadAllBytes(Path.Combine(output, "payload.bin")));
            Assert.Equal(unrelated, File.ReadAllBytes(Path.Combine(output, "unrelated.bin")));
            service.ReleaseKeepAwake("Other operation finished");
            Assert.False(service.IsKeepAwakeActive);
        }
        finally
        {
            cancellation.Cancel();
            pause.Set();
            if (package != null) ArchiveExtractionService.Instance.CancelPackageExtraction(package.Id);
            try { if (extraction != null) await extraction.WaitAsync(TimeSpan.FromSeconds(15)); }
            finally
            {
                if (package != null) package.PropertyChanged -= stateChanged;
                service.ReleaseAll();
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern PowerManagementService.ExecutionState SetThreadExecutionState(PowerManagementService.ExecutionState flags);

    private sealed class RecordingRequest : IDisposable
    {
        internal int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }
}
