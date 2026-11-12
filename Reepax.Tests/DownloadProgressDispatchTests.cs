using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Reepax.Models;
using Reepax.Services.Download;
using Xunit;

namespace Reepax.Tests;

public partial class DialogUiTests
{
    [Fact]
    public void DownloadProgress_EndedFailureCallbackCannotUndoALaterPause()
    {
        RunInSta(() =>
        {
            using var transfer = new ProgressTransfer(1);
            transfer.Start();
            PumpProgressUntil(transfer.Controls[0].Started.Task);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.True(transfer.Controls[0].Blocked.Task.Wait(TimeSpan.FromSeconds(10)));
            transfer.Controls[0].Fail = true;
            transfer.Controls[0].Release.TrySetResult();
            Assert.True(Task.WhenAll(transfer.Downloads).Wait(TimeSpan.FromSeconds(10)));
            var item = transfer.Items[0];
            Assert.Equal(DownloadStatus.Failed, item.Status);
            item.Status = DownloadStatus.Paused;
            item.StatusMessage = "Paused";
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(DownloadStatus.Paused, item.Status);
            Assert.Equal("Paused", item.StatusMessage);
            Assert.False(transfer.Engine.IsDownloading(item.Id));
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void DownloadProgress_BlockedDispatcherKeepsOnePendingUpdatePerFile(int connections)
    {
        RunInSta(() =>
        {
            using var transfer = new ProgressTransfer(connections, count: 2);
            var dispatcher = Dispatcher.CurrentDispatcher;
            var pending = new ConcurrentDictionary<DispatcherOperation, byte>();
            void Posted(object? sender, DispatcherHookEventArgs args)
            {
                if (args.Operation.Priority == DispatcherPriority.Background) pending.TryAdd(args.Operation, 0);
            }
            void Started(object? sender, DispatcherHookEventArgs args) => pending.TryRemove(args.Operation, out _);
            try
            {
                transfer.Start();
                PumpProgressUntil(Task.WhenAll(transfer.Controls.Select(control => control.Started.Task)));
                dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                dispatcher.Hooks.OperationPosted += Posted;
                dispatcher.Hooks.OperationStarted += Started;
                Assert.True(Task.WhenAll(transfer.Controls.Select(control => control.Blocked.Task)).Wait(TimeSpan.FromSeconds(10)));
                // Let the segmented watcher sample the final controlled read without pumping the UI.
                if (connections > 1) Task.Delay(400).GetAwaiter().GetResult();
                Assert.InRange(pending.Count, 1, transfer.Items.Length);
                dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                long expected = (connections - 1L) * ProgressTransfer.Data.Length / connections + 3 * ProgressControl.ReadSize;
                Assert.All(transfer.Items, item => Assert.Equal(expected, item.DownloadedBytes));
            }
            finally
            {
                dispatcher.Hooks.OperationPosted -= Posted;
                dispatcher.Hooks.OperationStarted -= Started;
            }
        });
    }

    [Theory]
    [InlineData(1, "complete")]
    [InlineData(4, "complete")]
    [InlineData(1, "pause")]
    [InlineData(4, "pause")]
    [InlineData(1, "queue")]
    [InlineData(4, "queue")]
    [InlineData(1, "fail")]
    [InlineData(4, "fail")]
    public void DownloadProgress_LateUpdatesCannotOverwriteTerminalState(int connections, string transition)
    {
        RunInSta(() =>
        {
            using var transfer = new ProgressTransfer(connections);
            transfer.Start();
            PumpProgressUntil(transfer.Controls[0].Started.Task);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            int notifications = 0;
            transfer.Engine.DownloadProgressUpdated += _ => notifications++;
            Assert.True(transfer.Controls[0].Blocked.Task.Wait(TimeSpan.FromSeconds(10)));
            var item = transfer.Items[0];
            if (transition is "complete" or "fail")
            {
                transfer.Controls[0].Fail = transition == "fail";
                transfer.Controls[0].Release.TrySetResult();
            }
            else
            {
                if (transition == "queue") item.Status = DownloadStatus.Queued;
                _ = transfer.Engine.CancelOrPauseDownload(item.Id, demotingToQueued: transition == "queue");
            }
            Assert.True(Task.WhenAll(transfer.Downloads).Wait(TimeSpan.FromSeconds(10)));
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(0, notifications);
            Assert.Equal(0, item.SpeedBytesPerSecond);
            Assert.Equal(0, item.RemainingSeconds);
            Assert.False(transfer.Engine.IsDownloading(item.Id));
            var expected = transition switch
            {
                "complete" => DownloadStatus.Completed,
                "pause" => DownloadStatus.Paused,
                "queue" => DownloadStatus.Queued,
                _ => DownloadStatus.Failed
            };
            Assert.Equal(expected, item.Status);
            if (transition == "complete")
            {
                Assert.Equal(ProgressTransfer.Data.Length, item.DownloadedBytes);
                Assert.Equal(100, item.ProgressPercentage);
                Assert.Equal(ProgressTransfer.Data, File.ReadAllBytes(item.SaveFilePath!));
            }
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void DownloadProgress_RestartFromCancellationCallbackKeepsNewSessionActive(int connections)
    {
        RunInSta(() =>
        {
            using var transfer = new ProgressTransfer(connections);
            transfer.Start();
            PumpProgressUntil(transfer.Controls[0].Started.Task);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var restarted = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Restart(DownloadItem item) => restarted.TrySetResult(transfer.Engine.ResumeDownloadAsync(item));
            transfer.Engine.DownloadCancelled += Restart;
            try
            {
                Assert.True(transfer.Controls[0].Blocked.Task.Wait(TimeSpan.FromSeconds(10)));
                _ = transfer.Engine.CancelOrPauseDownload(transfer.Items[0].Id);
                Assert.True(Task.WhenAll(transfer.Downloads).Wait(TimeSpan.FromSeconds(10)));
                Assert.True(restarted.Task.IsCompleted);
                Assert.True(transfer.Engine.IsDownloading(transfer.Items[0].Id));
                Assert.NotNull(transfer.Engine.GetDownloadTask(transfer.Items[0].Id));
                Task next = restarted.Task.GetAwaiter().GetResult();
                PumpProgressUntil(next);
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var item = transfer.Items[0];
                Assert.Equal(DownloadStatus.Completed, item.Status);
                Assert.Equal(100, item.ProgressPercentage);
                Assert.Equal(0, item.SpeedBytesPerSecond);
                Assert.Null(item.CurrentSessionStartedAt);
                Assert.Equal(ProgressTransfer.Data, File.ReadAllBytes(item.SaveFilePath!));
            }
            finally { transfer.Engine.DownloadCancelled -= Restart; }
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void DownloadProgress_CompletionDuringPropertyNotificationSuppressesObsoleteProgressEvent(int connections)
    {
        RunInSta(() =>
        {
            using var transfer = new ProgressTransfer(connections);
            transfer.Start();
            PumpProgressUntil(transfer.Controls[0].Started.Task);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.True(transfer.Controls[0].Blocked.Task.Wait(TimeSpan.FromSeconds(10)));
            int notifications = 0;
            bool released = false;
            bool completedDuringPublication = false;
            transfer.Engine.DownloadProgressUpdated += _ => notifications++;
            transfer.Items[0].PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(DownloadItem.DownloadedBytes) || released || !Application.Current.Dispatcher.CheckAccess()) return;
                released = true;
                transfer.Controls[0].Release.TrySetResult();
                completedDuringPublication = Task.WhenAll(transfer.Downloads).Wait(TimeSpan.FromSeconds(10));
            };
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.True(released);
            Assert.True(completedDuringPublication);
            Assert.Equal(0, notifications);
            Assert.Equal(DownloadStatus.Completed, transfer.Items[0].Status);
            Assert.Equal(100, transfer.Items[0].ProgressPercentage);
            Assert.Equal(0, transfer.Items[0].SpeedBytesPerSecond);
        });
    }

    private static void PumpProgressUntil(Task task)
    {
        var frame = new DispatcherFrame();
        var limit = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (task.IsCompleted || DateTime.UtcNow >= limit) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.True(task.IsCompleted, "Download did not reach the controlled response stage.");
        task.GetAwaiter().GetResult();
    }

    private sealed class ProgressTransfer : IDisposable
    {
        internal static readonly byte[] Data = Enumerable.Range(0, 512 * 1024).Select(i => (byte)(i % 251)).ToArray();
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("ProgressDispatch_");
        private readonly HttpClient _client;
        internal DownloadEngine Engine { get; }
        internal DownloadItem[] Items { get; }
        internal ProgressControl[] Controls { get; }
        internal Task[] Downloads { get; private set; } = Array.Empty<Task>();

        internal ProgressTransfer(int connections, int count = 1)
        {
            Controls = Enumerable.Range(0, count).Select(_ => new ProgressControl()).ToArray();
            Items = Enumerable.Range(0, count).Select(index => new DownloadItem
            {
                FileName = $"payload-{index}.bin", SaveFilePath = Path.Combine(_directory.FullName, $"payload-{index}.bin"),
                DirectDownloadUrl = $"https://example.com/{index}", IsCustomName = true,
                TotalBytes = Data.Length, Status = DownloadStatus.Paused,
                ExpectedChecksum = Convert.ToHexString(SHA256.HashData(Data))
            }).ToArray();
            if (connections > 1)
            {
                foreach (var item in Items)
                {
                    File.WriteAllBytes(item.SaveFilePath + ".part", new byte[Data.Length]);
                    File.WriteAllText(item.SaveFilePath + ".part.segments", JsonSerializer.Serialize(new
                    {
                        TotalBytes = Data.Length,
                        Segments = Enumerable.Range(0, connections).Select(i => new
                        {
                            Start = i * (long)Data.Length / connections, End = (i + 1L) * Data.Length / connections - 1, Done = 0L
                        })
                    }));
                }
            }
            _client = new HttpClient(new TestMockHttpMessageHandler((request, _) =>
            {
                int index = int.Parse(request.RequestUri!.AbsolutePath.Trim('/'));
                if (Controls[index].Fail) return Task.FromException<HttpResponseMessage>(new IOException("Response unavailable."));
                var range = request.Headers.Range?.Ranges.Single();
                int start = (int)(range?.From ?? 0), end = (int)(range?.To ?? Data.Length - 1);
                Stream stream = start == 0 ? new ProgressStream(Data.AsSpan(start, end - start + 1).ToArray(), Controls[index])
                    : new MemoryStream(Data, start, end - start + 1, writable: false);
                var response = new HttpResponseMessage(range == null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
                {
                    Content = new StreamContent(stream)
                };
                response.Content.Headers.ContentLength = end - start + 1;
                if (range != null) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, Data.Length);
                return Task.FromResult(response);
            }));
            Engine = new DownloadEngine(_client) { MaxConnectionsPerDownload = connections };
        }

        internal void Start() => Downloads = Items.Select(item => Task.Run(() => Engine.ResumeDownloadAsync(item))).ToArray();

        public void Dispose()
        {
            PumpProgressUntil(Engine.PauseAllAsync());
            PumpProgressUntil(Task.WhenAll(Downloads));
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            _client.Dispose();
            string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(_directory.Parent?.FullName, parent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected temporary directory.");
            _directory.Delete(recursive: true);
        }
    }

    private sealed class ProgressControl
    {
        internal const int ReadSize = 16 * 1024;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal volatile bool Fail;
    }

    private sealed class ProgressStream(byte[] data, ProgressControl control) : MemoryStream(data, writable: false)
    {
        private int _reads;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            control.Started.TrySetResult();
            if (++_reads <= 3)
            {
                await Task.Delay(350, cancellationToken);
                return await base.ReadAsync(buffer[..Math.Min(buffer.Length, ProgressControl.ReadSize)], cancellationToken);
            }
            control.Blocked.TrySetResult();
            await control.Release.Task.WaitAsync(cancellationToken);
            if (control.Fail) throw new IOException("Response interrupted.");
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
