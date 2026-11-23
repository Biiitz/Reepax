using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using Reepax.Models;
using Reepax.Services.Browser;
using Reepax.Services.Download;
using Reepax.Services.Extractor;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

internal static partial class WindowDownloadFlowWorker
{
    private static async Task CheckPackageRestartAsync(string root, string stage)
    {
        if (stage == "launch-failure")
        {
            await CheckRestartLaunchFailureAsync();
            return;
        }

        string downloads = Path.Combine(root, "downloads.json");
        string movedRoot = Path.Combine(root, "moved");
        using var storage = new DownloadPersistenceService(downloads);
        var main = new MainWindow { ShowActivated = false, ShowInTaskbar = false };
        var model = (MainViewModel)main.DataContext;
        try
        {
            if (stage.StartsWith("verify-", StringComparison.Ordinal))
            {
                string[] arguments = Environment.GetCommandLineArgs()[5..];
                foreach (var package in storage.LoadDownloads())
                    model.Packages.Add(package);
                var replay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnStatusChanged(object? sender, PropertyChangedEventArgs args)
                {
                    if (args.PropertyName == nameof(MainViewModel.StatusSummary) &&
                        (model.StatusSummary == Loc.Format("Status_PackageImported", "Example package") ||
                         model.StatusSummary.StartsWith(Loc.Format("Dialog_ImportErrorMessage", ""), StringComparison.Ordinal)))
                        replay.TrySetResult();
                }
                model.PropertyChanged += OnStatusChanged;
                try
                {
                    main.Show();
                    if (arguments.Any(PackageExportImportService.IsSupportedPackageFile))
                        await replay.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    await IdleAsync();
                }
                finally { model.PropertyChanged -= OnStatusChanged; }
                if (stage == "verify-remove")
                    Assert.Empty(model.Packages);
                else
                {
                    var package = Assert.Single(model.Packages);
                    string expectedRoot = stage == "verify-move" ? movedRoot : SettingsService.Instance.Settings.DefaultDownloadDirectory;
                    Assert.Equal("Example package", package.Name);
                    Assert.Equal(Path.Combine(expectedRoot, "Example package", "example.bin"), Assert.Single(package.Items).SaveFilePath);
                }
                Assert.False(replay.Task.IsCompleted, "Restart replayed a package import.");
                Assert.Equal(2, arguments.Length);
                Assert.Equal("--restart", arguments[0]);
                Assert.StartsWith("--restart-parent=", arguments[1]);
                AppRestartService.WaitForRestartParent(arguments);
                Console.WriteLine("Restarted package state verified");
                return;
            }

            main.Show();
            await UntilAsync(() => model.StatusSummary == Loc.Format("Status_PackageImported", "Example package"));
            var imported = Assert.Single(model.Packages);
            if (stage == "remove")
            {
                await model.RemovePackage(imported, deleteFilesFromDisk: false);
                Assert.Empty(model.Packages);
            }
            else if (stage == "move")
                Assert.True(await model.ChangeDownloadPathsAsync([imported], movedRoot));
            else
                Assert.Equal("keep", stage);

            storage.SaveDownloads(model.Packages, sync: true);
            string[] commandLine = Environment.GetCommandLineArgs();
            string[] originalArguments = [commandLine[0], ..commandLine.Skip(5)];
            var restart = AppRestartService.CreateStartInfo("Reepax.exe", originalArguments);
            File.WriteAllText(Path.Combine(root, "restart.json"), JsonSerializer.Serialize(restart.ArgumentList));
            Console.WriteLine("Package state saved");
        }
        finally { main.Close(); }
    }

    private static async Task CheckRestartLaunchFailureAsync()
    {
        var main = new MainWindow { ShowActivated = false, ShowInTaskbar = false };
        var model = (MainViewModel)main.DataContext;
        string destination = SettingsService.Instance.Settings.DefaultDownloadDirectory;
        const int prefixLength = 256 * 1024;
        byte[] payload = Enumerable.Range(0, prefixLength * 2).Select(index => (byte)(index % 251)).ToArray();
        await using var source = new PipelineHttpSource(new Dictionary<string, byte[]>
        {
            ["direct.bin"] = payload, ["browser.bin"] = payload
        }, writeBody: async (_, _, stream, body, token) =>
        {
            await stream.WriteAsync(body[..prefixLength], token);
            await stream.FlushAsync(token);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        var package = new DownloadPackage { Name = "Transfers", SaveDirectory = destination, IsCustomName = true };
        var direct = CreateTransfer("direct.bin");
        direct.DirectDownloadUrl = direct.OriginalUrl;
        var browser = CreateTransfer("browser.bin");
        package.Items.Add(direct);
        package.Items.Add(browser);
        model.Packages.Add(package);

        string archiveDirectory = Directory.CreateDirectory(Path.Combine(destination, "archive")).FullName;
        string zipPath = Path.Combine(archiveDirectory, "sample.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("extracted.txt").Open());
            writer.Write("sample content");
        }
        var archive = new DownloadPackage { Name = "Archive", SaveDirectory = archiveDirectory, IsCustomName = true, AutoPar2Repair = false };
        archive.Items.Add(new DownloadItem { FileName = "sample.zip", SaveFilePath = zipPath, Status = DownloadStatus.Completed });
        model.Packages.Add(archive);
        main.Show();

        bool previousTestEnvironment = DownloadPersistenceService.IsTestEnvironment;
        DownloadPersistenceService.IsTestEnvironment = false;
        string instanceName = "ReepaxRestart_" + Guid.NewGuid().ToString("N");
        var queue = QueueManager.Instance;
        var previousHost = queue.BrowserHost;
        var host = new RestartBrowserHost();
        queue.BrowserHost = host;
        var engine = DownloadEngine.Instance;
        engine.MaxConnectionsPerDownload = 1;
        engine.SetSpeedLimit(0);
        var extractor = ArchiveExtractionService.Instance;
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        Task directTransfer = engine.ResumeDownloadAsync(direct);
        Task browserTransfer = Task.CompletedTask;
        Task<bool>? extraction = null;
        HttpResponseMessage? browserResponse = null;
        try
        {
            browserResponse = await client.GetAsync(browser.OriginalUrl, HttpCompletionOption.ResponseHeadersRead);
            browserTransfer = engine.StartBrowserResponseAsync(browser, browserResponse);
            await UntilAsync(() => PartSize(direct) > 0 && PartSize(browser) > 0);
            extraction = extractor.CheckAndExtractPackageAsync(archive, force: true);
            extractor.PausePackageExtraction(archive.Id);
            await UntilAsync(() => archive.IsExtractionPaused);

            using var primary = new SingleInstanceService(instanceName, requestTimeoutMs: 1000);
            Assert.True(primary.TryAcquireOwnership());
            string[]? forwarded = null;
            primary.StartIpcServer(args => forwarded = args);
            int attempts = 0;
            bool closing = false;
            var restart = new AppRestartService(_ =>
            {
                using var contender = new SingleInstanceService(instanceName, requestTimeoutMs: 1000);
                Assert.False(contender.TryAcquireOwnership());
                Assert.Equal(DownloadStatus.Downloading, direct.Status);
                Assert.True(engine.HasBrowserResponse(browser.Id));
                if (++attempts == 1) throw new Win32Exception(5);
                if (attempts == 2) return null;
                return new Process();
            }, () => App.CompleteInstanceExit(true, () => closing = true, primary.Dispose));

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                Assert.False(await restart.TryRestartAsync());
                Assert.Equal(attempt, attempts);
                Assert.False(closing);
                Assert.True(main.IsVisible);
                Assert.Equal(DownloadStatus.Downloading, direct.Status);
                Assert.Equal(DownloadStatus.Downloading, browser.Status);
                Assert.False(directTransfer.IsCompleted);
                Assert.False(browserTransfer.IsCompleted);
                Assert.True(engine.HasBrowserResponse(browser.Id));
                Assert.Equal(1, host.ActiveWindowCount);
                Assert.True(extractor.IsPackageExtracting(archive.Id));
                Assert.False(extraction.IsCompleted);
            }
            Assert.True(await primary.SendArgsToPrimaryAsync(["--activate"]));
            Assert.Equal(new[] { "--activate" }, forwarded);
            extractor.ResumePackageExtraction(archive.Id);
            Assert.True(await extraction.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("sample content", File.ReadAllText(Path.Combine(archiveDirectory, "extracted.txt")));

            Assert.True(await restart.TryRestartAsync());
            Assert.Equal(3, attempts);
            Assert.True(closing);
            Assert.Equal(0, host.ActiveWindowCount);
            await Task.WhenAll(directTransfer, browserTransfer).WaitAsync(TimeSpan.FromSeconds(10));
            using var successor = new SingleInstanceService(instanceName, requestTimeoutMs: 1000);
            Assert.True(successor.TryAcquireOwnership(4000));
            using var another = new SingleInstanceService(instanceName, requestTimeoutMs: 1000);
            Assert.False(another.TryAcquireOwnership());
            var saved = DownloadPersistenceService.Instance.LoadDownloads().Single(value => value.Id == package.Id);
            Assert.Equal(2, saved.Items.Count);
            foreach (var item in saved.Items)
            {
                Assert.Equal(DownloadStatus.Paused, item.Status);
                byte[] partial = File.ReadAllBytes(item.SaveFilePath + ".part");
                Assert.InRange(partial.Length, 1, prefixLength);
                Assert.Equal(partial.Length, item.DownloadedBytes);
                Assert.Equal(payload[..partial.Length], partial);
            }
            Console.WriteLine("Restart launch failure recovered");
        }
        finally
        {
            try
            {
                extractor.CancelPackageExtraction(archive.Id);
                extractor.ResumePackageExtraction(archive.Id);
                await engine.PauseAllAsync();
                await Task.WhenAll(directTransfer, browserTransfer, extraction ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                browserResponse?.Dispose();
                queue.BrowserHost = previousHost;
                DownloadPersistenceService.IsTestEnvironment = previousTestEnvironment;
                main.Close();
            }
        }

        DownloadItem CreateTransfer(string fileName) => new()
        {
            FileName = fileName, SaveFilePath = Path.Combine(destination, fileName),
            OriginalUrl = source.Url(fileName), TotalBytes = payload.Length, IsCustomName = true, PackageId = package.Id
        };

        static long PartSize(DownloadItem item)
        {
            var file = new FileInfo(item.SaveFilePath + ".part");
            return file.Exists ? file.Length : 0;
        }
    }

    private sealed class RestartBrowserHost : IBrowserWindowHost
    {
        public int ActiveWindowCount { get; private set; } = 1;
        public event Action? WindowCapacityChanged { add { } remove { } }
        public bool TryOpenWindow(DownloadItem item, bool hidden, out int windowId) => throw new NotSupportedException();
        public void CloseWindow(int windowId) => ActiveWindowCount = 0;
        public void CloseAllWindows() => ActiveWindowCount = 0;
    }
}
