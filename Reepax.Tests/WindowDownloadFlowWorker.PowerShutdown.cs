using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;
using Reepax.Services.Verification;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

internal static partial class WindowDownloadFlowWorker
{
    private sealed class SessionEndingApp : App
    {
        internal bool UseWpfFallback { get; set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            // Reach the primary session handler without acquiring the real application instance.
            typeof(App).GetField("_isPrimaryInstance", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, true);
            if (!UseWpfFallback) InstallWindowsSessionHandler();
        }

        internal nint SendSessionMessage(uint message, nint wParam = 0, nint flags = 0)
        {
            var parkingWindow = typeof(Application).GetField("_parkingHwnd", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
            var handle = (nint)parkingWindow.GetType().GetProperty("Handle")!.GetValue(parkingWindow)!;
            Assert.NotEqual(nint.Zero, handle);
            return SendMessage(handle, message, wParam, flags);
        }
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    private static void RunPowerShutdown(SessionEndingApp app, string directory, string target)
    {
        ExceptionDispatchInfo? failure = null;
        bool completed = false;
        app.UseWpfFallback = target == "par2-fallback";
        app.Dispatcher.BeginInvoke(() =>
        {
            if (target.StartsWith("par2-", StringComparison.Ordinal))
            {
                _ = CheckPar2SessionEndAsync(app, directory, target, Complete);
                return;
            }
            try
            {
                CheckPowerShutdown(app, directory, confirmAtMainWindow: target == "main");
                Complete(null);
            }
            catch (Exception error) { Complete(error); }
        });
        app.Run();
        failure?.Throw();
        Assert.True(completed, "The session ended before the test finished checking the repair.");

        void Complete(Exception? error)
        {
            if (error != null) failure = ExceptionDispatchInfo.Capture(error);
            completed = true;
            app.Shutdown();
        }
    }

    private static async Task CheckPar2SessionEndAsync(SessionEndingApp app, string directory, string target, Action<Exception?> complete)
    {
        try
        {
            byte[] independent = [1, 3, 5, 7];
            var sample = await CreateDamagedPar2PackageAsync(directory, "Repair sample", independent);
            byte[] damaged = File.ReadAllBytes(sample.Path);
            var main = new MainWindow { ShowActivated = false, ShowInTaskbar = false };
            main.Show();
            var model = (MainViewModel)main.DataContext;
            QueueManager.Instance.Packages.Add(sample.Package);
            await using var checkpoint = new Par2ExtractionCancellationTests.NativeCheckpoint(repairStage: true);
            _ = model.Par2RepairPackageCommand.ExecuteAsync(sample.Package);
            var process = await checkpoint.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            try
            {
                Assert.False(process.HasExited);
                const uint query = 0x0011;
                const uint end = 0x0016;
                nint logoff = unchecked((int)0x80000000);
                nint mainHandle = new WindowInteropHelper(main).Handle;
                if (!app.UseWpfFallback)
                {
                    Assert.NotEqual(nint.Zero, app.SendSessionMessage(query, flags: logoff));
                    app.SendSessionMessage(end, flags: logoff);
                    Assert.True(main.IsVisible);
                    Assert.False(process.HasExited);
                }

                // Reproduce the on-disk state after PAR2 renames the original and starts writing its replacement.
                File.Move(sample.Path, sample.Path + ".2");
                File.WriteAllBytes(sample.Path, [4, 2]);
                if (app.UseWpfFallback)
                    app.SendSessionMessage(query, flags: logoff);
                else if (target == "par2-main")
                    SendMessage(mainHandle, end, 1, logoff);
                else
                    app.SendSessionMessage(end, wParam: 1, flags: logoff);

                Assert.True(process.HasExited, "The session handler returned with the PAR2 process still running.");
                Assert.False(main.IsVisible);
                Assert.False(Par2RepairService.Instance.HasActiveRepairs);
                Assert.Equal(damaged, File.ReadAllBytes(sample.Path));
                Assert.False(File.Exists(sample.Path + ".2"));
                Assert.Equal(independent, File.ReadAllBytes(sample.Path + ".1"));
                Assert.Equal(independent, File.ReadAllBytes(Path.Combine(sample.Package.SaveDirectory, "neighbor.bin")));
                complete(null);
            }
            catch (Exception error) { complete(error); }
        }
        catch (Exception error) { complete(error); }
    }

    private static void CheckPowerShutdown(SessionEndingApp app, string directory, bool confirmAtMainWindow)
    {
        var main = new MainWindow { ShowActivated = false, ShowInTaskbar = false };
        var queue = QueueManager.Instance;
        try
        {
            main.Show();
            Assert.Same(main, app.MainWindow);
            ((MainViewModel)main.DataContext).MinimizeToTrayOnClose = true;
            queue.StartQueue();
            string path = Path.Combine(directory, "sample.bin");
            var item = new DownloadItem
            {
                FileName = "sample.bin", SaveFilePath = path,
                TotalBytes = 10, Status = DownloadStatus.Paused
            };
            var package = new DownloadPackage { Name = "Sample", SaveDirectory = directory, IsCustomName = true };
            package.AddItems([item]);
            queue.Packages.Add(package);
            File.WriteAllBytes(path + ".part", [1, 2, 3]);

            // Enable persistence only for this worker's temporary profile.
            DownloadPersistenceService.IsTestEnvironment = false;
            ProcessStartInfo? request = null;
            long? savedBeforeRequest = null;
            PowerManagementService.ExecuteShutdown(startInfo =>
            {
                request = startInfo;
                savedBeforeRequest = ReadSavedProgress();
                throw new Win32Exception(2);
            });

            Assert.NotNull(request);
            Assert.Equal("shutdown.exe", request.FileName);
            Assert.Equal("/s /t 0", request.Arguments);
            Assert.False(request.UseShellExecute);
            Assert.Equal(3, savedBeforeRequest);
            CheckTrayRestore();
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path + ".part"));

            File.WriteAllBytes(path + ".part", [1, 2, 3, 4]);
            long? savedBeforeSleep = null;
            ProcessStartInfo? fallback = null;
            PowerManagementService.ExecuteSleepOrHibernate(() =>
            {
                savedBeforeSleep = ReadSavedProgress();
                return false;
            }, startInfo =>
            {
                fallback = startInfo;
                throw new Win32Exception(2);
            });
            Assert.Equal(4, savedBeforeSleep);
            Assert.NotNull(fallback);
            Assert.Equal("rundll32.exe", fallback.FileName);
            CheckTrayRestore();

            const uint queryEndSession = 0x0011;
            const uint endSession = 0x0016;
            nint mainHandle = new WindowInteropHelper(main).Handle;
            Assert.NotEqual(nint.Zero, mainHandle);
            Assert.NotEqual(nint.Zero, app.SendSessionMessage(queryEndSession));
            Assert.NotEqual(nint.Zero, SendMessage(mainHandle, queryEndSession, 0, 0));
            app.SendSessionMessage(endSession);
            SendMessage(mainHandle, endSession, 0, 0);
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.True(main.IsVisible);
            Assert.True(queue.IsRunning);
            CheckTrayRestore();
            Assert.Equal(4, ReadSavedProgress());

            byte[] finalPart = [1, 2, 3, 4, 5];
            File.WriteAllBytes(path + ".part", finalPart);
            main.Close();
            Assert.False(main.IsVisible);
            nint logoff = unchecked((int)0x80000000);
            Assert.NotEqual(nint.Zero, app.SendSessionMessage(queryEndSession, flags: logoff));
            Assert.Contains(main, app.Windows.Cast<Window>());
            Assert.True(queue.IsRunning);
            if (confirmAtMainWindow)
                SendMessage(mainHandle, endSession, 1, logoff);
            else
                app.SendSessionMessage(endSession, wParam: 1, flags: logoff);
            Assert.False(main.IsVisible);
            Assert.False(queue.IsRunning);
            Assert.Equal(finalPart.Length, ReadSavedProgress());
            var saved = Assert.Single(Assert.Single(DownloadPersistenceService.Instance.LoadDownloads()).Items);
            Assert.Equal(item.Id, saved.Id);
            Assert.Equal(DownloadStatus.Paused, saved.Status);
            Assert.Equal(finalPart.Length, saved.DownloadedBytes);
            Assert.Equal(finalPart, File.ReadAllBytes(path + ".part"));
        }
        finally
        {
            if (app.Windows.Cast<Window>().Contains(main)) main.ForceExit();
            DownloadPersistenceService.IsTestEnvironment = true;
        }

        void CheckTrayRestore()
        {
            main.Close();
            Assert.False(main.IsVisible);
            TrayIconService.Instance.RestoreMainWindow();
            Assert.True(main.IsVisible);
            Assert.True(queue.IsRunning);
        }

        static long ReadSavedProgress()
        {
            // Loading items can also reconstruct progress from the partial file.
            string path = Path.Combine(SettingsService.AppDataDirectory, "downloads.json");
            string json = SecureAppDataStorage.DecryptString(File.ReadAllText(path));
            using var document = JsonDocument.Parse(json);
            return document.RootElement[0].GetProperty("Items")[0].GetProperty("DownloadedBytes").GetInt64();
        }
    }
}
