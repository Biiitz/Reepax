using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

internal static partial class WindowDownloadFlowWorker
{
    private sealed class SessionEndingApp : App
    {
        protected override void OnStartup(StartupEventArgs e) { }

        internal void SimulateSessionEnding()
        {
            // Avoid acquiring production single-instance ownership to reach the primary session handler.
            typeof(App).GetField("_isPrimaryInstance", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, true);
            var args = (SessionEndingCancelEventArgs)Activator.CreateInstance(typeof(SessionEndingCancelEventArgs),
                BindingFlags.Instance | BindingFlags.NonPublic, binder: null, args: [ReasonSessionEnding.Shutdown], culture: null)!;
            OnSessionEnding(args);
            Assert.False(args.Cancel);
        }
    }

    private static void CheckPowerShutdown(SessionEndingApp app, string directory)
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

            byte[] finalPart = [1, 2, 3, 4, 5];
            File.WriteAllBytes(path + ".part", finalPart);
            app.SimulateSessionEnding();
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
