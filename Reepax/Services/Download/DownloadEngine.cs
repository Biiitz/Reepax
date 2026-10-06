using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32.SafeHandles;
using Reepax.Models;
using Reepax.Services.Localization;
using Reepax.Services.SystemIntegration;

namespace Reepax.Services.Download;

public class DownloadEngine
{
    private static readonly Lazy<DownloadEngine> _instance = new(() => new DownloadEngine());
    public static DownloadEngine Instance => _instance.Value;

    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<Guid, (CancellationTokenSource Cts, TaskCompletionSource<bool> Tcs)> _activeDownloads = new();
    private readonly BandwidthThrottler _throttler = new();

    public int ActiveDownloadsCount => _activeDownloads.Count;
    public bool IsDownloading(Guid itemId) => _activeDownloads.ContainsKey(itemId);
    public long CurrentSpeedLimit => _throttler.MaxBytesPerSecond;

    private void CompleteAndRemoveActiveDownload(Guid itemId)
    {
        if (_activeDownloads.TryRemove(itemId, out var entry))
        {
            try { entry.Cts.Dispose(); } catch { }
            entry.Tcs.TrySetResult(true);
        }
    }

    public Task? GetDownloadTask(Guid itemId)
    {
        if (_activeDownloads.TryGetValue(itemId, out var entry))
        {
            return entry.Tcs.Task;
        }
        return null;
    }

    /// <summary>
    /// Number of parallel connections (chunks) per download (1-20, default 5).
    /// Synchronized from settings. Servers without range support
    /// automatically fall back to a single stream.
    /// </summary>
    public int MaxConnectionsPerDownload { get; set; } = 5;

    private const long MinBytesForChunking = 8L * 1024 * 1024;   // Chunking threshold: at least 8 MB file size
    private const long MinSegmentBytes = 2L * 1024 * 1024;       // Minimum size per segment

    public void SetSpeedLimit(long maxBytesPerSecond)
    {
        _throttler.MaxBytesPerSecond = maxBytesPerSecond;
    }

    public event Action<DownloadItem>? DownloadCompleted;
    public event Action<DownloadItem, Exception>? DownloadFailed;
    public event Action<DownloadItem>? DownloadCancelled;
    public event Action<DownloadItem>? DownloadProgressUpdated;

    /// <summary>
    /// For unit tests: triggers DownloadCompleted event directly.
    /// </summary>
    internal void TriggerDownloadCompletedForTesting(DownloadItem item)
    {
        DownloadCompleted?.Invoke(item);
    }

    /// <summary>
    /// For unit tests: triggers DownloadFailed event directly.
    /// </summary>
    internal void TriggerDownloadFailedForTesting(DownloadItem item, Exception ex)
    {
        DownloadFailed?.Invoke(item, ex);
    }

    /// <summary>
    /// For unit tests: triggers DownloadCancelled event directly.
    /// </summary>
    internal void TriggerDownloadCancelledForTesting(DownloadItem item)
    {
        DownloadCancelled?.Invoke(item);
    }

    public DownloadEngine(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? HttpUserAgentService.CreateHttpClient(TimeSpan.FromHours(6), HttpContentType.BinaryOrAny);

        HttpUserAgentService.UserAgentChanged += _ =>
        {
            try
            {
                HttpUserAgentService.ApplyDefaultBrowserHeaders(_httpClient.DefaultRequestHeaders, null, HttpContentType.BinaryOrAny);
            }
            catch { }
        };
    }

    public async Task StartDownloadAsync(
        DownloadItem item, 
        string directUrl, 
        string? cookieHeader, 
        string? userAgent, 
        string? referer,
        string? suggestedFileName)
    {
        var cts = new CancellationTokenSource();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_activeDownloads.TryAdd(item.Id, (cts, tcs)))
        {
            cts.Dispose();
            return;
        }

        SafeInvokeAsync(() =>
        {
            item.DirectDownloadUrl = directUrl;
            item.Cookies = cookieHeader;
            item.UserAgent = userAgent;
            item.Referer = referer;
            item.StartedAt ??= DateTime.Now;
            item.Status = DownloadStatus.Downloading;
            item.StatusMessage = Loc.Get("Status_Connecting");
            item.ErrorMessage = null;
        });

        var downloadTask = Task.Run(async () =>
        {
            try
            {
                await ExecuteDownloadLoopAsync(item, cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SafeInvokeAsync(() =>
                {
                    item.Status = DownloadStatus.Failed;
                    item.StatusMessage = Loc.Get("Status_ErrorStarting");
                    item.ErrorMessage = ex.Message;
                });
                CompleteAndRemoveActiveDownload(item.Id);
                DownloadFailed?.Invoke(item, ex);
            }
            finally
            {
                CompleteAndRemoveActiveDownload(item.Id);
            }
        });

        await downloadTask;
    }

    public async Task ResumeDownloadAsync(DownloadItem item)
    {
        if (string.IsNullOrWhiteSpace(item.DirectDownloadUrl))
            return;

        var cts = new CancellationTokenSource();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_activeDownloads.TryAdd(item.Id, (cts, tcs)))
        {
            cts.Dispose();
            return;
        }

        SafeInvokeAsync(() =>
        {
            item.Status = DownloadStatus.Downloading;
            item.StatusMessage = Loc.Get("Status_ResumingDownload");
            item.ErrorMessage = null;
        });

        var downloadTask = Task.Run(async () =>
        {
            try
            {
                await ExecuteDownloadLoopAsync(item, cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SafeInvokeAsync(() =>
                {
                    item.Status = DownloadStatus.Failed;
                    item.StatusMessage = Loc.Get("Status_ErrorResuming");
                    item.ErrorMessage = ex.Message;
                });
                CompleteAndRemoveActiveDownload(item.Id);
                DownloadFailed?.Invoke(item, ex);
            }
            finally
            {
                CompleteAndRemoveActiveDownload(item.Id);
            }
        });

        await downloadTask;
    }

    private async Task ExecuteDownloadLoopAsync(DownloadItem item, CancellationToken cancellationToken)
    {
        var buffer = new byte[256 * 1024]; // 256 KB high-performance buffer
        string? tempFilePath = null;

        try
        {
            if (string.IsNullOrWhiteSpace(item.DirectDownloadUrl))
            {
                throw new InvalidOperationException("Direct download URL is empty.");
            }

            if (!item.DirectDownloadUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Unsupported URL scheme for download: {item.DirectDownloadUrl[..Math.Min(30, item.DirectDownloadUrl.Length)]}");
            }

            // Ensure package name is updated if item already has a meaningful name
            SafeInvoke(() =>
            {
                var pkg = QueueManager.Instance.Packages.FirstOrDefault(p => p.Id == item.PackageId || p.Items.Contains(item));
                if (pkg != null && !pkg.IsCustomName && (Extractor.PackageGrouper.IsGenericOrCrypticName(pkg.Name) || Extractor.UpdateDetector.IsUpdate(item.FileName)))
                {
                    Extractor.LinkMetadataResolverService.TryUpdatePackageName(pkg);
                }
            });

            // Ensure destination folder exists physically
            var destinationPath = item.SaveFilePath;
            if (string.IsNullOrWhiteSpace(destinationPath))
            {
                throw new InvalidOperationException("Destination path is not defined.");
            }

            var destinationDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDir) && !Directory.Exists(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
            }

            tempFilePath = destinationPath + ".part";

            long existingBytes = 0;
            if (File.Exists(tempFilePath))
            {
                existingBytes = new FileInfo(tempFilePath).Length;
            }

            // === Multi-Connection Download (Chunking) ===
            if (MaxConnectionsPerDownload > 1)
            {
                var segMetaPath = tempFilePath + ".segments";

                // 1. Resume an earlier chunked download (sidecar file exists)
                var segState = LoadSegmentState(segMetaPath);
                if (segState != null && File.Exists(tempFilePath))
                {
                    try
                    {
                        await ExecuteChunkedDownloadAsync(item, segState, tempFilePath, segMetaPath, cancellationToken);
                        return;
                    }
                    catch (Exception ex) when (IsRangeNotSupported(ex))
                    {
                        await FallbackToSingleConnectionDownloadAsync(item, tempFilePath, segMetaPath, buffer, cancellationToken);
                        return;
                    }
                }
                else if (File.Exists(segMetaPath))
                {
                    try { File.Delete(segMetaPath); } catch { }
                }

                // 2. Fresh download: probe server for range support and chunk if supported
                if (existingBytes == 0)
                {
                    var probe = await ProbeRangeSupportAsync(item, cancellationToken);
                    if (probe.IsSupported && probe.TotalBytes >= MinBytesForChunking)
                    {
                        int segmentCount = (int)Math.Clamp(
                            Math.Min(MaxConnectionsPerDownload, probe.TotalBytes / MinSegmentBytes),
                            2, 20);

                        segState = CreateSegments(probe.TotalBytes, segmentCount);

                        // Adopt filename from Content-Disposition of probe response
                        if (!item.IsCustomName &&
                            !string.IsNullOrWhiteSpace(probe.FileName) &&
                            !string.Equals(item.FileName, probe.FileName, StringComparison.OrdinalIgnoreCase))
                        {
                            var oldTemp = tempFilePath;
                            SafeInvoke(() =>
                            {
                                item.Rename(probe.FileName, isUserAction: false);
                                var pkg = QueueManager.Instance.Packages.FirstOrDefault(p => p.Id == item.PackageId || p.Items.Contains(item));
                                if (pkg != null && !pkg.IsCustomName)
                                {
                                    Extractor.LinkMetadataResolverService.TryUpdatePackageName(pkg);
                                }
                            });

                            var newDestDir = Path.GetDirectoryName(item.SaveFilePath);
                            if (!string.IsNullOrWhiteSpace(newDestDir) && !Directory.Exists(newDestDir))
                            {
                                Directory.CreateDirectory(newDestDir);
                            }

                            var renamedTemp = item.SaveFilePath + ".part";
                            if (!string.Equals(renamedTemp, tempFilePath, StringComparison.OrdinalIgnoreCase))
                            {
                                if (!string.IsNullOrWhiteSpace(oldTemp) && File.Exists(oldTemp))
                                {
                                    try { File.Move(oldTemp, renamedTemp, true); } catch { }
                                }
                                tempFilePath = renamedTemp;
                                segMetaPath = tempFilePath + ".segments";
                            }
                        }

                        SaveSegmentState(segMetaPath, segState);
                        try
                        {
                            await ExecuteChunkedDownloadAsync(item, segState, tempFilePath, segMetaPath, cancellationToken);
                            return;
                        }
                        catch (Exception ex) when (IsRangeNotSupported(ex))
                        {
                            await FallbackToSingleConnectionDownloadAsync(item, tempFilePath, segMetaPath, buffer, cancellationToken);
                            return;
                        }
                    }
                }
            }

            await ExecuteSingleConnectionDownloadAsync(item, tempFilePath, cancellationToken, buffer);
        }
        catch (OperationCanceledException)
        {
            SafeInvokeAsync(() =>
            {
                if (item.Status != DownloadStatus.Queued)
                {
                    item.Status = DownloadStatus.Paused;
                    item.StatusMessage = !item.IsEnabled ? Loc.Get("Status_Skipped") : Loc.Get("Status_Paused");
                }
                item.SpeedBytesPerSecond = 0;
                item.RemainingSeconds = 0;
                if (item.TotalBytes > 0)
                {
                    item.ProgressPercentage = Math.Clamp((double)item.DownloadedBytes / item.TotalBytes * 100.0, 0, 100);
                }
            });

            CompleteAndRemoveActiveDownload(item.Id);
            DownloadCancelled?.Invoke(item);
        }
        catch (Exception ex)
        {
            SafeInvokeAsync(() =>
            {
                item.Status = DownloadStatus.Failed;
                item.StatusMessage = Loc.Get("Status_Failed");
                item.ErrorMessage = ex.Message;
                item.SpeedBytesPerSecond = 0;
                item.RemainingSeconds = 0;
            });

            CompleteAndRemoveActiveDownload(item.Id);
            DownloadFailed?.Invoke(item, ex);
        }
    }

    internal async Task ExecuteSingleConnectionDownloadAsync(
        DownloadItem item, 
        string tempFilePath, 
        CancellationToken cancellationToken, 
        byte[]? buffer = null)
    {
        buffer ??= new byte[256 * 1024];
        var destinationPath = item.SaveFilePath;
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new InvalidOperationException("Destination path is not defined.");
        }

        long existingBytes = 0;
        if (File.Exists(tempFilePath))
        {
            existingBytes = new FileInfo(tempFilePath).Length;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, item.DirectDownloadUrl);
        ApplyRequestHeaders(request, item);

        // Resume support with HTTP Range header
        if (existingBytes > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingBytes, null);
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // Range invalid or file changed on server, restart file
            existingBytes = 0;
            if (File.Exists(tempFilePath))
            {
                Extractor.ArchiveExtractionService.DeleteOrMoveToTemp(tempFilePath);
            }

            using var freshRequest = new HttpRequestMessage(HttpMethod.Get, item.DirectDownloadUrl);
            ApplyRequestHeaders(freshRequest, item);

            using var freshResponse = await _httpClient.SendAsync(freshRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            freshResponse.EnsureSuccessStatusCode();
            await ProcessResponseStreamAsync(item, freshResponse, tempFilePath, destinationPath, buffer, cancellationToken);
            return;
        }

        response.EnsureSuccessStatusCode();

        // If server returned 200 OK instead of 206 Partial Content, the server does not support ranges
        // and sent the full file from byte 0. In that case, reset existingBytes to 0 and overwrite .part file.
        bool isPartial = response.StatusCode == HttpStatusCode.PartialContent;
        long effectiveInitialBytes = isPartial ? existingBytes : 0;

        await ProcessResponseStreamAsync(item, response, tempFilePath, destinationPath, buffer, cancellationToken, effectiveInitialBytes);
    }

    private async Task FallbackToSingleConnectionDownloadAsync(
        DownloadItem item, 
        string tempFilePath, 
        string segMetaPath, 
        byte[] buffer, 
        CancellationToken cancellationToken)
    {
        Services.Storage.AppLogger.Warn($"[DownloadEngine] Range requests refused by remote server for '{item.FileName}'. Falling back to clean single-connection stream.");

        try
        {
            if (File.Exists(segMetaPath))
            {
                File.Delete(segMetaPath);
            }
        }
        catch { }

        try
        {
            if (File.Exists(tempFilePath))
            {
                using var fs = new FileStream(tempFilePath, FileMode.Truncate, FileAccess.Write, FileShare.None);
            }
        }
        catch
        {
            try { File.Delete(tempFilePath); } catch { }
        }

        item.DownloadedBytes = 0;
        item.ProgressPercentage = 0;
        SafeInvokeAsync(() =>
        {
            item.DownloadedBytes = 0;
            item.ProgressPercentage = 0;
        });

        await ExecuteSingleConnectionDownloadAsync(item, tempFilePath, cancellationToken, buffer);
    }

    private static bool IsRangeNotSupported(Exception ex)
    {
        if (ex is RangeNotSupportedException)
            return true;

        if (ex is AggregateException agg)
        {
            return agg.InnerExceptions.Any(IsRangeNotSupported);
        }

        if (ex.InnerException != null && IsRangeNotSupported(ex.InnerException))
        {
            return true;
        }

        var msg = ex.Message;
        if (msg.Contains("Range requests not supported", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Server did not accept the range request", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("no 206 Partial Content", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Range not supported", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }


    // ==================== Multi-Connection Chunking ====================

    private sealed class SegmentState
    {
        public long Start { get; set; }
        public long End { get; set; }
        public long Done { get; set; }
    }

    private sealed class SegmentFileState
    {
        public long TotalBytes { get; set; }
        public List<SegmentState> Segments { get; set; } = new();
    }

    private static SegmentFileState CreateSegments(long totalBytes, int segmentCount)
    {
        var list = new List<SegmentState>(segmentCount);
        long segSize = totalBytes / segmentCount;
        for (int i = 0; i < segmentCount; i++)
        {
            long start = i * segSize;
            long end = (i == segmentCount - 1) ? totalBytes - 1 : start + segSize - 1;
            list.Add(new SegmentState { Start = start, End = end, Done = 0 });
        }
        return new SegmentFileState { TotalBytes = totalBytes, Segments = list };
    }

    private static SegmentFileState? LoadSegmentState(string segMetaPath)
    {
        try
        {
            if (!File.Exists(segMetaPath))
                return null;

            var state = JsonSerializer.Deserialize<SegmentFileState>(File.ReadAllText(segMetaPath));
            if (state == null || state.TotalBytes <= 0 || state.Segments.Count == 0)
                return null;

            // Validation: segments must cover the range 0..TotalBytes-1 without gaps
            long expectedStart = 0;
            foreach (var seg in state.Segments)
            {
                if (seg.Start != expectedStart || seg.End < seg.Start ||
                    seg.Done < 0 || seg.Done > seg.End - seg.Start + 1)
                    return null;
                expectedStart = seg.End + 1;
            }
            if (expectedStart != state.TotalBytes)
                return null;

            return state;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveSegmentState(string segMetaPath, SegmentFileState state)
    {
        try
        {
            File.WriteAllText(segMetaPath, JsonSerializer.Serialize(state));
        }
        catch { }
    }

    private static void ApplyRequestHeaders(HttpRequestMessage request, DownloadItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Cookies))
            request.Headers.TryAddWithoutValidation("Cookie", item.Cookies);
        if (!string.IsNullOrWhiteSpace(item.UserAgent))
            request.Headers.TryAddWithoutValidation("User-Agent", item.UserAgent);
        if (!string.IsNullOrWhiteSpace(item.Referer))
            request.Headers.TryAddWithoutValidation("Referer", item.Referer);
    }

    /// <summary>
    /// Probes via HTTP range (bytes=0-0) if the server supports partial content,
    /// retrieving total size and filename.
    /// </summary>
    private async Task<(bool IsSupported, long TotalBytes, string? FileName)> ProbeRangeSupportAsync(DownloadItem item, CancellationToken cancellationToken)
    {
        try
        {
            // Only HTTP(S) is probeable (not blob: etc.)
            if (string.IsNullOrWhiteSpace(item.DirectDownloadUrl) ||
                !item.DirectDownloadUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return (false, 0, null);

            using var request = new HttpRequestMessage(HttpMethod.Get, item.DirectDownloadUrl);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            ApplyRequestHeaders(request, item);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.PartialContent &&
                response.Content.Headers.ContentRange?.Length is > 0)
            {
                return (true, response.Content.Headers.ContentRange.Length.Value,
                        ExtractContentDispositionFileName(response));
            }
        }
        catch (Exception ex)
        {
            Services.Storage.AppLogger.Warn($"[DownloadEngine] Range probe for '{item.FileName}' failed: {ex.Message}");
        }

        return (false, 0, null);
    }

    /// <summary>
    /// Downloads a file using multiple parallel connections (HTTP range chunks).
    /// Segment progress is persisted in a sidecar file for crash-resilient resumes.
    /// </summary>
    private async Task ExecuteChunkedDownloadAsync(
        DownloadItem item,
        SegmentFileState state,
        string tempFilePath,
        string segMetaPath,
        CancellationToken cancellationToken)
    {
        var totalBytes = state.TotalBytes;
        var segments = state.Segments;
        long initialDownloaded = segments.Sum(s => s.Done);

        // Preallocate target file to full size (instant on NTFS without writing zeroes)
        {
            using var prealloc = new FileStream(tempFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (prealloc.Length != totalBytes)
                prealloc.SetLength(totalBytes);
        }

        var speedTracker = new RollingSpeedTracker(initialDownloaded);
        // Shared trickle throttler across ALL segments -> 500 KB/s in paused mode
        var trickleThrottler = new BandwidthThrottler { MaxBytesPerSecond = 500 * 1024 };
        var segmentsFinished = false;

        SafeInvokeAsync(() =>
        {
            item.TotalBytes = totalBytes;
            item.DownloadedBytes = initialDownloaded;
            item.Status = DownloadStatus.Downloading;
            item.StatusMessage = Loc.Get("Status_Downloading");
            item.StartedAt ??= DateTime.Now;
        });

        // Watcher: aggregates segment progress 4x per second for UI + sidecar persistence
        var watcher = Task.Run(async () =>
        {
            long lastSaveTs = Stopwatch.GetTimestamp();
            while (!Volatile.Read(ref segmentsFinished) && !cancellationToken.IsCancellationRequested)
            {
                try { await Task.Delay(250, cancellationToken); }
                catch (OperationCanceledException) { break; }

                long aggregated = segments.Sum(s => s.Done);
                var speed = speedTracker.CalculateSpeed(aggregated);
                SafeInvokeAsync(() => item.UpdateProgress(aggregated, totalBytes, speed));
                DownloadProgressUpdated?.Invoke(item);

                var now = Stopwatch.GetTimestamp();
                if ((double)(now - lastSaveTs) / Stopwatch.Frequency >= 2.0)
                {
                    SaveSegmentState(segMetaPath, state);
                    lastSaveTs = now;
                }
            }
        });

        try
        {
            using var fs = new FileStream(tempFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 256 * 1024, FileOptions.Asynchronous);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var tasks = segments.Select(async seg =>
            {
                try
                {
                    await DownloadSegmentAsync(item, seg, fs.SafeFileHandle, trickleThrottler, linkedCts.Token);
                }
                catch (Exception ex) when (IsRangeNotSupported(ex))
                {
                    try { linkedCts.Cancel(); } catch { }
                    throw;
                }
            });
            var allTask = Task.WhenAll(tasks);
            try
            {
                await allTask;
            }
            catch
            {
                if (allTask.Exception != null)
                {
                    var rangeEx = allTask.Exception.InnerExceptions.FirstOrDefault(IsRangeNotSupported);
                    if (rangeEx != null)
                    {
                        throw rangeEx;
                    }
                }
                throw;
            }
        }
        finally
        {
            Volatile.Write(ref segmentsFinished, true);
            try { await watcher; } catch { }
            SaveSegmentState(segMetaPath, state);
            long doneBytes = segments.Sum(s => s.Done);
            SafeInvokeAsync(() =>
            {
                item.DownloadedBytes = doneBytes;
                if (totalBytes > 0)
                {
                    item.ProgressPercentage = Math.Clamp((double)doneBytes / totalBytes * 100.0, 0, 100);
                }
                item.SpeedBytesPerSecond = 0;
                item.RemainingSeconds = 0;
            });
        }

        // Verify completeness
        long written = segments.Sum(s => s.Done);
        if (written < totalBytes)
        {
            throw new IOException($"Download was interrupted: Only {written} of {totalBytes} bytes were transferred.");
        }

        // Completion: .part -> final filename
        var actualFinalPath = !string.IsNullOrWhiteSpace(item.SaveFilePath) ? item.SaveFilePath : tempFilePath[..^5];
        var targetDir = Path.GetDirectoryName(actualFinalPath);
        if (!string.IsNullOrWhiteSpace(targetDir) && !Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        File.Move(tempFilePath, actualFinalPath, overwrite: true);
        Extractor.ArchiveExtractionService.DeleteOrMoveToTemp(segMetaPath);

        SafeInvoke(() =>
        {
            item.DownloadedBytes = written;
            item.ProgressPercentage = 100;
            item.SpeedBytesPerSecond = 0;
            item.RemainingSeconds = 0;
            item.Status = DownloadStatus.Completed;
            item.StatusMessage = Loc.Get("Status_Completed");
            item.CompletedAt = DateTime.Now;
            if (item.StartedAt.HasValue && item.CompletedAt.Value >= item.StartedAt.Value)
            {
                item.ElapsedDurationMs = (long)(item.CompletedAt.Value - item.StartedAt.Value).TotalMilliseconds;
            }
        });

        CompleteAndRemoveActiveDownload(item.Id);
        DownloadCompleted?.Invoke(item);
    }

    /// <summary>
    /// Downloads a single byte-range segment into the preallocated .part file.
    /// Internal retries (3x) on network errors; progress is preserved.
    /// </summary>
    private async Task DownloadSegmentAsync(
        DownloadItem item,
        SegmentState segment,
        SafeFileHandle fileHandle,
        BandwidthThrottler trickleThrottler,
        CancellationToken cancellationToken)
    {
        const int maxSegmentRetries = 3;
        var buffer = new byte[256 * 1024];
        long segmentLength = segment.End - segment.Start + 1;

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (segment.Done >= segmentLength)
                    return;

                long rangeStart = segment.Start + segment.Done;

                using var request = new HttpRequestMessage(HttpMethod.Get, item.DirectDownloadUrl);
                request.Headers.Range = new RangeHeaderValue(rangeStart, segment.End);
                ApplyRequestHeaders(request, item);

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    // Segment already completed according to server
                    segment.Done = segmentLength;
                    return;
                }

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    throw new RangeNotSupportedException("Server returned 200 OK instead of 206 Partial Content: Range requests not supported.");
                }

                response.EnsureSuccessStatusCode();

                if (response.StatusCode != HttpStatusCode.PartialContent)
                {
                    throw new RangeNotSupportedException("Server did not accept the range request (no 206 Partial Content).");
                }

                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                long offset = rangeStart;
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    await _throttler.ThrottleAsync(bytesRead, cancellationToken);
                    if (item.IsTrickling)
                    {
                        await trickleThrottler.ThrottleAsync(bytesRead, cancellationToken);
                    }
                    await RandomAccess.WriteAsync(fileHandle, buffer.AsMemory(0, bytesRead), offset, cancellationToken);
                    offset += bytesRead;
                    segment.Done += bytesRead;
                }

                if (segment.Done < segmentLength)
                {
                    throw new IOException("Segment stream was prematurely closed by the server.");
                }

                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsRangeNotSupported(ex))
            {
                throw;
            }
            catch (Exception) when (attempt < maxSegmentRetries - 1)
            {
                // Brief jitter backoff before segment retry
                await Task.Delay(Random.Shared.Next(400, 1200), cancellationToken);
            }
        }
    }

    private async Task ProcessResponseStreamAsync(
        DownloadItem item, 
        HttpResponseMessage response, 
        string tempFilePath, 
        string finalFilePath, 
        byte[] buffer, 
        CancellationToken cancellationToken,
        long initialDownloadedBytes = 0)
    {
        long? contentLength = response.Content.Headers.ContentLength;
        long totalBytes;

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            if (response.Content.Headers.ContentRange?.Length != null && response.Content.Headers.ContentRange.Length.Value > 0)
            {
                totalBytes = response.Content.Headers.ContentRange.Length.Value;
            }
            else
            {
                totalBytes = (contentLength ?? 0) + initialDownloadedBytes;
            }
        }
        else if (response.Content.Headers.ContentRange?.Length != null && response.Content.Headers.ContentRange.Length.Value > 0)
        {
            totalBytes = response.Content.Headers.ContentRange.Length.Value;
        }
        else
        {
            totalBytes = contentLength ?? (item.TotalBytes > 0 ? item.TotalBytes : 0);
        }

        if (totalBytes <= 0 && item.TotalBytes > 0)
        {
            totalBytes = item.TotalBytes;
        }

        // Try extracting Content-Disposition filename with sanitization & fallback
        string? extractedFileName = ExtractContentDispositionFileName(response);

        if (!item.IsCustomName && !string.IsNullOrWhiteSpace(extractedFileName) && !string.Equals(item.FileName, extractedFileName, StringComparison.OrdinalIgnoreCase))
        {
            var oldTempPath = tempFilePath;
            SafeInvoke(() => item.Rename(extractedFileName, isUserAction: false));
            var newTempPath = item.SaveFilePath + ".part";

            if (!string.Equals(oldTempPath, newTempPath, StringComparison.OrdinalIgnoreCase) && File.Exists(oldTempPath))
            {
                try
                {
                    if (File.Exists(newTempPath)) File.Delete(newTempPath);
                    File.Move(oldTempPath, newTempPath);
                    tempFilePath = newTempPath;
                }
                catch { }
            }

            try
            {
                var pkg = QueueManager.Instance.Packages.FirstOrDefault(p => p.Id == item.PackageId || p.Items.Contains(item));
                if (pkg != null && !pkg.IsCustomName)
                {
                    Extractor.LinkMetadataResolverService.TryUpdatePackageName(pkg);
                }
            }
            catch { }
        }

        SafeInvokeAsync(() =>
        {
            if (totalBytes > 0) item.TotalBytes = totalBytes;
            item.DownloadedBytes = initialDownloadedBytes;
            item.Status = DownloadStatus.Downloading;
            item.StatusMessage = Loc.Get("Status_Downloading");
        });

        using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var fileStreamOptions = new FileStreamOptions
        {
            Mode = initialDownloadedBytes > 0 ? FileMode.Append : FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.ReadWrite,
            BufferSize = 256 * 1024,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        };
        using var fileStream = new FileStream(tempFilePath, fileStreamOptions);

        if (totalBytes > 0 && initialDownloadedBytes == 0)
        {
            try
            {
                if (fileStream.Length != totalBytes)
                    fileStream.SetLength(totalBytes);
            }
            catch { }
        }

        long currentDownloaded = initialDownloadedBytes;
        var speedTracker = new RollingSpeedTracker(initialDownloadedBytes);
        var lastUiUpdateTime = Stopwatch.GetTimestamp();

        // Dedicated throttler for "Paused" trickle mode (500 KB/s per download)
        var trickleThrottler = new BandwidthThrottler { MaxBytesPerSecond = 500 * 1024 };

        try
        {
            int bytesRead;
            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await _throttler.ThrottleAsync(bytesRead, cancellationToken);
                if (item.IsTrickling)
                {
                    await trickleThrottler.ThrottleAsync(bytesRead, cancellationToken);
                }
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                currentDownloaded += bytesRead;

                var now = Stopwatch.GetTimestamp();
                if (((double)(now - lastUiUpdateTime) / Stopwatch.Frequency) >= 0.25) // 4 smooth UI updates per second
                {
                    var currentSpeed = speedTracker.CalculateSpeed(currentDownloaded);
                    SafeInvokeAsync(() =>
                    {
                        item.UpdateProgress(currentDownloaded, totalBytes, currentSpeed);
                    });
                    DownloadProgressUpdated?.Invoke(item);
                    lastUiUpdateTime = now;
                }
            }

            await fileStream.FlushAsync(cancellationToken);
        }
        finally
        {
            try { await fileStream.FlushAsync(); } catch { }
            SafeInvokeAsync(() =>
            {
                item.DownloadedBytes = currentDownloaded;
                if (totalBytes > 0)
                {
                    item.ProgressPercentage = Math.Clamp((double)currentDownloaded / totalBytes * 100.0, 0, 100);
                }
                item.SpeedBytesPerSecond = 0;
                item.RemainingSeconds = 0;
            });
        }

        fileStream.Close();

        // Validate stream completeness: do not mark complete if stream was truncated
        if (totalBytes > 0 && currentDownloaded < totalBytes)
        {
            SafeInvokeAsync(() =>
            {
                item.DownloadedBytes = currentDownloaded;
            });
            throw new IOException(
                $"Download was interrupted: Only {currentDownloaded} of {totalBytes} bytes were transferred.");
        }

        // Download completed, atomically rename temp file to final destination
        var actualFinalPath = !string.IsNullOrWhiteSpace(item.SaveFilePath) ? item.SaveFilePath : finalFilePath;
        var targetDir = Path.GetDirectoryName(actualFinalPath);
        if (!string.IsNullOrWhiteSpace(targetDir) && !Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        File.Move(tempFilePath, actualFinalPath, overwrite: true);

        SafeInvoke(() =>
        {
            item.DownloadedBytes = currentDownloaded;
            if (totalBytes <= 0) item.TotalBytes = currentDownloaded;
            item.ProgressPercentage = 100;
            item.SpeedBytesPerSecond = 0;
            item.RemainingSeconds = 0;
            item.Status = DownloadStatus.Completed;
            item.StatusMessage = Loc.Get("Status_Completed");
            item.CompletedAt = DateTime.Now;
            if (item.StartedAt.HasValue && item.CompletedAt.Value >= item.StartedAt.Value)
            {
                item.ElapsedDurationMs = (long)(item.CompletedAt.Value - item.StartedAt.Value).TotalMilliseconds;
            }
        });

        CompleteAndRemoveActiveDownload(item.Id);
        DownloadCompleted?.Invoke(item);
    }

    /// <summary>
    /// Extracts and sanitizes the filename from the Content-Disposition header
    /// of an HTTP response (including fallback for non-RFC compliant headers).
    /// </summary>
    private static string? ExtractContentDispositionFileName(HttpResponseMessage response)
    {
        string? extractedFileName = null;
        try
        {
            var contentDisposition = response.Content.Headers.ContentDisposition;
            if (contentDisposition?.FileNameStar != null)
            {
                var name = contentDisposition.FileNameStar.Trim('\"').Trim();
                if (!string.IsNullOrWhiteSpace(name)) extractedFileName = Uri.UnescapeDataString(name);
            }
            else if (contentDisposition?.FileName != null)
            {
                var name = contentDisposition.FileName.Trim('\"').Trim();
                if (!string.IsNullOrWhiteSpace(name)) extractedFileName = Uri.UnescapeDataString(name);
            }
        }
        catch (FormatException)
        {
            // Fallback for non-RFC compliant Content-Disposition headers
            if (response.Content.Headers.TryGetValues("Content-Disposition", out var values))
            {
                var raw = string.Join(";", values);
                var match = System.Text.RegularExpressions.Regex.Match(raw, @"filename\*?=(?:UTF-8''|""?)([^"";]+)""?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    extractedFileName = Uri.UnescapeDataString(match.Groups[1].Value.Trim('\"', '\'').Trim());
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(extractedFileName))
        {
            var cleanName = Path.GetFileName(extractedFileName).Trim('\"', ' ', '\t');
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                cleanName = cleanName.Replace(c, '_');
            }
            if (!string.IsNullOrWhiteSpace(cleanName))
            {
                extractedFileName = cleanName;
            }
        }

        return extractedFileName;
    }

    public Task? CancelOrPauseDownload(Guid itemId, bool waitForCompletion = false, int timeoutMs = 2000)
    {
        if (_activeDownloads.TryGetValue(itemId, out var entry))
        {
            try { entry.Cts.Cancel(); } catch { }
            if (waitForCompletion)
            {
                try { entry.Tcs.Task.Wait(TimeSpan.FromMilliseconds(timeoutMs)); } catch { }
            }
            return entry.Tcs.Task;
        }
        return null;
    }

    public void PauseAll(bool waitForCompletion = false, int timeoutMs = 3000)
    {
        var entries = _activeDownloads.Values.ToArray();
        foreach (var entry in entries)
        {
            try { entry.Cts.Cancel(); } catch { }
        }

        if (waitForCompletion && entries.Length > 0)
        {
            try
            {
                var tasks = entries.Select(e => e.Tcs.Task).ToArray();
                Task.WaitAll(tasks, TimeSpan.FromMilliseconds(timeoutMs));
            }
            catch { }
        }
    }

    public async Task PauseAllAsync(int timeoutMs = 3000)
    {
        var entries = _activeDownloads.Values.ToArray();
        foreach (var entry in entries)
        {
            try { entry.Cts.Cancel(); } catch { }
        }

        if (entries.Length > 0)
        {
            try
            {
                var tasks = entries.Select(e => e.Tcs.Task).ToArray();
                await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(timeoutMs));
            }
            catch { }
        }
    }

    private class RollingSpeedTracker
    {
        private readonly Queue<(long Timestamp, long CumulativeBytes)> _samples = new();
        private readonly object _lock = new();

        public RollingSpeedTracker(long initialCumulativeBytes = 0)
        {
            _samples.Enqueue((Stopwatch.GetTimestamp(), initialCumulativeBytes));
        }

        public double CalculateSpeed(long currentCumulativeBytes)
        {
            lock (_lock)
            {
                var now = Stopwatch.GetTimestamp();
                _samples.Enqueue((now, currentCumulativeBytes));
                PruneOldSamples(now);

                if (_samples.Count < 2)
                    return 0;

                var earliest = _samples.Peek();
                var duration = (double)(now - earliest.Timestamp) / Stopwatch.Frequency;

                if (duration <= 0.05)
                    return 0;

                long bytesDiff = currentCumulativeBytes - earliest.CumulativeBytes;
                if (bytesDiff <= 0)
                    return 0;

                return bytesDiff / duration;
            }
        }

        private void PruneOldSamples(long now)
        {
            while (_samples.Count > 1 && (((double)(now - _samples.Peek().Timestamp) / Stopwatch.Frequency) > 1.5))
            {
                _samples.Dequeue();
            }
        }
    }

    private static void SafeInvoke(Action action)
    {
        try
        {
            var app = Application.Current;
            if (app?.Dispatcher != null && !app.Dispatcher.HasShutdownStarted && !app.Dispatcher.HasShutdownFinished)
            {
                if (!app.Dispatcher.CheckAccess())
                {
                    app.Dispatcher.Invoke(action);
                }
                else
                {
                    action();
                }
            }
            else
            {
                action();
            }
        }
        catch (Exception)
        {
            try { action(); } catch { }
        }
    }

    /// <summary>
    /// Like SafeInvoke, but non-blocking (BeginInvoke with Background priority):
    /// Ensures UI rendering and user inputs have priority so downloads never cause UI lag.
    /// </summary>
    private static void SafeInvokeAsync(Action action)
    {
        try
        {
            var app = Application.Current;
            if (app?.Dispatcher != null && !app.Dispatcher.HasShutdownStarted && !app.Dispatcher.HasShutdownFinished)
            {
                if (!app.Dispatcher.CheckAccess())
                {
                    app.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { action(); } catch { }
                    }), System.Windows.Threading.DispatcherPriority.Background);
                    return;
                }
            }

            action();
        }
        catch (Exception)
        {
            try { action(); } catch { }
        }
    }
}

public class RangeNotSupportedException : Exception
{
    public RangeNotSupportedException() : base("Range requests not supported.") { }
    public RangeNotSupportedException(string message) : base(message) { }
    public RangeNotSupportedException(string message, Exception innerException) : base(message, innerException) { }
}
