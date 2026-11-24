using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Localization;
using Reepax.ViewModels;
using Reepax.Views;
using Xunit;

namespace Reepax.Tests;

internal static partial class WindowDownloadFlowWorker
{
    private static async Task CheckItemMenuDownloadAsync(string directory, bool keyboard)
    {
        const int chunk = 256 * 1024;
        byte[] payload = new byte[4 * chunk];
        byte[] neighbor = new byte[payload.Length];
        new Random(17).NextBytes(payload);
        new Random(29).NextBytes(neighbor);
        var releaseNeighbor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int payloadRequests = 0;
        await using var source = new PipelineHttpSource(new Dictionary<string, byte[]>
        {
            ["payload.bin"] = payload,
            ["neighbor.bin"] = neighbor
        }, writeBody: async (name, _, stream, bytes, token) =>
        {
            if (name == "neighbor.bin")
            {
                await stream.WriteAsync(bytes[..chunk], token);
                await stream.FlushAsync(token);
                await releaseNeighbor.Task.WaitAsync(token);
                await stream.WriteAsync(bytes[chunk..], token);
            }
            else if (Interlocked.Increment(ref payloadRequests) <= 2)
            {
                await stream.WriteAsync(bytes[..chunk], token);
                await stream.FlushAsync(token);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            else
            {
                await stream.WriteAsync(bytes, token);
            }
        });

        var main = new MainWindow { ShowActivated = false, ShowInTaskbar = false };
        var queue = QueueManager.Instance;
        var engine = DownloadEngine.Instance;
        queue.MaxConcurrentDownloads = 1;
        engine.MaxConnectionsPerDownload = 1;
        var package = new DownloadPackage { Name = "Downloads", SaveDirectory = directory, IsCustomName = true, IsExpanded = true };
        var item = MakeItem("payload.bin");
        var busy = MakeItem("neighbor.bin");
        package.AddItems([item, busy]);
        queue.Packages.Add(package);
        main.Show();
        var menu = Assert.IsType<ContextMenu>(main.FindResource("ItemContextMenu"));
        string url = item.DirectDownloadUrl!;
        string part = item.SaveFilePath + ".part";
        string checkpoint = part + ".segments";
        try
        {
            var first = engine.ResumeDownloadAsync(item);
            await UntilAsync(() => PartSize(item) > 0);
            queue.PauseItem(item);
            await first.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(DownloadStatus.Paused, item.Status);
            int firstOffset = checked((int)item.DownloadedBytes);
            Assert.InRange(firstOffset, 1, chunk);
            Assert.Equal(payload[..firstOffset], File.ReadAllBytes(part));
            byte[] resumeState = File.ReadAllBytes(checkpoint);

            var unrelated = engine.ResumeDownloadAsync(busy);
            await UntilAsync(() => PartSize(busy) > 0);
            Assert.Equal(1, engine.ActiveDownloadsCount);
            item.RetryCount = 2;
            item.FastHostResolveFailed = true;

            await ActivateMenuAsync(item, "Menu_Resume");
            Assert.Equal(DownloadStatus.Queued, item.Status);
            Assert.Equal(url, item.DirectDownloadUrl);
            Assert.Equal(2, item.RetryCount);
            Assert.True(item.FastHostResolveFailed);
            Assert.Equal(payload[..firstOffset], File.ReadAllBytes(part));
            Assert.Equal(resumeState, File.ReadAllBytes(checkpoint));
            Assert.True(engine.IsDownloading(busy.Id));
            Assert.Equal(1, source.Requests["payload.bin"]);

            releaseNeighbor.TrySetResult();
            await unrelated.WaitAsync(TimeSpan.FromSeconds(10));
            await UntilAsync(() => PartSize(item) > firstOffset);
            Assert.Equal(DownloadStatus.Downloading, item.Status);
            Assert.Equal(neighbor, File.ReadAllBytes(busy.SaveFilePath!));

            await ActivateMenuAsync(item, "Menu_Pause");
            await UntilAsync(() => !engine.IsDownloading(item.Id));
            Assert.Equal(DownloadStatus.Paused, item.Status);
            Assert.Equal(url, item.DirectDownloadUrl);
            Assert.Equal(2, item.RetryCount);
            int secondOffset = checked((int)item.DownloadedBytes);
            Assert.InRange(secondOffset, firstOffset + 1, firstOffset + chunk);
            Assert.Equal(payload[..secondOffset], File.ReadAllBytes(part));

            await ActivateMenuAsync(item, "Menu_Resume");
            await UntilAsync(() => item.Status == DownloadStatus.Completed && !engine.IsDownloading(item.Id));
            Assert.Equal(payload, File.ReadAllBytes(item.SaveFilePath!));
            Assert.Equal(neighbor, File.ReadAllBytes(busy.SaveFilePath!));
            Assert.False(File.Exists(part));
            Assert.False(File.Exists(checkpoint));
            Assert.Equal(new long?[] { null, firstOffset, secondOffset }, source.Ranges
                .Where(request => request.File == item.FileName).Select(request => request.Start));

            // Keep retries queued so that only the menu action can mutate their state.
            queue.ActiveDownloadsCountOverride = () => queue.MaxConcurrentDownloads;
            string md5 = new('a', 32);
            byte[] partial = [11, 17, 29];
            var failed = MakeItem("failed.bin");
            failed.Status = DownloadStatus.Failed;
            failed.RetryCount = 2;
            failed.ExpectedChecksum = md5;
            failed.ExpectedChecksumHasKnownSource = true;
            package.AddItems([failed]);
            File.WriteAllBytes(failed.SaveFilePath + ".part", partial);

            await ActivateMenuAsync(failed, "Menu_Retry");
            Assert.Equal(DownloadStatus.Queued, failed.Status);
            Assert.Null(failed.DirectDownloadUrl);
            Assert.Equal(0, failed.RetryCount);
            Assert.Equal(md5, failed.ExpectedChecksum);
            Assert.Equal(partial, File.ReadAllBytes(failed.SaveFilePath + ".part"));

            var legacy = MakeItem("legacy.bin");
            legacy.Status = DownloadStatus.Failed;
            legacy.ExpectedChecksum = md5;
            legacy.ExpectedChecksumHasKnownSource = false;
            package.AddItems([legacy]);
            string rejected = legacy.SaveFilePath + ".part";
            File.WriteAllBytes(rejected, partial);
            legacy.RejectedFiles = RejectedDownloadFiles.Capture(legacy.SaveFilePath!, published: false);
            legacy.SetDownloadFailure(new DownloadFailure { Kind = DownloadFailureKind.ChecksumMismatch });
            bool confirm = false;
            int confirmations = 0;
            ConfirmDialog.ShowDialogOverrideForTesting = () => { confirmations++; return confirm; };
            await ActivateMenuAsync(legacy, "Menu_RetryWithoutLegacyMd5");
            Assert.Equal(DownloadStatus.Failed, legacy.Status);
            Assert.Equal(md5, legacy.ExpectedChecksum);
            Assert.Equal(partial, File.ReadAllBytes(rejected));

            confirm = true;
            await ActivateMenuAsync(legacy, "Menu_RetryWithoutLegacyMd5");
            Assert.Equal(2, confirmations);
            Assert.Equal(DownloadStatus.Queued, legacy.Status);
            Assert.Null(legacy.ExpectedChecksum);
            Assert.False(File.Exists(rejected));
            Assert.Equal(partial, File.ReadAllBytes(failed.SaveFilePath + ".part"));
            Assert.Equal(payload, File.ReadAllBytes(item.SaveFilePath!));
            Assert.Equal(neighbor, File.ReadAllBytes(busy.SaveFilePath!));
        }
        finally
        {
            ConfirmDialog.ShowDialogOverrideForTesting = null;
            queue.ActiveDownloadsCountOverride = null;
            menu.IsOpen = false;
            queue.StopQueue();
            await engine.PauseAllAsync();
            main.Close();
        }

        DownloadItem MakeItem(string name) => new()
        {
            PackageId = package.Id, FileName = name, SaveFilePath = Path.Combine(directory, name),
            OriginalUrl = source.Url(name), DirectDownloadUrl = source.Url(name), Status = DownloadStatus.Paused,
            IsCustomName = true, TotalBytes = payload.Length
        };

        static long PartSize(DownloadItem download)
        {
            var file = new FileInfo(download.SaveFilePath + ".part");
            return file.Exists ? file.Length : 0;
        }

        async Task ActivateMenuAsync(DownloadItem target, string label)
        {
            package.IsExpanded = true;
            await IdleAsync();
            var row = Descendants<Border>(main).Single(border => ReferenceEquals(border.DataContext, target) &&
                ReferenceEquals(border.ContextMenu, menu));
            menu.PlacementTarget = row;
            if (keyboard)
            {
                menu.IsOpen = true;
                await IdleAsync();
            }
            else
            {
                menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            }

            var action = label == "Menu_RetryWithoutLegacyMd5"
                ? Assert.Single(menu.Items.OfType<MenuItem>(), entry => Equals(entry.Tag, "RetryWithoutLegacyMd5"))
                : Assert.IsType<MenuItem>(menu.Items[0]);
            Assert.Equal(Loc.Get(label), action.Header);
            Assert.Equal(Visibility.Visible, action.Visibility);
            string icon = label switch
            {
                "Menu_Pause" => "IconPause",
                "Menu_Retry" => "IconRefresh",
                "Menu_RetryWithoutLegacyMd5" => "IconShield",
                _ => "IconPlay"
            };
            Assert.Same(main.FindResource(icon), Assert.IsType<System.Windows.Shapes.Path>(action.Icon).Data);
            if (keyboard)
            {
                action.Focus();
                var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(action), 0, Key.Enter)
                    { RoutedEvent = Keyboard.KeyDownEvent };
                action.RaiseEvent(key);
                Assert.True(key.Handled);
            }
            else
            {
                // Lay out the detached menu item and use WPF's mouse-up path without moving the desktop cursor.
                Assert.Null(PresentationSource.FromVisual(action));
                action.Measure(new Size(300, 50));
                action.Arrange(new Rect(0, 0, 300, 50));
                action.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            }
            await IdleAsync();
            menu.IsOpen = false;
        }
    }
}
