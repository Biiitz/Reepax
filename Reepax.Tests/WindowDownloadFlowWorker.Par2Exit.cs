using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Localization;
using Reepax.Services.SystemIntegration;
using Reepax.Services.Verification;
using Reepax.ViewModels;
using Reepax.Views;
using Xunit;

namespace Reepax.Tests;

internal static partial class WindowDownloadFlowWorker
{
    private static async Task CheckPar2ExitAsync(string directory, bool fromTray)
    {
        byte[] independent = [1, 3, 5, 7];
        var first = await CreateDamagedPar2PackageAsync(directory, "First package", independent);
        var second = await CreateDamagedPar2PackageAsync(directory, "Second package", independent);
        var samples = new[] { first, second };
        var queue = QueueManager.Instance;
        var repairs = Par2RepairService.Instance;
        var main = new MainWindow { ShowActivated = false, ShowInTaskbar = false };
        var model = (MainViewModel)main.DataContext;
        var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        main.Closed += (_, _) => closed.TrySetResult(!repairs.HasActiveRepairs &&
            samples.All(sample => !File.Exists(sample.Path + ".2")));
        int confirmations = 0;
        bool confirmExit = false;
        ConfirmDialog.ShowDialogOverrideForTesting = () =>
        {
            confirmations++;
            return confirmExit;
        };

        try
        {
            foreach (var sample in samples) queue.Packages.Add(sample.Package);
            main.Show();
            await IdleAsync();
            Assert.False(queue.HasActiveDownloads());
            model.MinimizeToTrayOnClose = fromTray;

            await using var firstCheckpoint = new Par2ExtractionCancellationTests.NativeCheckpoint(repairStage: true);
            Task firstRepair = model.Par2RepairPackageCommand.ExecuteAsync(first.Package);
            var firstProcess = await firstCheckpoint.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(firstProcess.HasExited);

            MenuItem? exitItem = null;
            if (fromTray)
            {
                main.Close();
                Assert.False(main.IsVisible);
                Assert.False(closed.Task.IsCompleted);
                Assert.Equal(0, confirmations);
                var tray = TrayIconService.Instance;
                var menu = Assert.IsType<ContextMenu>(typeof(TrayIconService)
                    .GetField("_contextMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tray));
                exitItem = Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, Loc.Get("Tray_Menu_Exit")));
            }

            void RequestExit()
            {
                if (exitItem == null) main.Close();
                else exitItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }

            RequestExit();
            Assert.Equal(1, confirmations);
            Assert.False(closed.Task.IsCompleted);
            Assert.False(firstProcess.HasExited);

            await using var secondCheckpoint = new Par2ExtractionCancellationTests.NativeCheckpoint(repairStage: true);
            Task secondRepair = model.Par2RepairPackageAsync(second.Package);
            var secondProcess = await secondCheckpoint.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(secondProcess.HasExited);

            confirmExit = true;
            RequestExit();
            RequestExit();
            Assert.Equal(2, confirmations);
            Assert.False(closed.Task.IsCompleted);
            Assert.False(await repairs.RunTrackedPackageOperationAsync(new DownloadPackage(), () => Task.FromResult(true)));

            firstCheckpoint.Resume();
            await firstRepair.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(firstProcess.HasExited);
            Assert.Equal(first.Original, File.ReadAllBytes(first.Path));
            await IdleAsync();
            Assert.False(closed.Task.IsCompleted);
            Assert.False(secondProcess.HasExited);

            secondCheckpoint.Resume();
            Assert.True(await closed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            await secondRepair.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(secondProcess.HasExited);
            foreach (var sample in samples)
            {
                Assert.Equal(sample.Original, File.ReadAllBytes(sample.Path));
                Assert.Equal(independent, File.ReadAllBytes(sample.Path + ".1"));
                Assert.Equal(independent, File.ReadAllBytes(Path.Combine(sample.Package.SaveDirectory, "neighbor.bin")));
            }
        }
        finally
        {
            ConfirmDialog.ShowDialogOverrideForTesting = null;
            if (Application.Current.Windows.OfType<MainWindow>().Contains(main)) main.ForceExit();
        }
    }

    private static async Task CheckPar2RestartAsync(string directory)
    {
        byte[] independent = [1, 3, 5, 7];
        var sample = await CreateDamagedPar2PackageAsync(directory, "Repair sample", independent);
        var main = new MainWindow { ShowActivated = false, ShowInTaskbar = false };
        main.Show();
        var model = (MainViewModel)main.DataContext;
        QueueManager.Instance.Packages.Add(sample.Package);
        await using var checkpoint = new Par2ExtractionCancellationTests.NativeCheckpoint(repairStage: true);
        Task repair = model.Par2RepairPackageCommand.ExecuteAsync(sample.Package);
        var process = await checkpoint.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        int launches = 0;
        int exits = 0;
        var restart = new AppRestartService(_ =>
        {
            launches++;
            Assert.True(process.HasExited);
            Assert.Equal(sample.Original, File.ReadAllBytes(sample.Path));
            Assert.False(File.Exists(sample.Path + ".2"));
            return new Process();
        }, () => exits++);

        model.RestartService = restart;
        Task result = model.RestartApplicationCommand.ExecuteAsync(null);
        Assert.True(Par2RepairService.Instance.IsExitPending);
        Assert.Equal(0, launches);
        Assert.False(result.IsCompleted);
        checkpoint.Resume();
        await result.WaitAsync(TimeSpan.FromSeconds(10));
        await repair.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, launches);
        Assert.Equal(1, exits);
        Assert.Equal(independent, File.ReadAllBytes(sample.Path + ".1"));
        Assert.Equal(independent, File.ReadAllBytes(Path.Combine(sample.Package.SaveDirectory, "neighbor.bin")));
        main.ForceExit();
    }

    internal static async Task<(DownloadPackage Package, string Path, byte[] Original)> CreateDamagedPar2PackageAsync(
        string root, string name, byte[] independent)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        string path = Path.Combine(directory, "payload.bin");
        byte[] original = new byte[32 * 1024];
        new Random(7).NextBytes(original);
        File.WriteAllBytes(path, original);
        string parity = await Par2RepairServiceTests.CreateParityAsync(directory, path);
        byte[] damaged = original.ToArray();
        damaged[17] ^= 0x5a;
        File.WriteAllBytes(path, damaged);
        File.WriteAllBytes(path + ".1", independent);
        File.WriteAllBytes(Path.Combine(directory, "neighbor.bin"), independent);

        var package = new DownloadPackage { Name = name, SaveDirectory = directory, AutoExtractArchives = false };
        package.AddItems(new[] { path, parity }.Select(file => new DownloadItem
        {
            FileName = Path.GetFileName(file), SaveFilePath = file, Status = DownloadStatus.Completed
        }));
        return (package, path, original);
    }
}
