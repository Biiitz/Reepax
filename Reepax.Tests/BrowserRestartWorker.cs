using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Browser;
using Reepax.Services.Storage;

namespace Reepax.Tests;

internal static class BrowserRestartWorker
{
    internal sealed class NoBrowserHost : IBrowserWindowHost
    {
        internal bool Requested { get; private set; }
        public int ActiveWindowCount => 0;
        public event Action? WindowCapacityChanged { add { } remove { } }
        public bool TryOpenWindow(DownloadItem item, bool hidden, out int windowId)
        {
            Requested = true;
            windowId = 0;
            return false;
        }
        public void CloseWindow(int windowId) { }
        public void CloseAllWindows() { }
    }

    internal static Task SaveAsync(string root, string url, Action<RecoveryProcessReport> report)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    await SaveOnUiAsync(root, url, report);
                    finished.TrySetResult();
                }
                catch (Exception error) { finished.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return finished.Task;
    }

    private static async Task SaveOnUiAsync(string root, string url, Action<RecoveryProcessReport> report)
    {
        await using var fixture = await BrowserHandoffFixture.CreateAsync();
        fixture.Package.SaveDirectory = Path.Combine(root, "downloads");
        fixture.Item.FileName = "download.bin";
        fixture.Item.SaveFilePath = Path.Combine(fixture.Package.SaveDirectory, fixture.Item.FileName);
        fixture.Item.OriginalUrl = new Uri(new Uri(url), "/start").AbsoluteUri;
        string expectedFile = Path.Combine(root, "download.bin");
        if (File.Exists(expectedFile))
            fixture.Item.ExpectedChecksum = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(expectedFile)));
        await fixture.NavigateThroughAsync("/start", "location.href=" + JsonSerializer.Serialize(fixture.Item.OriginalUrl));
        await fixture.WebView.ExecuteScriptAsync("location.href=" + JsonSerializer.Serialize(url));
        string part = fixture.Item.SaveFilePath + ".part";
        await BrowserHandoffFixture.UntilAsync(() => File.Exists(part) && new FileInfo(part).Length > 0);
        fixture.Queue.PauseItem(fixture.Item);
        report(new(Environment.ProcessId, "pausing", false, false, false, fixture.Item.DownloadedBytes, null));
        await DownloadEngine.Instance.PauseBrowserResponseAsync(fixture.Item)!.WaitAsync(TimeSpan.FromSeconds(10));
        if (fixture.Item.Status != DownloadStatus.Paused || fixture.ClosedWindows != 0 ||
            fixture.Item.DirectDownloadUrl != url || !DownloadEngine.HasMatchingSourceCheckpoint(fixture.Item.SaveFilePath, url))
            throw new InvalidDataException("The original browser transfer did not preserve its resumable request.");
        using var storage = new DownloadPersistenceService(Path.Combine(root, "downloads.json"));
        storage.SaveDownloads([fixture.Package], sync: true);
        report(new(Environment.ProcessId, "saved", false, false, false, fixture.Item.DownloadedBytes, null));
    }
}
