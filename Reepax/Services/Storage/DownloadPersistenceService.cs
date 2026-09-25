using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Localization;

namespace Reepax.Services.Storage;

public class DownloadItemDto
{
    public Guid Id { get; set; }
    public Guid PackageId { get; set; }
    public string OriginalUrl { get; set; } = string.Empty;
    public string? DirectDownloadUrl { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string HosterName { get; set; } = "Unknown";
    public string HosterIconKey { get; set; } = "Globe";
    public long TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public double ProgressPercentage { get; set; }
    public DownloadStatus Status { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public string? SaveFilePath { get; set; }
    public string? Cookies { get; set; }
    public string? UserAgent { get; set; }
    public string? Referer { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public long ElapsedDurationMs { get; set; }
}

public class NextTaskStepDto
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public NextTaskStepState State { get; set; } = NextTaskStepState.Pending;
}

public class DownloadPackageDto
{
    public Guid Id { get; set; }
    public Guid? ParentPackageId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SaveDirectory { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public bool AutoExtractArchives { get; set; } = false;
    public bool LowResourceExtraction { get; set; } = false;
    public bool DeleteArchiveAfterExtraction { get; set; } = false;
    public bool MoveArchiveToRecycleBin { get; set; } = false;
    public bool AutoResolveHostLinks { get; set; } = true;
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    public bool IsExpanded { get; set; } = true;
    public string PackageIconKey { get; set; } = "Archive";
    public long TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public double ProgressPercentage { get; set; }
    public DownloadStatus Status { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public long ElapsedDurationMs { get; set; }
    public List<NextTaskStepDto> NextTaskSteps { get; set; } = new();
    public List<DownloadItemDto> Items { get; set; } = new();
}

public class DownloadPersistenceService : IDisposable
{
    private static readonly Lazy<DownloadPersistenceService> _instance = new(() => new DownloadPersistenceService());
    public static DownloadPersistenceService Instance => _instance.Value;

    public static bool IsTestEnvironment { get; set; } = AppDomain.CurrentDomain.GetAssemblies().Any(a => 
        a.GetName().Name?.Contains("xunit", StringComparison.OrdinalIgnoreCase) == true || 
        a.GetName().Name?.Contains("testhost", StringComparison.OrdinalIgnoreCase) == true || 
        a.GetName().Name?.Contains("vstest", StringComparison.OrdinalIgnoreCase) == true);

    private readonly string _downloadsFilePath;
    private readonly string _historyFilePath;
    private readonly bool _isCustomPath;
    private readonly object _fileLock = new();
    private readonly object _hookLock = new();
    private readonly HashSet<DownloadPackage> _hookedPackages = new();
    private readonly HashSet<DownloadItem> _hookedItems = new();
    private readonly Dictionary<ObservableCollection<DownloadItem>, HashSet<DownloadItem>> _collectionToItemsMap = new();
    private readonly HashSet<NextTaskStep> _hookedSteps = new();
    private readonly Dictionary<ObservableCollection<NextTaskStep>, HashSet<NextTaskStep>> _collectionToStepsMap = new();
    private NotifyCollectionChangedEventHandler? _packagesCollectionChangedHandler;
    private readonly Timer _debounceTimer;
    private volatile bool _isDirty;
    private long _latestDownloadsSaveSequence;
    private long _latestHistorySaveSequence;
    private ObservableCollection<DownloadPackage>? _trackedPackages;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>JSON DTO for segment sidecar file (.part.segments) of DownloadEngine.</summary>
    private sealed class SegmentStateDto
    {
        public long Start { get; set; }
        public long End { get; set; }
        public long Done { get; set; }
    }

    /// <summary>JSON DTO for segment sidecar file (.part.segments) of DownloadEngine.</summary>
    private sealed class SegmentFileStateDto
    {
        public long TotalBytes { get; set; }
        public List<SegmentStateDto> Segments { get; set; } = new();
    }

    /// <summary>
    /// Reads the segment sidecar file (&lt;SaveFilePath&gt;.part.segments) of a chunked download
    /// and returns the sum of actually downloaded bytes (sum of Done values).
    /// Returns null if no sidecar exists or if it is invalid.
    /// Background: In multi-connection downloads, the .part file is pre-allocated to full size
    /// — file length is not an indicator of actual progress.
    /// </summary>
    public static long? GetSegmentedDownloadedBytes(string? saveFilePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(saveFilePath))
                return null;

            var segMetaPath = saveFilePath + ".part.segments";
            if (!File.Exists(segMetaPath))
                return null;

            string json;
            using (var stream = new FileStream(segMetaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
            {
                json = reader.ReadToEnd();
            }

            if (string.IsNullOrWhiteSpace(json))
                return null;

            var state = JsonSerializer.Deserialize<SegmentFileStateDto>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (state == null || state.TotalBytes <= 0 || state.Segments.Count == 0)
                return null;

            // Same validation as DownloadEngine.LoadSegmentState: continuous coverage of 0..TotalBytes-1
            long expectedStart = 0;
            long doneSum = 0;
            foreach (var seg in state.Segments)
            {
                if (seg.Start != expectedStart || seg.End < seg.Start ||
                    seg.Done < 0 || seg.Done > seg.End - seg.Start + 1)
                    return null;
                doneSum += seg.Done;
                expectedStart = seg.End + 1;
            }
            if (expectedStart != state.TotalBytes)
                return null;

            return doneSum;
        }
        catch
        {
            return null;
        }
    }

    public DownloadPersistenceService(string? customFilePath = null)
    {
        if (!string.IsNullOrWhiteSpace(customFilePath))
        {
            _downloadsFilePath = customFilePath;
            _isCustomPath = true;
            var dir = Path.GetDirectoryName(_downloadsFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }
            _historyFilePath = Path.Combine(dir ?? "", "history.json");
        }
        else
        {
            _isCustomPath = false;
            var appDataDir = SettingsService.AppDataDirectory;
            Directory.CreateDirectory(appDataDir);
            _downloadsFilePath = Path.Combine(appDataDir, "downloads.json");
            _historyFilePath = Path.Combine(appDataDir, "history.json");
        }

        _debounceTimer = new Timer(OnDebounceTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Dispose()
    {
        _debounceTimer?.Dispose();
        lock (_hookLock)
        {
            if (_trackedPackages != null && _packagesCollectionChangedHandler != null)
            {
                _trackedPackages.CollectionChanged -= _packagesCollectionChangedHandler;
            }
        }
    }

    public void TrackPackages(ObservableCollection<DownloadPackage> packages)
    {
        if (!_isCustomPath && IsTestEnvironment) return;

        lock (_hookLock)
        {
            if (_trackedPackages != null && _packagesCollectionChangedHandler != null)
            {
                _trackedPackages.CollectionChanged -= _packagesCollectionChangedHandler;
                foreach (var pkg in _hookedPackages.ToArray())
                {
                    UnhookPackageEvents(pkg);
                }
            }

            _trackedPackages = packages;
            _packagesCollectionChangedHandler = (s, e) => OnTrackedPackagesCollectionChanged(s, e);
            _trackedPackages.CollectionChanged += _packagesCollectionChangedHandler;

            foreach (var pkg in packages)
            {
                HookPackageEvents(pkg);
            }
        }
    }

    private void OnTrackedPackagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        lock (_hookLock)
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                foreach (var pkg in _hookedPackages.ToArray())
                {
                    UnhookPackageEvents(pkg);
                }
                if (_trackedPackages != null)
                {
                    foreach (var pkg in _trackedPackages)
                    {
                        HookPackageEvents(pkg);
                    }
                }
            }
            else
            {
                if (e.OldItems != null)
                {
                    foreach (DownloadPackage pkg in e.OldItems)
                    {
                        UnhookPackageEvents(pkg);
                    }
                }
                if (e.NewItems != null)
                {
                    foreach (DownloadPackage pkg in e.NewItems)
                    {
                        HookPackageEvents(pkg);
                    }
                }
            }
        }
        RequestSave();
    }

    public void HookPackageEvents(DownloadPackage pkg)
    {
        if (!_isCustomPath && IsTestEnvironment) return;

        lock (_hookLock)
        {
            if (!_hookedPackages.Add(pkg)) return;

            pkg.PropertyChanged += OnPackagePropertyChanged;
            pkg.Items.CollectionChanged += OnPackageItemsCollectionChanged;
            pkg.NextTaskSteps.CollectionChanged += OnPackageStepsCollectionChanged;

            if (!_collectionToItemsMap.TryGetValue(pkg.Items, out var itemsSet))
            {
                itemsSet = new HashSet<DownloadItem>();
                _collectionToItemsMap[pkg.Items] = itemsSet;
            }

            foreach (var item in pkg.Items)
            {
                HookItemEvents(item, pkg.Items);
            }

            if (!_collectionToStepsMap.TryGetValue(pkg.NextTaskSteps, out var stepsSet))
            {
                stepsSet = new HashSet<NextTaskStep>();
                _collectionToStepsMap[pkg.NextTaskSteps] = stepsSet;
            }

            foreach (var step in pkg.NextTaskSteps)
            {
                HookStepEvents(step, pkg.NextTaskSteps);
            }
        }
    }

    public void UnhookPackageEvents(DownloadPackage pkg)
    {
        if (!_isCustomPath && IsTestEnvironment) return;

        lock (_hookLock)
        {
            if (!_hookedPackages.Remove(pkg)) return;

            pkg.PropertyChanged -= OnPackagePropertyChanged;
            pkg.Items.CollectionChanged -= OnPackageItemsCollectionChanged;
            pkg.NextTaskSteps.CollectionChanged -= OnPackageStepsCollectionChanged;

            if (_collectionToItemsMap.TryGetValue(pkg.Items, out var itemsSet))
            {
                foreach (var item in itemsSet.ToArray())
                {
                    UnhookItemEvents(item);
                }
                _collectionToItemsMap.Remove(pkg.Items);
            }
            else
            {
                foreach (var item in pkg.Items)
                {
                    UnhookItemEvents(item);
                }
            }

            if (_collectionToStepsMap.TryGetValue(pkg.NextTaskSteps, out var stepsSet))
            {
                foreach (var step in stepsSet.ToArray())
                {
                    UnhookStepEvents(step);
                }
                _collectionToStepsMap.Remove(pkg.NextTaskSteps);
            }
            else
            {
                foreach (var step in pkg.NextTaskSteps)
                {
                    UnhookStepEvents(step);
                }
            }
        }
    }

    public void HookStepEvents(NextTaskStep step, ObservableCollection<NextTaskStep>? parentCollection)
    {
        if (!_isCustomPath && IsTestEnvironment) return;

        lock (_hookLock)
        {
            if (parentCollection != null)
            {
                if (!_collectionToStepsMap.TryGetValue(parentCollection, out var set))
                {
                    set = new HashSet<NextTaskStep>();
                    _collectionToStepsMap[parentCollection] = set;
                }
                set.Add(step);
            }

            if (_hookedSteps.Add(step))
            {
                step.PropertyChanged += OnStepPropertyChanged;
            }
        }
    }

    public void UnhookStepEvents(NextTaskStep step)
    {
        if (!_isCustomPath && IsTestEnvironment) return;

        lock (_hookLock)
        {
            if (_hookedSteps.Remove(step))
            {
                step.PropertyChanged -= OnStepPropertyChanged;
            }
        }
    }

    private void OnStepPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NextTaskStep.State))
        {
            RequestSave();
        }
    }

    private void OnPackageStepsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RequestSave();
        lock (_hookLock)
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                if (sender is ObservableCollection<NextTaskStep> stepsCol)
                {
                    if (_collectionToStepsMap.TryGetValue(stepsCol, out var trackedSteps))
                    {
                        foreach (var step in trackedSteps.ToArray())
                        {
                            UnhookStepEvents(step);
                        }
                        trackedSteps.Clear();
                    }
                    foreach (var step in stepsCol)
                    {
                        HookStepEvents(step, stepsCol);
                    }
                }
            }
            else
            {
                if (e.OldItems != null)
                {
                    foreach (NextTaskStep step in e.OldItems)
                    {
                        UnhookStepEvents(step);
                        if (sender is ObservableCollection<NextTaskStep> stepsCol &&
                            _collectionToStepsMap.TryGetValue(stepsCol, out var trackedSteps))
                        {
                            trackedSteps.Remove(step);
                        }
                    }
                }
                if (e.NewItems != null)
                {
                    foreach (NextTaskStep step in e.NewItems)
                    {
                        if (sender is ObservableCollection<NextTaskStep> stepsCol)
                        {
                            HookStepEvents(step, stepsCol);
                        }
                        else
                        {
                            HookStepEvents(step, null);
                        }
                    }
                }
            }
        }
    }

    public void HookItemEvents(DownloadItem item)
    {
        HookItemEvents(item, null);
    }

    public void HookItemEvents(DownloadItem item, ObservableCollection<DownloadItem>? parentCollection)
    {
        if (!_isCustomPath && IsTestEnvironment) return;

        lock (_hookLock)
        {
            if (parentCollection != null)
            {
                if (!_collectionToItemsMap.TryGetValue(parentCollection, out var set))
                {
                    set = new HashSet<DownloadItem>();
                    _collectionToItemsMap[parentCollection] = set;
                }
                set.Add(item);
            }

            if (_hookedItems.Add(item))
            {
                item.PropertyChanged += OnItemPropertyChanged;
            }
        }
    }

    public void UnhookItemEvents(DownloadItem item)
    {
        if (!_isCustomPath && IsTestEnvironment) return;

        lock (_hookLock)
        {
            if (_hookedItems.Remove(item))
            {
                item.PropertyChanged -= OnItemPropertyChanged;
            }
        }
    }

    private void OnPackagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadPackage.IsEnabled) or 
            nameof(DownloadPackage.Name) or 
            nameof(DownloadPackage.SaveDirectory) or 
            nameof(DownloadPackage.IsExpanded) or
            nameof(DownloadPackage.Status) or
            nameof(DownloadPackage.StatusMessage))
        {
            RequestSave();
        }
    }

    private void OnPackageItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RequestSave();
        lock (_hookLock)
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                if (sender is ObservableCollection<DownloadItem> itemsCol)
                {
                    if (_collectionToItemsMap.TryGetValue(itemsCol, out var trackedItems))
                    {
                        foreach (var item in trackedItems.ToArray())
                        {
                            UnhookItemEvents(item);
                        }
                        trackedItems.Clear();
                    }
                    foreach (var item in itemsCol)
                    {
                        HookItemEvents(item, itemsCol);
                    }
                }
            }
            else
            {
                if (e.OldItems != null)
                {
                    foreach (DownloadItem item in e.OldItems)
                    {
                        UnhookItemEvents(item);
                        if (sender is ObservableCollection<DownloadItem> itemsCol &&
                            _collectionToItemsMap.TryGetValue(itemsCol, out var trackedItems))
                        {
                            trackedItems.Remove(item);
                        }
                    }
                }
                if (e.NewItems != null)
                {
                    foreach (DownloadItem item in e.NewItems)
                    {
                        if (sender is ObservableCollection<DownloadItem> itemsCol)
                        {
                            HookItemEvents(item, itemsCol);
                        }
                        else
                        {
                            HookItemEvents(item);
                        }
                    }
                }
            }
        }
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadItem.IsEnabled) or 
            nameof(DownloadItem.Status) or 
            nameof(DownloadItem.FileName) or 
            nameof(DownloadItem.SaveFilePath) or 
            nameof(DownloadItem.TotalBytes))
        {
            RequestSave();
        }
    }

    public void RequestSave()
    {
        if (!_isCustomPath && IsTestEnvironment) return;
        _isDirty = true;
        _debounceTimer.Change(300, Timeout.Infinite);
    }

    public void RequestThrottledSave()
    {
        if (!_isCustomPath && IsTestEnvironment) return;
        _isDirty = true;
        _debounceTimer.Change(1500, Timeout.Infinite);
    }

    private void OnDebounceTimerElapsed(object? state)
    {
        if (_isDirty && _trackedPackages != null)
        {
            List<DownloadPackageDto>? dtosSnapshot = null;

            try
            {
                var app = System.Windows.Application.Current;
                if (app?.Dispatcher != null && !app.Dispatcher.HasShutdownStarted && !app.Dispatcher.CheckAccess())
                {
                    app.Dispatcher.Invoke(() =>
                    {
                        if (_trackedPackages != null)
                        {
                            dtosSnapshot = CreateDtos(_trackedPackages);
                        }
                    });
                }
                else
                {
                    if (_trackedPackages != null)
                    {
                        dtosSnapshot = CreateDtos(_trackedPackages);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("[DownloadPersistenceService] Fehler beim Erstellen des UI-Snapshots", ex);
            }

            if (dtosSnapshot != null)
            {
                long seq = Interlocked.Increment(ref _latestDownloadsSaveSequence);
                bool success = SaveDownloadsInternal(dtosSnapshot, seq);
                if (success)
                {
                    _isDirty = false;
                }
                else
                {
                    _debounceTimer.Change(1500, Timeout.Infinite);
                }
            }
        }
    }

    private static List<DownloadPackageDto> CreateDtos(IEnumerable<DownloadPackage> packages)
    {
        DownloadPackage[] pkgArray;
        try
        {
            pkgArray = packages.ToArray();
        }
        catch
        {
            pkgArray = packages.ToList().ToArray();
        }

        return pkgArray.Select(pkg =>
        {
            DownloadItem[] itemsArray;
            try
            {
                itemsArray = pkg.Items.ToArray();
            }
            catch
            {
                itemsArray = pkg.Items.ToList().ToArray();
            }

            var itemDtos = itemsArray.Select(item =>
            {
                // Incomplete downloads are normalized to Paused (or Skipped) when saved
                var itemStatus = item.Status == DownloadStatus.Completed ? DownloadStatus.Completed : DownloadStatus.Paused;
                var itemStatusMsg = !item.IsEnabled ? Loc.Get("Status_Skipped") : (itemStatus == DownloadStatus.Completed ? Loc.Get("Status_Completed") : Loc.Get("Status_Paused"));

                long downloaded = item.DownloadedBytes;
                if (itemStatus != DownloadStatus.Completed && !string.IsNullOrWhiteSpace(item.SaveFilePath))
                {
                    var segBytes = GetSegmentedDownloadedBytes(item.SaveFilePath);
                    if (segBytes.HasValue)
                    {
                        downloaded = segBytes.Value;
                    }
                    else
                    {
                        var part = item.SaveFilePath + ".part";
                        if (File.Exists(part))
                        {
                            try
                            {
                                var len = new FileInfo(part).Length;
                                if (len > downloaded) downloaded = len;
                            }
                            catch { }
                        }
                    }
                }

                double progress = item.TotalBytes > 0 
                    ? Math.Clamp((double)downloaded / item.TotalBytes * 100.0, 0, 100) 
                    : (itemStatus == DownloadStatus.Completed ? 100.0 : 0);

                return new DownloadItemDto
                {
                    Id = item.Id,
                    PackageId = item.PackageId != Guid.Empty ? item.PackageId : pkg.Id,
                    OriginalUrl = item.OriginalUrl,
                    DirectDownloadUrl = item.DirectDownloadUrl,
                    FileName = item.FileName,
                    HosterName = item.HosterName,
                    HosterIconKey = item.HosterIconKey,
                    TotalBytes = item.TotalBytes,
                    DownloadedBytes = downloaded,
                    ProgressPercentage = progress,
                    Status = itemStatus,
                    StatusMessage = itemStatusMsg,
                    ErrorMessage = item.ErrorMessage,
                    SaveFilePath = item.SaveFilePath,
                    Cookies = item.Cookies,
                    UserAgent = item.UserAgent,
                    Referer = item.Referer,
                    IsEnabled = item.IsEnabled,
                    CreatedAt = item.CreatedAt,
                    StartedAt = item.StartedAt,
                    CompletedAt = item.CompletedAt,
                    ElapsedDurationMs = item.ElapsedDurationMs > 0 ? item.ElapsedDurationMs : (long)item.Duration.TotalMilliseconds
                };
            }).ToList();

            long pkgTotal = itemDtos.Sum(i => i.TotalBytes);
            long pkgDownloaded = itemDtos.Sum(i => i.DownloadedBytes);
            double pkgProgress = pkgTotal > 0 ? Math.Clamp((double)pkgDownloaded / pkgTotal * 100.0, 0, 100) : 0;
            var pkgStatus = (itemDtos.Count > 0 && itemDtos.Where(i => i.IsEnabled).All(i => i.Status == DownloadStatus.Completed))
                ? DownloadStatus.Completed
                : DownloadStatus.Paused;
            string pkgStatusMsg;
            if (!pkg.IsEnabled)
            {
                pkgStatusMsg = Loc.Get("Status_Skipped");
            }
            else if (pkgStatus == DownloadStatus.Completed)
            {
                if (!string.IsNullOrWhiteSpace(pkg.StatusMessage) &&
                    pkg.StatusMessage != Loc.Get("Status_Paused") &&
                    pkg.StatusMessage != Loc.Get("Status_Downloading") &&
                    pkg.StatusMessage != Loc.Get("Status_Queued"))
                {
                    pkgStatusMsg = pkg.StatusMessage;
                }
                else
                {
                    pkgStatusMsg = Loc.Get("Status_Completed");
                }
            }
            else
            {
                pkgStatusMsg = Loc.Get("Status_Paused");
            }

            var stepDtos = pkg.NextTaskSteps.Select(s => new NextTaskStepDto
            {
                Key = s.Key,
                Name = s.Name,
                State = s.State
            }).ToList();

            return new DownloadPackageDto
            {
                Id = pkg.Id,
                ParentPackageId = pkg.ParentPackageId,
                Name = pkg.Name,
                SaveDirectory = pkg.SaveDirectory,
                IsEnabled = pkg.IsEnabled,
                AutoExtractArchives = pkg.AutoExtractArchives,
                LowResourceExtraction = pkg.LowResourceExtraction,
                DeleteArchiveAfterExtraction = pkg.DeleteArchiveAfterExtraction,
                MoveArchiveToRecycleBin = pkg.MoveArchiveToRecycleBin,
                AutoResolveHostLinks = pkg.AutoResolveHostLinks,
                IsExpanded = pkg.IsExpanded,
                PackageIconKey = pkg.PackageIconKey,
                TotalBytes = pkgTotal > 0 ? pkgTotal : pkg.TotalBytes,
                DownloadedBytes = pkgDownloaded,
                ProgressPercentage = pkgProgress,
                Status = pkgStatus,
                StatusMessage = pkgStatusMsg,
                CreatedAt = pkg.CreatedAt,
                StartedAt = pkg.StartedAt,
                CompletedAt = pkg.CompletedAt,
                ElapsedDurationMs = pkg.ElapsedDurationMs > 0 ? pkg.ElapsedDurationMs : (long)pkg.Duration.TotalMilliseconds,
                NextTaskSteps = stepDtos,
                Items = itemDtos
            };
        }).ToList();
    }

    public static List<DownloadPackageDto> SnapshotDtos(IEnumerable<DownloadPackage> packages)
    {
        var app = System.Windows.Application.Current;
        if (app?.Dispatcher != null && !app.Dispatcher.HasShutdownStarted && !app.Dispatcher.CheckAccess())
        {
            try
            {
                return app.Dispatcher.Invoke(() => CreateDtos(packages));
            }
            catch
            {
                return CreateDtos(packages);
            }
        }

        return CreateDtos(packages);
    }

    public void SaveDownloads(IEnumerable<DownloadPackage> packages, bool sync = false)
    {
        if (!_isCustomPath && IsTestEnvironment) return;

        try
        {
            var dtos = SnapshotDtos(packages);
            if (sync || _isCustomPath || IsTestEnvironment)
            {
                SaveDownloadsInternal(dtos);
            }
            else
            {
                long seq = Interlocked.Increment(ref _latestDownloadsSaveSequence);
                Task.Run(() => SaveDownloadsInternal(dtos, seq));
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("[DownloadPersistenceService] Fehler beim Speichern der Downloads", ex);
        }
    }

    public void SaveDownloadsFromDtos(List<DownloadPackageDto> dtos, bool sync = false)
    {
        if (!_isCustomPath && IsTestEnvironment) return;

        if (sync || _isCustomPath || IsTestEnvironment)
        {
            SaveDownloadsInternal(dtos);
        }
        else
        {
            long seq = Interlocked.Increment(ref _latestDownloadsSaveSequence);
            Task.Run(() => SaveDownloadsInternal(dtos, seq));
        }
    }

    private bool SaveDownloadsInternal(List<DownloadPackageDto> dtos, long sequence = 0)
    {
        string? tempFile = null;
        try
        {
            var json = JsonSerializer.Serialize(dtos, _jsonOptions);

            lock (_fileLock)
            {
                if (sequence > 0 && sequence < Volatile.Read(ref _latestDownloadsSaveSequence))
                {
                    return true;
                }

                var dir = Path.GetDirectoryName(_downloadsFilePath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (File.Exists(_downloadsFilePath))
                {
                    try
                    {
                        var bakFile = _downloadsFilePath + ".bak";
                        File.Copy(_downloadsFilePath, bakFile, overwrite: true);
                    }
                    catch { }
                }

                var encryptedData = SecureAppDataStorage.EncryptString(json);
                tempFile = _downloadsFilePath + ".tmp";
                File.WriteAllText(tempFile, encryptedData);

                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        File.Move(tempFile, _downloadsFilePath, overwrite: true);
                        return true;
                    }
                    catch (IOException) when (i < 2)
                    {
                        Thread.Sleep(25 * (i + 1));
                    }
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Error("[DownloadPersistenceService] Fehler beim Schreiben der Downloads-Datei", ex);
            return false;
        }
        finally
        {
            if (tempFile != null)
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }
        }
    }

    public void SaveHistory(IEnumerable<DownloadPackage> historyPackages, bool sync = false)
    {
        if (!_isCustomPath && IsTestEnvironment) return;

        try
        {
            var dtos = SnapshotDtos(historyPackages);
            if (sync || _isCustomPath || IsTestEnvironment)
            {
                SaveHistoryInternal(dtos);
            }
            else
            {
                long seq = Interlocked.Increment(ref _latestHistorySaveSequence);
                Task.Run(() => SaveHistoryInternal(dtos, seq));
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("[DownloadPersistenceService] Fehler beim Speichern des Verlaufs", ex);
        }
    }

    private bool SaveHistoryInternal(List<DownloadPackageDto> dtos, long sequence = 0)
    {
        string? tempFile = null;
        try
        {
            var json = JsonSerializer.Serialize(dtos, _jsonOptions);

            lock (_fileLock)
            {
                if (sequence > 0 && sequence < Volatile.Read(ref _latestHistorySaveSequence))
                {
                    return true;
                }

                var dir = Path.GetDirectoryName(_historyFilePath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var encryptedData = SecureAppDataStorage.EncryptString(json);
                tempFile = _historyFilePath + ".tmp";
                File.WriteAllText(tempFile, encryptedData);

                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        File.Move(tempFile, _historyFilePath, overwrite: true);
                        return true;
                    }
                    catch (IOException) when (i < 2)
                    {
                        Thread.Sleep(25 * (i + 1));
                    }
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Error("[DownloadPersistenceService] Fehler beim Schreiben der Verlaufs-Datei", ex);
            return false;
        }
        finally
        {
            if (tempFile != null)
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }
        }
    }

    public List<DownloadPackage> LoadHistory()
    {
        var result = new List<DownloadPackage>();
        if (!_isCustomPath && IsTestEnvironment) return result;

        try
        {
            string? raw = null;

            lock (_fileLock)
            {
                if (File.Exists(_historyFilePath))
                {
                    raw = File.ReadAllText(_historyFilePath);
                }
            }

            if (string.IsNullOrWhiteSpace(raw))
                return result;

            List<DownloadPackageDto>? dtos = null;
            try
            {
                var json = SecureAppDataStorage.DecryptString(raw);
                dtos = JsonSerializer.Deserialize<List<DownloadPackageDto>>(json, _jsonOptions);
            }
            catch (Exception dex)
            {
                AppLogger.Error("[DownloadPersistenceService] Fehler beim Deserialisieren des Verlaufs", dex);
                try
                {
                    var corruptBackup = _historyFilePath + $".corrupt_{DateTime.Now:yyyyMMdd_HHmmss}.bak";
                    File.Copy(_historyFilePath, corruptBackup, overwrite: true);
                }
                catch { }
                return result;
            }

            if (dtos == null)
                return result;

            foreach (var pkgDto in dtos)
            {
                var cleanSaveDir = SanitizeSavedPackageDirectory(pkgDto.SaveDirectory, pkgDto.Name);
                var package = new DownloadPackage
                {
                    Id = pkgDto.Id,
                    ParentPackageId = pkgDto.ParentPackageId,
                    Name = pkgDto.Name,
                    SaveDirectory = cleanSaveDir,
                    IsEnabled = pkgDto.IsEnabled,
                    AutoExtractArchives = pkgDto.AutoExtractArchives,
                    LowResourceExtraction = pkgDto.LowResourceExtraction,
                    DeleteArchiveAfterExtraction = pkgDto.DeleteArchiveAfterExtraction,
                    MoveArchiveToRecycleBin = pkgDto.MoveArchiveToRecycleBin,
                    AutoResolveHostLinks = ResolveHostLinksOption(pkgDto),
                    IsExpanded = pkgDto.IsExpanded,
                    PackageIconKey = pkgDto.PackageIconKey,
                    CreatedAt = pkgDto.CreatedAt,
                    StartedAt = pkgDto.StartedAt,
                    CompletedAt = pkgDto.CompletedAt,
                    ElapsedDurationMs = pkgDto.ElapsedDurationMs
                };

                foreach (var itemDto in pkgDto.Items)
                {
                    var itemSavePath = itemDto.SaveFilePath;
                    if (!string.IsNullOrWhiteSpace(itemDto.FileName) &&
                        (string.IsNullOrWhiteSpace(itemSavePath) || !string.Equals(Path.GetDirectoryName(itemSavePath), cleanSaveDir, StringComparison.OrdinalIgnoreCase)))
                    {
                        itemSavePath = Path.Combine(cleanSaveDir, itemDto.FileName);
                    }

                    var item = new DownloadItem
                    {
                        Id = itemDto.Id,
                        PackageId = package.Id,
                        OriginalUrl = itemDto.OriginalUrl,
                        DirectDownloadUrl = itemDto.DirectDownloadUrl,
                        FileName = itemDto.FileName,
                        HosterName = itemDto.HosterName,
                        HosterIconKey = itemDto.HosterIconKey,
                        TotalBytes = itemDto.TotalBytes,
                        DownloadedBytes = itemDto.DownloadedBytes,
                        ErrorMessage = itemDto.ErrorMessage,
                        SaveFilePath = itemSavePath,
                        Cookies = itemDto.Cookies,
                        UserAgent = itemDto.UserAgent,
                        Referer = itemDto.Referer,
                        IsEnabled = itemDto.IsEnabled,
                        CreatedAt = itemDto.CreatedAt,
                        StartedAt = itemDto.StartedAt,
                        CompletedAt = itemDto.CompletedAt,
                        ElapsedDurationMs = itemDto.ElapsedDurationMs,
                        Status = itemDto.Status,
                        StatusMessage = itemDto.StatusMessage,
                        ProgressPercentage = itemDto.ProgressPercentage
                    };

                    package.Items.Add(item);
                }

                package.RecalculateAggregates();

                // Restore NextTaskStep states
                if (pkgDto.NextTaskSteps != null && pkgDto.NextTaskSteps.Count > 0)
                {
                    foreach (var stepDto in pkgDto.NextTaskSteps)
                    {
                        var step = package.NextTaskSteps.FirstOrDefault(s => s.Key == stepDto.Key);
                        if (step != null)
                        {
                            step.State = stepDto.State;
                        }
                    }
                }

                bool isExtracted = (pkgDto.StatusMessage == Loc.Get("Status_CompletedAndExtracted") ||
                                    pkgDto.StatusMessage == "Fertig & Entpackt" ||
                                    pkgDto.StatusMessage == "Completed & Extracted" ||
                                    (package.NextTaskSteps.Count > 0 && package.NextTaskSteps.All(s => s.State == NextTaskStepState.Done)));

                if (isExtracted && package.AutoExtractArchives)
                {
                    package.StatusMessage = Loc.Get("Status_CompletedAndExtracted");
                    foreach (var step in package.NextTaskSteps)
                    {
                        step.State = NextTaskStepState.Done;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(pkgDto.StatusMessage) && 
                         (pkgDto.StatusMessage == Loc.Get("Status_CompletedExtractionError") || 
                          pkgDto.StatusMessage == "Entpackfehler" || 
                          pkgDto.StatusMessage == "Extraction error"))
                {
                    package.StatusMessage = Loc.Get("Status_CompletedExtractionError");
                }

                if (package.CheckIsFullyCompleted())
                {
                    package.HasCompletedNotified = true;
                }

                result.Add(package);
            }

            RelinkPackageHierarchy(result);
        }
        catch (Exception ex)
        {
            AppLogger.Error("[DownloadPersistenceService] Fehler beim Laden des Verlaufs", ex);
        }

        return result;
    }

    public List<DownloadPackage> LoadDownloads()
    {
        var result = new List<DownloadPackage>();
        if (!_isCustomPath && IsTestEnvironment) return result;

        try
        {
            string? raw = null;

            lock (_fileLock)
            {
                if (File.Exists(_downloadsFilePath))
                {
                    try
                    {
                        raw = File.ReadAllText(_downloadsFilePath);
                    }
                    catch { }
                }
                else
                {
                    var bakFile = _downloadsFilePath + ".bak";
                    if (File.Exists(bakFile))
                    {
                        try
                        {
                            raw = File.ReadAllText(bakFile);
                            if (!IsTestEnvironment) AppLogger.Warn("[DownloadPersistenceService] downloads.json fehlte, aus downloads.json.bak geladen.");
                        }
                        catch { }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(raw))
                return result;

            List<DownloadPackageDto>? dtos = null;
            try
            {
                var json = SecureAppDataStorage.DecryptString(raw);
                dtos = JsonSerializer.Deserialize<List<DownloadPackageDto>>(json, _jsonOptions);
            }
            catch (Exception dex)
            {
                if (!IsTestEnvironment) AppLogger.Error("[DownloadPersistenceService] Fehler beim Deserialisieren der Downloads", dex);
                try
                {
                    var corruptBackup = _downloadsFilePath + $".corrupt_{DateTime.Now:yyyyMMdd_HHmmss}.bak";
                    File.Copy(_downloadsFilePath, corruptBackup, overwrite: true);

                    var bakFile = _downloadsFilePath + ".bak";
                    if (File.Exists(bakFile))
                    {
                        var bakRaw = File.ReadAllText(bakFile);
                        var bakJson = SecureAppDataStorage.DecryptString(bakRaw);
                        dtos = JsonSerializer.Deserialize<List<DownloadPackageDto>>(bakJson, _jsonOptions);
                        if (dtos != null && !IsTestEnvironment)
                        {
                            AppLogger.Warn("[DownloadPersistenceService] Downloads erfolgreich aus Backup wiederhergestellt.");
                        }
                    }
                }
                catch { }

                if (dtos == null)
                    return result;
            }

            if (dtos == null)
                return result;

            foreach (var pkgDto in dtos)
            {
                var cleanSaveDir = SanitizeSavedPackageDirectory(pkgDto.SaveDirectory, pkgDto.Name);
                var package = new DownloadPackage
                {
                    Id = pkgDto.Id,
                    ParentPackageId = pkgDto.ParentPackageId,
                    Name = pkgDto.Name,
                    SaveDirectory = cleanSaveDir,
                    IsEnabled = pkgDto.IsEnabled,
                    AutoExtractArchives = pkgDto.AutoExtractArchives,
                    LowResourceExtraction = pkgDto.LowResourceExtraction,
                    DeleteArchiveAfterExtraction = pkgDto.DeleteArchiveAfterExtraction,
                    MoveArchiveToRecycleBin = pkgDto.MoveArchiveToRecycleBin,
                    AutoResolveHostLinks = ResolveHostLinksOption(pkgDto),
                    IsExpanded = pkgDto.IsExpanded,
                    PackageIconKey = pkgDto.PackageIconKey,
                    CreatedAt = pkgDto.CreatedAt,
                    StartedAt = pkgDto.StartedAt,
                    CompletedAt = pkgDto.CompletedAt,
                    ElapsedDurationMs = pkgDto.ElapsedDurationMs
                };

                foreach (var itemDto in pkgDto.Items)
                {
                    var itemSavePath = itemDto.SaveFilePath;
                    if (!string.IsNullOrWhiteSpace(itemDto.FileName) &&
                        (string.IsNullOrWhiteSpace(itemSavePath) || !string.Equals(Path.GetDirectoryName(itemSavePath), cleanSaveDir, StringComparison.OrdinalIgnoreCase)))
                    {
                        itemSavePath = Path.Combine(cleanSaveDir, itemDto.FileName);
                    }

                    var item = new DownloadItem
                    {
                        Id = itemDto.Id,
                        PackageId = package.Id,
                        OriginalUrl = itemDto.OriginalUrl,
                        DirectDownloadUrl = itemDto.DirectDownloadUrl,
                        FileName = itemDto.FileName,
                        HosterName = itemDto.HosterName,
                        HosterIconKey = itemDto.HosterIconKey,
                        TotalBytes = itemDto.TotalBytes,
                        DownloadedBytes = itemDto.DownloadedBytes,
                        ErrorMessage = itemDto.ErrorMessage,
                        SaveFilePath = itemSavePath,
                        Cookies = itemDto.Cookies,
                        UserAgent = itemDto.UserAgent,
                        Referer = itemDto.Referer,
                        IsEnabled = itemDto.IsEnabled,
                        CreatedAt = itemDto.CreatedAt,
                        StartedAt = itemDto.StartedAt,
                        CompletedAt = itemDto.CompletedAt,
                        ElapsedDurationMs = itemDto.ElapsedDurationMs
                    };

                    if (!string.IsNullOrWhiteSpace(item.SaveFilePath))
                    {
                        var partFile = item.SaveFilePath + ".part";
                        if (File.Exists(partFile))
                        {
                            try
                            {
                                // For chunked downloads, the .part file is pre-allocated to full size;
                                // actual progress resides in the .segments sidecar (sum of Done values).
                                var segmentedBytes = GetSegmentedDownloadedBytes(item.SaveFilePath);
                                if (segmentedBytes.HasValue)
                                {
                                    item.DownloadedBytes = segmentedBytes.Value;
                                }
                                else
                                {
                                    var info = new FileInfo(partFile);
                                    if (info.Length > item.DownloadedBytes)
                                    {
                                        item.DownloadedBytes = info.Length;
                                    }
                                }
                            }
                            catch { }
                        }
                    }

                    if (itemDto.Status == DownloadStatus.Completed)
                    {
                        item.Status = DownloadStatus.Completed;
                        item.StatusMessage = Loc.Get("Status_Completed");
                        item.ProgressPercentage = 100.0;
                    }
                    else
                    {
                        item.Status = DownloadStatus.Paused;
                        if (!item.IsEnabled)
                        {
                            item.StatusMessage = Loc.Get("Status_Skipped");
                        }
                        else
                        {
                            item.StatusMessage = Loc.Get("Status_Paused");
                        }

                        if (item.TotalBytes > 0)
                        {
                            item.ProgressPercentage = Math.Clamp((double)item.DownloadedBytes / item.TotalBytes * 100.0, 0, 100);
                        }
                    }

                    item.SpeedBytesPerSecond = 0;
                    item.RemainingSeconds = 0;

                    package.Items.Add(item);
                }

                package.RecalculateAggregates();

                // Restore NextTaskStep states
                if (pkgDto.NextTaskSteps != null && pkgDto.NextTaskSteps.Count > 0)
                {
                    foreach (var stepDto in pkgDto.NextTaskSteps)
                    {
                        var step = package.NextTaskSteps.FirstOrDefault(s => s.Key == stepDto.Key);
                        if (step != null)
                        {
                            step.State = stepDto.State;
                        }
                    }
                }

                bool isExtracted = (pkgDto.StatusMessage == Loc.Get("Status_CompletedAndExtracted") ||
                                    pkgDto.StatusMessage == "Fertig & Entpackt" ||
                                    pkgDto.StatusMessage == "Completed & Extracted" ||
                                    (package.NextTaskSteps.Count > 0 && package.NextTaskSteps.All(s => s.State == NextTaskStepState.Done)));

                if (isExtracted && package.AutoExtractArchives)
                {
                    package.StatusMessage = Loc.Get("Status_CompletedAndExtracted");
                    foreach (var step in package.NextTaskSteps)
                    {
                        step.State = NextTaskStepState.Done;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(pkgDto.StatusMessage) && 
                         (pkgDto.StatusMessage == Loc.Get("Status_CompletedExtractionError") || 
                          pkgDto.StatusMessage == "Entpackfehler" || 
                          pkgDto.StatusMessage == "Extraction error"))
                {
                    package.StatusMessage = Loc.Get("Status_CompletedExtractionError");
                }

                if (package.CheckIsFullyCompleted())
                {
                    package.HasCompletedNotified = true;
                }

                result.Add(package);
            }

            RelinkPackageHierarchy(result);
        }
        catch (Exception ex)
        {
            AppLogger.Error("[DownloadPersistenceService] Fehler beim Laden der Downloads", ex);
        }

        return result;
    }

    /// <summary>
    /// Links loaded packages to the ClippedPackages of their parent packages based on ParentPackageId.
    /// </summary>
    public static void RelinkPackageHierarchy(IEnumerable<DownloadPackage> packages)
    {
        if (packages == null) return;
        var list = packages.ToList();

        foreach (var pkg in list)
        {
            pkg.ClippedPackages.Clear();
        }

        foreach (var pkg in list)
        {
            if (pkg.ParentPackageId.HasValue)
            {
                var parent = list.FirstOrDefault(p => p.Id == pkg.ParentPackageId.Value);
                if (parent != null && parent != pkg)
                {
                    pkg.ParentPackageName = parent.Name;
                    if (!parent.ClippedPackages.Contains(pkg))
                    {
                        parent.ClippedPackages.Add(pkg);
                    }
                }
                else
                {
                    pkg.ParentPackageId = null;
                    pkg.ParentPackageName = null;
                }
            }
        }
    }

    public static string SanitizeSavedPackageDirectory(string? savedDir, string? packageName)
    {
        if (string.IsNullOrWhiteSpace(savedDir))
            return string.Empty;

        var dirName = Path.GetFileName(savedDir.TrimEnd('\\', '/'));
        if (!string.IsNullOrWhiteSpace(dirName) &&
            (Extractor.PackageGrouper.IsGenericOrCrypticName(dirName) || Extractor.LinkExtractor.IsPureNumericOrHash(dirName)) &&
            !string.IsNullOrWhiteSpace(packageName) &&
            !Extractor.PackageGrouper.IsGenericOrCrypticName(packageName))
        {
            var parentDir = Path.GetDirectoryName(savedDir.TrimEnd('\\', '/'));
            if (!string.IsNullOrWhiteSpace(parentDir))
            {
                return Path.Combine(parentDir, Extractor.PackageGrouper.MakeSafeDirectoryName(packageName));
            }
        }

        return savedDir;
    }

    private static bool ResolveHostLinksOption(DownloadPackageDto pkgDto)
    {
        return pkgDto.AutoResolveHostLinks;
    }
}
