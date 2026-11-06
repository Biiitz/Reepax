using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Extractor;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;
using Reepax.Services.Verification;
using Xunit;

namespace Reepax.Tests;

internal sealed class PackagePipelineFixture : IAsyncDisposable
{
    internal const string Password = "Example Archive Key";
    private readonly string _root = Directory.CreateTempSubdirectory("PackagePipeline_").FullName;
    private readonly QueueManager _queue = QueueManager.Instance;
    private readonly PostDownloadActionService _actions = PostDownloadActionService.Instance;
    private readonly Func<DownloadPackage, Task<bool>> _previousExtractor;
    private readonly Func<int>? _previousActiveCount;
    private readonly DownloadPackage[] _previousPackages;
    private readonly Action _restoreSettings;
    private readonly bool _previousTestMode;
    private readonly HttpClient _client = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
    private readonly ConcurrentBag<Task> _operations = new();
    private readonly ConcurrentDictionary<Guid, Task> _completions = new();
    private readonly Channel<Task<bool>> _extractions = Channel.CreateUnbounded<Task<bool>>();
    private PipelineHttpSource? _source;
    private int _countdowns;
    private int _notifications;

    internal DownloadEngine Engine { get; }
    internal DownloadPackage Package { get; private set; } = null!;
    internal DownloadItem Archive { get; private set; } = null!;
    internal byte[] OriginalArchive { get; private set; } = [];
    internal byte[] ServedArchive { get; private set; } = [];
    internal byte[] Payload { get; private set; } = [];
    internal byte[] PreviousOutput { get; } = Encoding.UTF8.GetBytes("previous extracted result");
    internal byte[] Unaffected { get; } = Encoding.UTF8.GetBytes("independent document and backup");
    internal string OutputPath => Path.Combine(Package.SaveDirectory, "payload.bin");
    internal int CountdownCount => Volatile.Read(ref _countdowns);
    internal int NotificationCount => Volatile.Read(ref _notifications);
    internal ConcurrentQueue<string> EarlyCompletion { get; } = new();
    internal TaskCompletionSource CountdownStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal PipelineHttpSource Source => _source!;
    internal bool AutoStartExtraction { get; set; } = true;

    private PackagePipelineFixture()
    {
        Engine = new DownloadEngine(_client) { MaxConnectionsPerDownload = 1 };
        var settings = SettingsService.Instance.Settings;
        var previous = (settings.EnableChecksumVerification, settings.MaxAutoRetryOnCorruption, settings.AutoPar2Repair,
            settings.DeleteArchiveAfterExtraction, settings.MoveArchiveToRecycleBin, settings.DeletePar2AfterExtraction,
            settings.EnableCompletionNotifications, settings.EnableCompletionSound, settings.EnableErrorSound, settings.PostDownloadAction);
        var passwords = settings.ExtractionPasswords.ToList();
        var statistics = settings.ArchivePasswordStatistics.Select(entry => entry.Snapshot()).ToList();
        _restoreSettings = () =>
        {
            (settings.EnableChecksumVerification, settings.MaxAutoRetryOnCorruption, settings.AutoPar2Repair,
                settings.DeleteArchiveAfterExtraction, settings.MoveArchiveToRecycleBin, settings.DeletePar2AfterExtraction,
                settings.EnableCompletionNotifications, settings.EnableCompletionSound, settings.EnableErrorSound, settings.PostDownloadAction) = previous;
            settings.ExtractionPasswords = passwords;
            settings.ArchivePasswordStatistics = statistics;
        };
        _previousTestMode = DownloadPersistenceService.IsTestEnvironment;
        DownloadPersistenceService.IsTestEnvironment = true;
        _previousPackages = _queue.Packages.ToArray();
        _previousExtractor = _queue.ExtractPackageAsync;
        _previousActiveCount = _queue.ActiveDownloadsCountOverride;
        _queue.StopQueue();
        _queue.Packages.Clear();
        // Only the explicitly driven local engine may start downloads in this fixture.
        _queue.ActiveDownloadsCountOverride = () => 1000;
        _queue.ExtractPackageAsync = package =>
        {
            if (!AutoStartExtraction) return Task.FromResult(false);
            Task<bool> task = _previousExtractor(package);
            _operations.Add(task);
            _extractions.Writer.TryWrite(task);
            return task;
        };
        _actions.CancelCountdown();
        _actions.CurrentAction = PostDownloadAction.None;
        settings.EnableChecksumVerification = true;
        settings.MaxAutoRetryOnCorruption = 0;
        settings.AutoPar2Repair = true;
        settings.DeleteArchiveAfterExtraction = false;
        settings.MoveArchiveToRecycleBin = false;
        settings.DeletePar2AfterExtraction = false;
        settings.EnableCompletionNotifications = false;
        settings.EnableCompletionSound = false;
        settings.EnableErrorSound = false;
        SettingsService.Instance.ReplaceArchivePasswords([]);
        Engine.DownloadCompleted += item =>
        {
            var completion = _queue.HandleDownloadCompleted(item);
            _completions[item.Id] = completion;
            _operations.Add(completion);
        };
        Engine.DownloadFailed += _queue.OnBackgroundDownloadFailed;
        _actions.StateChanged += ActionStateChanged;
    }

    internal static async Task<PackagePipelineFixture> CreateAsync(string scenario, bool encrypted = false, int payloadLength = 16 * 1024)
    {
        var fixture = new PackagePipelineFixture();
        try
        {
            await fixture.InitializeAsync(scenario, encrypted, payloadLength);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    private async Task InitializeAsync(string scenario, bool encrypted, int payloadLength)
    {
        string originals = Path.Combine(_root, "source");
        string downloads = Path.Combine(_root, "downloads");
        Directory.CreateDirectory(originals);
        Directory.CreateDirectory(downloads);
        var random = new Random(731);
        Payload = new byte[payloadLength];
        random.NextBytes(Payload);
        if (encrypted)
        {
            string text = new string(Payload.Select(value => (char)('!' + value % 90)).ToArray());
            Payload = Encoding.UTF8.GetBytes(text);
            OriginalArchive = ArchivePasswordTests.CreateEncryptedZip(Password, "payload.bin", text);
        }
        else
        {
            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = zip.CreateEntry("payload.bin", CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(2020, 1, 2, 0, 0, 0, TimeSpan.Zero);
                using var output = entry.Open();
                output.Write(Payload);
            }
            OriginalArchive = memory.ToArray();
        }
        File.WriteAllBytes(Path.Combine(originals, "archive.zip"), OriginalArchive);
        await CreateRepairDataAsync(originals);
        ServedArchive = OriginalArchive.ToArray();
        int offset = 40 + 512;
        int damage = scenario == "unrepairable" ? 4096 : scenario is "repairable" or "checksum-rejected" ? 1 : 0;
        for (int index = offset; index < offset + damage; index++) ServedArchive[index] ^= 0x5a;
        var files = Directory.GetFiles(originals, "*.par2").OrderBy(path => path)
            .ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes);
        files.Add("archive.zip", ServedArchive);
        _source = new PipelineHttpSource(files);
        Package = new DownloadPackage
        {
            Name = "Example package", IsCustomName = true, SaveDirectory = downloads,
            AutoExtractArchives = true, AutoPar2Repair = true, IsEnabled = true
        };
        foreach (var (name, bytes) in files)
        {
            var item = new DownloadItem
            {
                PackageId = Package.Id, FileName = name, SaveFilePath = Path.Combine(downloads, name),
                OriginalUrl = Source.Url(name), DirectDownloadUrl = Source.Url(name), IsCustomName = true,
                TotalBytes = bytes.Length, Status = DownloadStatus.Queued, IsEnabled = true,
                ExpectedChecksum = name != "archive.zip" || scenario is "intact" or "checksum-rejected" ? Hash(name == "archive.zip" ? OriginalArchive : bytes) : null
            };
            Package.Items.Add(item);
            if (name == "archive.zip") Archive = item;
        }
        File.WriteAllBytes(OutputPath, PreviousOutput);
        File.WriteAllBytes(Path.Combine(downloads, "unaffected.bin"), Unaffected);
        File.WriteAllBytes(Path.Combine(downloads, "unaffected.bin.1"), Unaffected);
        Package.EnsureNextTaskSteps();
        Package.PropertyChanged += PackageChanged;
        _queue.Packages.Add(Package);
        _actions.CurrentAction = PostDownloadAction.Sleep;
        _queue.StartQueue();
    }

    private static async Task CreateRepairDataAsync(string directory)
    {
        string executable = Par2RepairService.Instance.ResolvePar2ExecutablePath() ?? throw new InvalidOperationException("Repair executable is unavailable.");
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string arg in new[] { "c", "-s256", "-c2", "archive.par2", "archive.zip" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Repair data creation did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await Task.WhenAll(stdout, stderr);
        }
    }

    internal async Task DownloadRepairFilesAsync()
    {
        foreach (var item in Package.Items.Where(item => item != Archive))
        {
            await Engine.ResumeDownloadAsync(item).WaitAsync(TimeSpan.FromSeconds(30));
            await _completions[item.Id].WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(DownloadStatus.Completed, item.Status);
            Assert.Equal(File.ReadAllBytes(Path.Combine(_root, "source", item.FileName)), File.ReadAllBytes(item.SaveFilePath!));
            Assert.Equal(Hash(File.ReadAllBytes(item.SaveFilePath!)), item.ExpectedChecksum);
        }
    }

    internal async Task DownloadArchiveAsync()
    {
        Task download = Engine.ResumeDownloadAsync(Archive);
        _operations.Add(download);
        await Source.ArchiveRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(File.Exists(Archive.SaveFilePath));
        Assert.False(Package.HasCompletedNotified);
        Assert.Equal(0, CountdownCount);
        Assert.Equal(PreviousOutput, File.ReadAllBytes(OutputPath));
        Source.ReleaseArchive();
        await download.WaitAsync(TimeSpan.FromSeconds(30));
        if (_completions.TryGetValue(Archive.Id, out var completion))
            await completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    internal async Task<Task<bool>> NextExtractionAsync() =>
        await _extractions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

    internal bool HasPendingExtraction => _extractions.Reader.TryPeek(out _);

    internal void EvaluateCompletion() => _actions.Evaluate(_queue.Packages);

    private void PackageChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(DownloadPackage.IsNewlyCompleted) && Package.IsNewlyCompleted)
        {
            Interlocked.Increment(ref _notifications);
            CheckPublishedResults("notification");
        }
    }

    private void ActionStateChanged()
    {
        if (!_actions.IsCountdownActive || Interlocked.CompareExchange(ref _countdowns, 1, 0) != 0) return;
        CheckPublishedResults("action");
        _actions.CancelCountdown();
        CountdownStarted.TrySetResult();
    }

    private void CheckPublishedResults(string stage)
    {
        try
        {
            if (!Package.CheckIsFullyCompleted() || !File.ReadAllBytes(OutputPath).SequenceEqual(Payload) ||
                !File.ReadAllBytes(Archive.SaveFilePath!).SequenceEqual(OriginalArchive))
                EarlyCompletion.Enqueue(stage);
        }
        catch (IOException) { EarlyCompletion.Enqueue(stage); }
    }

    internal void AssertUnaffectedFiles()
    {
        Assert.Equal(Unaffected, File.ReadAllBytes(Path.Combine(Package.SaveDirectory, "unaffected.bin")));
        Assert.Equal(Unaffected, File.ReadAllBytes(Path.Combine(Package.SaveDirectory, "unaffected.bin.1")));
        foreach (var item in Package.Items.Where(item => item != Archive))
            Assert.Equal(item.ExpectedChecksum, item.CalculatedChecksum);
    }

    internal static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public async ValueTask DisposeAsync()
    {
        _queue.StopQueue();
        if (Package != null) ArchiveExtractionService.Instance.CancelPackageExtraction(Package);
        try
        {
            var stopSource = _source?.DisposeAsync().AsTask() ?? Task.CompletedTask;
            try { await Task.WhenAll(Engine.PauseAllAsync(), stopSource); }
            finally { await Task.WhenAll(_operations).WaitAsync(TimeSpan.FromSeconds(15)); }
        }
        finally
        {
            _actions.CancelCountdown();
            _actions.CurrentAction = PostDownloadAction.None;
            _actions.StateChanged -= ActionStateChanged;
            if (Package != null) Package.PropertyChanged -= PackageChanged;
            _queue.ExtractPackageAsync = _previousExtractor;
            _queue.ActiveDownloadsCountOverride = _previousActiveCount;
            _queue.Packages.Clear();
            foreach (var package in _previousPackages) _queue.Packages.Add(package);
            _restoreSettings();
            DownloadPersistenceService.IsTestEnvironment = _previousTestMode;
            _client.Dispose();
            string root = Path.GetFullPath(_root);
            string temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!string.Equals(Path.GetDirectoryName(root), temporary, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("PackagePipeline_", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected integration test directory.");
            Directory.Delete(root, recursive: true);
        }
    }
}
