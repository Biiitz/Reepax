using System.IO;
using System.Net.Http;
using Reepax.Models;
using Reepax.Services.Localization;
using Reepax.Services.Storage;

namespace Reepax.Services.Download;

public partial class DownloadEngine
{
    internal async Task StartBrowserResponseAsync(DownloadItem item, HttpResponseMessage response)
    {
        if (item.IsUnavailableForProcessing) return;
        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pause = new BrowserPauseState();
        if (!_activeDownloads.TryAdd(item.Id, new ActiveDownload(cancellation, finished) { BrowserPause = pause }))
        {
            cancellation.Dispose();
            return;
        }
        UiState(item).Begin(token);
        // Preserve resumable GET context without replaying the initial browser request.
        item.DirectDownloadUrl = response.RequestMessage is { Method: var method, RequestUri: { Scheme: "http" or "https" } address } &&
            method == HttpMethod.Get ? address.AbsoluteUri : null;
        item.StartDownloadSession();
        PublishDownloadState(item, token, () =>
        {
            item.ClearDownloadFailure();
            item.Status = DownloadStatus.Downloading;
            item.StatusMessage = Loc.Get("Status_Downloading");
        });
        await Task.Run(async () =>
        {
            try
            {
                response.EnsureSuccessStatusCode();
                await pause.WaitAsync(token);
                string destination;
                string key = GetSourceKey(item.DirectDownloadUrl ?? item.OriginalUrl);
                string legacyKey = GetSourceKey(item.OriginalUrl);
                using (await PackagePathOperationGate.Instance.EnterAsync(token))
                {
                    token.ThrowIfCancellationRequested();
                    if (item.IsUnavailableForProcessing) throw new OperationCanceledException(token);
                    if (item.RejectedFiles != null && !item.RejectedFiles.TryReset(item)) throw new DownloadRetryBlockedException();
                    string originalPath = item.SaveFilePath ?? throw new IOException("The download destination is missing.");
                    if (Path.Exists(originalPath)) throw new DownloadDestinationExistsException();
                    if (Path.Exists(originalPath + ".part") || Path.Exists(originalPath + ".part.segments"))
                    {
                        var saved = LoadSegmentState(originalPath + ".part.segments");
                        if (saved is not { IsSequential: true } || (saved.SourceKey != key && saved.SourceKey != legacyKey))
                            throw new DownloadDestinationExistsException();
                    }
                    string? name = ExtractContentDispositionFileName(response);
                    bool renamed = true;
                    SafeInvoke(() =>
                    {
                        renamed = item.IsCustomName || string.IsNullOrWhiteSpace(name) || item.Rename(name, isUserAction: false);
                        var package = QueueManager.Instance.Packages.FirstOrDefault(package => package.Id == item.PackageId || package.Items.Contains(item));
                        if (renamed && package is { IsCustomName: false })
                            Extractor.LinkMetadataResolverService.TryUpdatePackageNameUnderLease(package);
                    });
                    if (!renamed) throw new DownloadDestinationExistsException();
                    // The destination identity is fixed before the streaming writer starts.
                    response.Content.Headers.ContentDisposition = null;
                    destination = item.SaveFilePath ?? throw new IOException("The download destination is missing.");
                    if (Path.Exists(destination)) throw new DownloadDestinationExistsException();
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                }
                string part = destination + ".part";
                var checkpoint = LoadSegmentState(part + ".segments");
                long existing = 0;
                bool fresh = !Path.Exists(part);
                if (!fresh || Path.Exists(part + ".segments"))
                {
                    if (checkpoint is not { IsSequential: true } || (checkpoint.SourceKey != key && checkpoint.SourceKey != legacyKey) || !File.Exists(part))
                        throw new DownloadDestinationExistsException();
                    existing = checkpoint.Segments[0].Done;
                    await VerifyBrowserPrefixAsync(response, part, existing, token);
                    checkpoint.SourceKey = key;
                    checkpoint.EntityTag = GetStrongEntityTag(response);
                }
                // Ignore metadata size hints when the original response has no length header.
                item.TotalBytes = response.Content.Headers.ContentLength ?? 0;
                await ProcessResponseStreamAsync(item, response, part, destination, token,
                    existing, checkpoint: checkpoint, sourceKey: key, requireNewPartial: fresh, browserPause: pause);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested || item.IsUnavailableForProcessing)
            {
                UiState(item).Stop(token);
                bool demoted = _demotedToQueuedItems.ContainsKey(item.Id) || item.Status == DownloadStatus.Queued;
                PublishDownloadState(item, token, () =>
                {
                    if (!demoted)
                    {
                        item.Status = DownloadStatus.Paused;
                        item.StatusMessage = Loc.Get("Status_Paused");
                    }
                    item.SpeedBytesPerSecond = 0;
                    item.RemainingSeconds = 0;
                }, allowEnded: true);
                CompleteAndRemoveActiveDownload(item, token);
                DownloadCancelled?.Invoke(item);
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                UiState(item).Stop(token);
                PublishDownloadState(item, token, () =>
                {
                    if (item.Status != DownloadStatus.Queued)
                    {
                        item.Status = DownloadStatus.Paused;
                        item.StatusMessage = Loc.Get("Status_Paused");
                    }
                    item.SpeedBytesPerSecond = 0;
                    item.RemainingSeconds = 0;
                }, allowEnded: true);
                CompleteAndRemoveActiveDownload(item, token);
                DownloadCancelled?.Invoke(item);
            }
            catch (Exception error)
            {
                UiState(item).Stop(token);
                item.Status = DownloadStatus.Failed;
                item.SetDownloadFailure(DownloadFailure.FromException(error));
                PublishDownloadState(item, token, () =>
                {
                    item.Status = DownloadStatus.Failed;
                    item.SetDownloadFailure(DownloadFailure.FromException(error));
                    item.SpeedBytesPerSecond = 0;
                    item.RemainingSeconds = 0;
                }, allowEnded: true);
                CompleteAndRemoveActiveDownload(item, token);
                DownloadFailed?.Invoke(item, error);
            }
            finally
            {
                item.RecordSessionDuration();
                CompleteAndRemoveActiveDownload(item, token);
            }
        });
    }

    private async Task VerifyBrowserPrefixAsync(HttpResponseMessage response, string part, long count, CancellationToken token)
    {
        using var original = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (count < 0 || original.Length < count || response.Content.Headers.ContentLength is { } total && total < count)
            throw new IOException("The saved partial download does not match the browser response.");
        var incoming = await response.Content.ReadAsStreamAsync(token);
        byte[] saved = new byte[64 * 1024], current = new byte[saved.Length];
        while (count > 0)
        {
            int length = (int)Math.Min(count, saved.Length);
            await original.ReadExactlyAsync(saved.AsMemory(0, length), token);
            await _throttler.AcquireReadQuotaAsync(length, token);
            await incoming.ReadExactlyAsync(current.AsMemory(0, length), token).AsTask().WaitAsync(NetworkReadTimeout, token);
            if (!saved.AsSpan(0, length).SequenceEqual(current.AsSpan(0, length)))
                throw new IOException("The saved partial download belongs to a different response.");
            count -= length;
        }
    }
}
