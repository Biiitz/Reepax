using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using Reepax.Services.Download;
using Reepax.Services.Storage;

namespace Reepax.Services.SystemIntegration;

public static class AppRestartService
{
    private static int _isRestarting;

    /// <summary>
    /// Test hook: Action invoked instead of launching a process and exiting when in test environment.
    /// </summary>
    public static Action? RestartActionOverride { get; set; }

    /// <summary>
    /// Pauses all active downloads, saves application state and downloads,
    /// disposes mutex/IPC, and starts a new instance of Reepax.
    /// </summary>
    public static void Restart()
    {
        if (Interlocked.Exchange(ref _isRestarting, 1) != 0)
            return;

        AppLogger.Info("[AppRestartService] command.");

        try
        {
            // 1. Prepare MainWindow state and column widths if UI is alive
            var app = Application.Current;
            if (!DownloadPersistenceService.IsTestEnvironment &&
                app?.Dispatcher != null &&
                app.Dispatcher.Thread?.IsAlive == true &&
                !app.Dispatcher.HasShutdownStarted &&
                !app.Dispatcher.HasShutdownFinished)
            {
                if (app.Dispatcher.CheckAccess())
                {
                    if (app.MainWindow is MainWindow mw)
                    {
                        mw.PrepareForRestart();
                    }
                }
                else
                {
                    app.Dispatcher.Invoke(() =>
                    {
                        if (app.MainWindow is MainWindow mw)
                        {
                            mw.PrepareForRestart();
                        }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[AppRestartService] Error saving window state before restart", ex);
        }

        try
        {
            // 2. Pause all downloads, flush streams, persist downloads and settings
            QueueManager.Instance.StopQueue(isExiting: true);
            SettingsService.Instance.SaveSettings();
        }
        catch (Exception ex)
        {
            AppLogger.Error("[AppRestartService] Error during safe shutdown", ex);
        }

        try
        {
            // 3. Hide tray icon immediately
            TrayIconService.Instance.HideTrayIcon();
            TrayIconService.Instance.Dispose();
        }
        catch { }

        // Test hook check
        if (DownloadPersistenceService.IsTestEnvironment)
        {
            RestartActionOverride?.Invoke();
            _isRestarting = 0;
            return;
        }

        // 4. Resolve executable path
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            try
            {
                exePath = Process.GetCurrentProcess().MainModule?.FileName;
            }
            catch { }
        }

        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            AppLogger.Error("[AppRestartService] Could not determine application executable path for restart.");
            _isRestarting = 0;
            return;
        }

        var psi = CreateStartInfo(exePath, Environment.GetCommandLineArgs());

        // 6. Release single instance mutex & IPC so the new instance can acquire ownership without race condition
        try
        {
            SingleInstanceService.Instance.Dispose();
        }
        catch { }

        // 7. Launch new process
        try
        {
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            AppLogger.Error("[AppRestartService] Failed to start new instance", ex);
        }

        // 8. Terminate current process
        try
        {
            var app = Application.Current;
            app?.Dispatcher?.Invoke(() =>
            {
                if (app.MainWindow is MainWindow mw)
                {
                    mw.ForceExit();
                }
                app.Shutdown();
            });
        }
        catch { }

        Environment.Exit(0);
    }

    internal static ProcessStartInfo CreateStartInfo(string executable, string[] commandLine)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
        };

        // A framework-dependent launch through dotnet still needs the application assembly.
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase) &&
            commandLine.Length > 0 && Path.GetExtension(commandLine[0]).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(commandLine[0]);

        // Forward window startup options, but do not replay file opens or update handshakes.
        foreach (string argument in commandLine.Skip(1))
        {
            if (string.Equals(argument, "--minimized", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argument, "--autostart", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(argument);
        }
        start.ArgumentList.Add("--restart");
        return start;
    }
}
