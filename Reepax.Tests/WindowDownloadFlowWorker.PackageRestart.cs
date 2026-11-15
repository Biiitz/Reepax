using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Reepax.Models;
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
                Assert.Equal(new[] { "--restart" }, arguments);
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
        string savePath = Path.Combine(destination, "partial.bin");
        byte[] prefix = [1, 3, 5, 7];
        File.WriteAllBytes(savePath + ".part", prefix);
        var package = new DownloadPackage { Name = "Example package", SaveDirectory = destination };
        package.Items.Add(new DownloadItem
        {
            FileName = "partial.bin", SaveFilePath = savePath,
            OriginalUrl = "https://example.test/partial.bin", TotalBytes = 16, Status = DownloadStatus.Downloading
        });
        model.Packages.Add(package);
        main.Show();

        bool previousTestEnvironment = DownloadPersistenceService.IsTestEnvironment;
        DownloadPersistenceService.IsTestEnvironment = false;
        string instanceName = "ReepaxRestart_" + Guid.NewGuid().ToString("N");
        try
        {
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
                var saved = Assert.Single(Assert.Single(DownloadPersistenceService.Instance.LoadDownloads()).Items);
                Assert.Equal(DownloadStatus.Paused, saved.Status);
                Assert.Equal(prefix.Length, saved.DownloadedBytes);
                Assert.Equal(prefix, File.ReadAllBytes(savePath + ".part"));
                if (++attempts == 1) throw new Win32Exception(5);
                return new Process();
            }, () => App.CompleteInstanceExit(true, () => closing = true, primary.Dispose));

            Assert.False(restart.TryRestart());
            Assert.Equal(1, attempts);
            Assert.False(closing);
            Assert.True(main.IsVisible);
            Assert.Equal(DownloadStatus.Paused, Assert.Single(package.Items).Status);
            Assert.True(await primary.SendArgsToPrimaryAsync(["--activate"]));
            Assert.Equal(new[] { "--activate" }, forwarded);

            Assert.True(restart.TryRestart());
            Assert.Equal(2, attempts);
            Assert.True(closing);
            using var successor = new SingleInstanceService(instanceName, requestTimeoutMs: 1000);
            Assert.True(successor.TryAcquireOwnership(4000));
            Console.WriteLine("Restart launch failure recovered");
        }
        finally
        {
            DownloadPersistenceService.IsTestEnvironment = previousTestEnvironment;
            main.Close();
        }
    }
}
