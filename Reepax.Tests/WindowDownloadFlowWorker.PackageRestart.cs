using System.ComponentModel;
using System.IO;
using System.Text.Json;
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
}
