using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Reepax.Helpers;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Extractor;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

internal sealed class ConcurrentPackageWorkload : IAsyncDisposable
{
    private const int Prefix = 96 * 1024;
    private readonly string _root;
    private readonly int _rounds;
    private readonly MainViewModel _vm;
    private readonly QueueManager _queue = QueueManager.Instance;
    private readonly DownloadEngine _engine = DownloadEngine.Instance;
    private readonly DownloadPackage[] _packages;
    private readonly Dictionary<Guid, byte[]> _downloads = new();
    private readonly Dictionary<Guid, (byte[] Main, byte[] Nested)> _outputs = new();
    private readonly ConcurrentDictionary<string, BodyGate> _gates = new();
    private readonly ConcurrentDictionary<Guid, int> _notifications = new();
    private readonly ConcurrentQueue<string> _early = new();
    private readonly ConcurrentBag<Task<bool>> _extractions = new();
    private readonly ConcurrentBag<Exception> _transferErrors = new();
    private readonly Dictionary<string, double> _maximumOperationMs = new();
    private readonly TaskCompletionSource _publication = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _publishRelease = new(false);
    private readonly Func<DownloadPackage, Task<bool>> _extract;
    private readonly PipelineHttpSource _source;
    private readonly DownloadListView _projection;
    private readonly ScrollViewer _scroll;
    private readonly HwndSource _viewSource;
    private readonly DispatcherTimer _heartbeat;
    private readonly Stopwatch _clock = new();
    private readonly byte[] _unaffected = Encoding.UTF8.GetBytes("independent example file");
    private int _ticks, _maxActive, _moved, _publicationBlocked, _maxRealized;
    private double _lastTick, _maxGap;
    private volatile bool _released;

    internal ConcurrentPackageWorkload(string root, int count, int rounds)
    {
        _root = root;
        _rounds = rounds;
        var settings = SettingsService.Instance.Settings;
        settings.DefaultDownloadDirectory = Path.Combine(root, "downloads");
        settings.EnableCompletionNotifications = settings.EnableCompletionSound = settings.EnableErrorSound = false;
        settings.EnableFileLogging = settings.AutoCollapseCompletedPackages = false;
        settings.AutoPar2Repair = settings.DeleteArchiveAfterExtraction = settings.MoveArchiveToRecycleBin = false;
        settings.EnableChecksumVerification = true;
        settings.MaxAutoRetryOnCorruption = 0;
        settings.MaxConcurrentBackgroundDownloads = 6;
        settings.PostDownloadAction = PostDownloadAction.None;
        PostDownloadActionService.Instance.CurrentAction = PostDownloadAction.None;
        _engine.MaxConnectionsPerDownload = 1;
        _engine.SetSpeedLimit(0);
        _queue.Packages.Clear();
        _vm = new MainViewModel { RenameFailureNotification = _ => { } };
        _queue.MaxConcurrentDownloads = 6;
        _vm.ClearColumnSort();
        _packages = Enumerable.Range(0, count).Select(CreatePackage).ToArray();
        var files = _packages.SelectMany(package => package.Items).ToDictionary(item => SourceName(item), item => _downloads[item.Id]);
        _source = new PipelineHttpSource(files, WriteBodyAsync);
        foreach (var package in _packages)
        {
            foreach (var item in package.Items) item.OriginalUrl = item.DirectDownloadUrl = _source.Url(SourceName(item));
            _queue.Packages.Add(package);
        }
        for (int index = 0; index < 128; index++)
        {
            var package = new DownloadPackage
            {
                Name = $"Waiting {index:D3}", SaveDirectory = Path.Combine(root, "waiting", $"Waiting {index:D3}"),
                IsCustomName = true, IsEnabled = false, AutoPar2Repair = false
            };
            package.AddItems(Enumerable.Range(0, 32).Select(file => new DownloadItem
            {
                FileName = $"waiting-{file:D2}.bin", TotalBytes = 1024, DownloadedBytes = 256,
                Status = DownloadStatus.Paused, IsEnabled = false
            }));
            _queue.Packages.Add(package);
        }
        var (scroll, list) = DownloadListVirtualizationTests.CreateList(_vm.RootPackages, useFullTemplates: true);
        _scroll = scroll;
        _projection = Assert.IsType<DownloadListView>(list);
        _viewSource = new HwndSource(new HwndSourceParameters("Concurrent load view")
        {
            Width = 960, Height = 520, PositionX = -32000, PositionY = -32000,
            WindowStyle = unchecked((int)0x80000000)
        });
        _viewSource.RootVisual = _projection;
        LayoutAndCheckVirtualization();
        _extract = _queue.ExtractPackageAsync;
        _queue.ExtractPackageAsync = package =>
        {
            var task = _extract(package);
            _extractions.Add(task);
            return task;
        };
        _engine.DownloadFailed += DownloadFailed;
        ExtractionRecoveryJournal.PublicationCheckpoint = PublicationCheckpoint;
        _heartbeat = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        _heartbeat.Tick += Tick;
    }

    private DownloadPackage CreatePackage(int index)
    {
        var random = new Random(713 + index);
        byte[] data = new byte[256 * 1024], nested = new byte[64 * 1024], extra = new byte[128 * 1024];
        random.NextBytes(data);
        random.NextBytes(nested);
        random.NextBytes(extra);
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in new[] { ("payload.bin", data), ("Nested/content.bin", nested) })
            {
                var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(2020, 1, 2, 0, 0, 0, TimeSpan.Zero);
                using var output = entry.Open();
                output.Write(bytes);
            }
        }
        var package = new DownloadPackage
        {
            Name = $"Package {index:D3}", SaveDirectory = Path.Combine(_root, "downloads", $"Package {index:D3}"),
            IsCustomName = true, AutoResolveHostLinks = false, AutoExtractArchives = true, AutoPar2Repair = false,
            IsExpanded = true, IsSelected = index % 4 == 0
        };
        foreach (var (name, bytes) in new[] { ("bundle.zip", memory.ToArray()), ("additional.bin", extra), ("note.bin", new byte[] { (byte)index, 19, 71 }) })
        {
            var item = new DownloadItem
            {
                FileName = name, SaveFilePath = Path.Combine(package.SaveDirectory, name), PackageId = package.Id,
                Status = DownloadStatus.Queued, IsCustomName = true, ExpectedChecksum = Hash(bytes), TotalBytes = bytes.Length,
                IsSelected = name == "additional.bin"
            };
            _downloads.Add(item.Id, bytes);
            package.Items.Add(item);
        }
        _outputs.Add(package.Id, (data, nested));
        Directory.CreateDirectory(package.SaveDirectory);
        File.WriteAllBytes(Path.Combine(package.SaveDirectory, "payload.bin"), _unaffected);
        File.WriteAllBytes(Path.Combine(package.SaveDirectory, "unaffected.bin"), _unaffected);
        package.EnsureNextTaskSteps();
        package.PropertyChanged += PackageChanged;
        return package;
    }

    private static string SourceName(DownloadItem item) => item.Id.ToString("N");
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private async Task WriteBodyAsync(string name, long start, Stream destination, ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        var gate = _gates.GetOrAdd(name, _ => new BodyGate());
        if (bytes.Length > Prefix && Interlocked.Exchange(ref gate.Used, 1) == 0 && !_released)
        {
            await destination.WriteAsync(bytes[..Prefix], token);
            await destination.FlushAsync(token);
            await gate.Release.Task.WaitAsync(token);
            bytes = bytes[Prefix..];
        }
        await destination.WriteAsync(bytes, token);
    }

    private void PublicationCheckpoint(string target, ExtractionPublicationBoundary boundary)
    {
        if (boundary != ExtractionPublicationBoundary.BeforeOriginalMove ||
            target != Path.Combine(_packages[2].SaveDirectory, "payload.bin") ||
            Interlocked.Exchange(ref _publicationBlocked, 1) != 0) return;
        _publication.TrySetResult();
        if (!_publishRelease.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Publication checkpoint was not released.");
    }

    internal async Task<ConcurrentLoadReport> RunAsync()
    {
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        _clock.Start();
        _heartbeat.Start();
        _queue.StartQueue();
        var initial = _packages.Take(2).ToArray();
        foreach (var package in initial)
        {
            var archive = package.Items.Single(item => item.FileName == "bundle.zip");
            await UntilAsync(archive, () => PartialLength(archive) > 0);
            var note = package.Items.Single(item => item.FileName == "note.bin");
            await UntilAsync(note, () => note.Status == DownloadStatus.Completed && !note.IsVerifyingIntegrity);
        }
        await UntilAsync(_vm, () => _engine.ActiveDownloadsCount >= 4);
        _maxActive = Math.Max(_maxActive, _engine.ActiveDownloadsCount);
        foreach (var package in _packages.Skip(3))
        {
            package.IsEnabled = false;
            _queue.PausePackage(package);
        }

        for (int round = 0; round < _rounds; round++)
        {
            var active = initial.SelectMany(package => package.Items).Select(item => _engine.GetDownloadTask(item.Id)).Where(task => task != null).Cast<Task>().ToArray();
            foreach (var package in initial) _queue.PausePackage(package);
            await Task.WhenAll(active).WaitAsync(TimeSpan.FromSeconds(15));
            foreach (var package in initial)
            {
                Assert.All(package.Items, item => Assert.Equal(item.FileName == "note.bin" ? DownloadStatus.Completed : DownloadStatus.Paused, item.Status));
                var archive = package.Items.Single(item => item.FileName == "bundle.zip");
                Assert.True(File.Exists(archive.SaveFilePath + ".part.segments"));
                var partial = File.ReadAllBytes(archive.SaveFilePath + ".part");
                Assert.InRange(partial.Length, 1, _downloads[archive.Id].Length - 1);
                Assert.Equal(_downloads[archive.Id][..partial.Length], partial);
                Assert.Equal(partial.Length, archive.DownloadedBytes);
            }
            var originalDirectories = initial.Select(package => package.SaveDirectory).ToArray();
            Assert.True(await _vm.ChangeDownloadPathsAsync(initial, Path.Combine(_root, $"moved-{round}")));
            _moved += initial.Length;
            Assert.All(originalDirectories, directory => Assert.False(Directory.Exists(directory)));
            foreach (var package in initial)
                foreach (var item in package.Items) Assert.Equal(Path.Combine(package.SaveDirectory, item.FileName), item.SaveFilePath);
            await ReorderAndSaveAsync(round);

            if (round == 0)
            {
                ReleasePackage(_packages[2]);
                await _publication.Task.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(_packages[2].IsExtracting);
                Assert.False(_packages[2].HasCompletedNotified);
                string before = _packages[2].SaveDirectory;
                Assert.False(await _vm.ChangeDownloadPathsAsync([_packages[2]], Path.Combine(_root, "busy-target")));
                Assert.Equal(before, _packages[2].SaveDirectory);
                Assert.Equal(_unaffected, File.ReadAllBytes(Path.Combine(before, "payload.bin")));
                Assert.False(Directory.Exists(Path.Combine(_root, "busy-target")));
                await ReorderAndSaveAsync(round + 10);
                _publishRelease.Set();
            }

            if (round + 1 < _rounds)
            {
                foreach (var package in initial)
                {
                    var archive = package.Items.Single(item => item.FileName == "bundle.zip");
                    long previousLength = PartialLength(archive);
                    _gates[SourceName(archive)].Release.TrySetResult();
                    _gates[SourceName(archive)] = new BodyGate();
                    _queue.ResumeItem(archive);
                    await UntilAsync(archive, () => PartialLength(archive) > previousLength);
                }
            }
        }

        _released = true;
        foreach (var gate in _gates.Values) gate.Release.TrySetResult();
        foreach (var package in _packages)
        {
            package.IsEnabled = true;
            _queue.ResumePackage(package);
        }
        _queue.ProcessQueue();
        foreach (var package in _packages) await UntilAsync(package, () => package.IsExtracted && package.HasCompletedNotified && !package.IsExtracting);
        await Task.WhenAll(_extractions).WaitAsync(TimeSpan.FromSeconds(30));
        _heartbeat.Stop();
        Assert.Empty(_early);
        Assert.Empty(_transferErrors);
        Assert.Equal(0, _engine.ActiveDownloadsCount);
        Assert.False(ArchiveExtractionService.Instance.IsAnyExtracting);
        Assert.False(PostDownloadActionService.Instance.IsCountdownActive);
        Assert.InRange(_maxActive, 4, 6);
        long verified = VerifyResults();
        _vm.RecalculateGlobalStats();
        Assert.Equal(_packages.Length * 3, _vm.TotalDownloadsCount);
        Assert.Equal(_packages.Length * 3 + 128 * 32, _queue.Packages.Sum(package => package.Items.Count));
        Assert.Equal(0, _vm.ActiveDownloadsCount);
        process.Refresh();
        var resumedNames = initial.Select(package => SourceName(package.Items.Single(item => item.FileName == "bundle.zip"))).ToHashSet();
        int resumedRanges = _source.Ranges.Count(range => resumedNames.Contains(range.File) && range.Start > 0);
        return new(_packages.Length, _packages.Sum(package => package.Items.Count), _packages.Count(package => package.IsExtracted),
            _notifications.Values.Sum(), _moved, resumedRanges, _maxActive, _ticks, _maxRealized, _maxGap,
            _clock.Elapsed.TotalMilliseconds, (process.TotalProcessorTime - cpu).TotalMilliseconds, process.PeakWorkingSet64, verified, _maximumOperationMs);
    }

    private async Task ReorderAndSaveAsync(int round)
    {
        var operation = Stopwatch.StartNew();
        var group = _packages.Where((_, index) => index % 4 == 0).ToArray();
        _vm.MovePackagesAfter(group, _packages[^1]);
        _vm.MovePackagesBefore(group, _packages[1]);
        foreach (var package in _packages)
        {
            var selected = package.Items.Single(item => item.FileName == "additional.bin");
            _vm.MoveItemsBefore([selected], package.Items[0]);
            _vm.MoveItemsAfter([selected], package.Items[^1]);
            Assert.True(selected.IsSelected);
            Assert.All(package.Items, item => Assert.Equal(package.Id, item.PackageId));
            package.IsExpanded = false;
            package.IsExpanded = true;
        }
        _vm.SearchFilterText = "Package";
        _vm.SearchFilterText = string.Empty;
        _vm.RecalculateGlobalStats();
        RecordOperation("reorder-filter-expand", operation);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        operation.Restart();
        _scroll.ScrollToBottom();
        LayoutAndCheckVirtualization();
        _scroll.ScrollToTop();
        LayoutAndCheckVirtualization();
        RecordOperation("scroll-layout", operation);
        var expectedRows = _vm.RootPackages.SelectMany(package => new object[] { package }.Concat(package.IsExpanded ? package.Items : Enumerable.Empty<DownloadItem>())).ToArray();
        Assert.Equal(expectedRows, _projection.Items.Cast<DownloadListRow>().Select(row => row.Model));
        Assert.Equal(_queue.Packages.Count, _vm.RootPackages.Count);
        using var persistence = new DownloadPersistenceService(Path.Combine(_root, $"snapshot-{round}.json"));
        operation.Restart();
        persistence.SaveDownloads(_queue.Packages, sync: true);
        Assert.True(File.Exists(Path.Combine(_root, $"snapshot-{round}.json")), "A synchronous save must publish its snapshot before returning.");
        RecordOperation("test-snapshot-save", operation);
        operation.Restart();
        var loaded = persistence.LoadDownloads();
        RecordOperation("test-snapshot-reload", operation);
        Assert.False(persistence.HasDownloadsLoadFailed);
        Assert.Equal(_queue.Packages.Count, loaded.Count);
        foreach (var package in _packages)
        {
            var saved = loaded.Single(candidate => candidate.Id == package.Id);
            Assert.Equal(package.SaveDirectory, saved.SaveDirectory);
            foreach (var item in package.Items)
            {
                var restored = saved.Items.Single(candidate => candidate.Id == item.Id);
                Assert.Equal(item.SaveFilePath, restored.SaveFilePath);
                Assert.Equal(item.ExpectedChecksum, restored.ExpectedChecksum);
            }
        }
        Assert.All(_packages.Where((_, index) => index % 4 == 0), package => Assert.True(package.IsSelected));
    }

    private void RecordOperation(string name, Stopwatch clock) =>
        _maximumOperationMs[name] = Math.Max(_maximumOperationMs.GetValueOrDefault(name), clock.Elapsed.TotalMilliseconds);

    private long VerifyResults()
    {
        long verified = 0;
        foreach (var package in _packages)
        {
            Assert.True(package.CheckIsFullyCompleted());
            Assert.Equal(1, _notifications[package.Id]);
            Assert.All(package.NextTaskSteps, step => Assert.Equal(NextTaskStepState.Done, step.State));
            foreach (var item in package.Items)
            {
                Assert.Equal(DownloadStatus.Completed, item.Status);
                byte[] actual = File.ReadAllBytes(item.SaveFilePath!);
                Assert.Equal(_downloads[item.Id], actual);
                Assert.Equal(Hash(actual), item.CalculatedChecksum);
                Assert.Equal(Hash(actual), item.ExpectedChecksum);
                Assert.False(File.Exists(item.SaveFilePath + ".part"));
                Assert.False(File.Exists(item.SaveFilePath + ".part.segments"));
                verified += actual.Length;
            }
            var expected = _outputs[package.Id];
            Assert.Equal(expected.Main, File.ReadAllBytes(Path.Combine(package.SaveDirectory, "payload.bin")));
            Assert.Equal(expected.Nested, File.ReadAllBytes(Path.Combine(package.SaveDirectory, "Nested", "content.bin")));
            Assert.Equal(_unaffected, File.ReadAllBytes(Path.Combine(package.SaveDirectory, "unaffected.bin")));
            Assert.Empty(Directory.GetDirectories(package.SaveDirectory, ".archive-work-*", SearchOption.AllDirectories));
            verified += expected.Main.Length + expected.Nested.Length;
        }
        foreach (var package in _packages.Take(2))
        {
            string name = SourceName(package.Items.Single(item => item.FileName == "bundle.zip"));
            Assert.Equal(_rounds, _source.Ranges.Count(range => range.File == name && range.Start > 0));
        }
        Assert.Empty(Directory.GetDirectories(_root, ".download-move-*", SearchOption.AllDirectories));
        return verified;
    }

    private void LayoutAndCheckVirtualization()
    {
        _projection.Measure(new Size(960, 520));
        _projection.Arrange(new Rect(0, 0, 960, 520));
        _projection.UpdateLayout();
        int realized = Enumerable.Range(0, _projection.Items.Count)
            .Count(index => _projection.ItemContainerGenerator.ContainerFromIndex(index) != null);
        Assert.InRange(realized, 1, 128);
        _maxRealized = Math.Max(_maxRealized, realized);
    }

    private void ReleasePackage(DownloadPackage package)
    {
        foreach (var item in package.Items)
        {
            var gate = _gates.GetOrAdd(SourceName(item), _ => new BodyGate());
            gate.Used = 1;
            gate.Release.TrySetResult();
        }
    }

    private static async Task UntilAsync(INotifyPropertyChanged model, Func<bool> condition)
    {
        if (condition()) return;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, PropertyChangedEventArgs args) { if (condition()) reached.TrySetResult(); }
        var observer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        observer.Tick += (_, _) => { if (condition()) reached.TrySetResult(); };
        model.PropertyChanged += Changed;
        observer.Start();
        try
        {
            if (condition()) reached.TrySetResult();
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(45));
        }
        finally { observer.Stop(); model.PropertyChanged -= Changed; }
    }

    private static long PartialLength(DownloadItem item)
    {
        var file = new FileInfo(item.SaveFilePath + ".part");
        return file.Exists ? file.Length : 0;
    }

    private void PackageChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is not DownloadPackage package || args.PropertyName != nameof(DownloadPackage.IsNewlyCompleted) || !package.IsNewlyCompleted) return;
        _notifications.AddOrUpdate(package.Id, 1, (_, count) => count + 1);
        try
        {
            if (!package.IsExtracted || !File.ReadAllBytes(Path.Combine(package.SaveDirectory, "payload.bin")).SequenceEqual(_outputs[package.Id].Main))
                _early.Enqueue("Completion preceded verified extraction.");
        }
        catch (IOException) { _early.Enqueue("Completion preceded publication."); }
    }

    private void DownloadFailed(DownloadItem item, Exception error) => _transferErrors.Add(error);

    private void Tick(object? sender, EventArgs args)
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        if (_ticks++ > 0) _maxGap = Math.Max(_maxGap, now - _lastTick);
        _lastTick = now;
        _maxActive = Math.Max(_maxActive, _engine.ActiveDownloadsCount);
        _vm.RecalculateGlobalStats();
    }

    public async ValueTask DisposeAsync()
    {
        _released = true;
        _publishRelease.Set();
        foreach (var gate in _gates.Values) gate.Release.TrySetResult();
        _heartbeat.Stop();
        _queue.StopQueue();
        foreach (var package in _packages) ArchiveExtractionService.Instance.CancelPackageExtraction(package);
        await _engine.PauseAllAsync();
        await _source.DisposeAsync();
        await Task.WhenAll(_extractions).WaitAsync(TimeSpan.FromSeconds(20));
        _queue.ExtractPackageAsync = _extract;
        ExtractionRecoveryJournal.PublicationCheckpoint = null;
        _engine.DownloadFailed -= DownloadFailed;
        foreach (var package in _packages) package.PropertyChanged -= PackageChanged;
        _viewSource.RootVisual = null;
        _viewSource.Dispose();
        _projection.Packages = null;
        DownloadPersistenceService.Instance.Dispose();
        _publishRelease.Dispose();
    }

    private sealed class BodyGate
    {
        internal int Used;
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
