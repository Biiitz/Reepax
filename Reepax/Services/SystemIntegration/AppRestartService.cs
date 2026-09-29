using System;
using System.Collections.Generic;
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

        AppLogger.Info("[AppRestartService] Application restart requested via keyboard shortcut / command.");

        try
        {
            // 1. Prepare MainWindow state and column widths if UI is alive
            if (Application.Current?.Dispatcher != null &&
                !Application.Current.Dispatcher.HasShutdownStarted &&
                !Application.Current.Dispatcher.HasShutdownFinished)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (Application.Current.MainWindow is MainWindow mw)
                    {
                        mw.PrepareForRestart();
                    }
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[AppRestartService] Error saving window state before restart: {ex.Message}");
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

        // 5. Preserve existing arguments (e.g. --portable) and append --restart flag
        var cmdArgs = Environment.GetCommandLineArgs();
        var arguments = new List<string>();
        if (cmdArgs.Length > 1)
        {
            for (int i = 1; i < cmdArgs.Length; i++)
            {
                var arg = cmdArgs[i];
                if (!string.Equals(arg, "--restart", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(arg, "--activate", StringComparison.OrdinalIgnoreCase))
                {
                    arguments.Add(arg.Contains(' ') ? $"\"{arg}\"" : arg);
                }
            }
        }
        arguments.Add("--restart");
        var argString = string.Join(" ", arguments);

        // 6. Release single instance mutex & IPC so the new instance can acquire ownership without race condition
        try
        {
            SingleInstanceService.Instance.Dispose();
        }
        catch { }

        // 7. Launch new process
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = argString,
                UseShellExecute = true,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            AppLogger.Error("[AppRestartService] Failed to start new instance", ex);
        }

        // 8. Terminate current process
        try
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                if (Application.Current.MainWindow is MainWindow mw)
                {
                    mw.ForceExit();
                }
                Application.Current.Shutdown();
            });
        }
        catch { }

        Environment.Exit(0);
    }
}
