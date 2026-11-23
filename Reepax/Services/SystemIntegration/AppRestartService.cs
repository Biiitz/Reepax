using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Reepax.Services.Download;
using Reepax.Services.Storage;
using Reepax.Services.Verification;

namespace Reepax.Services.SystemIntegration;

public sealed class AppRestartService
{
    private const string RestartParentPrefix = "--restart-parent=";
    internal static AppRestartService Instance { get; } = new(Process.Start, ExitApplication);

    private readonly Func<ProcessStartInfo, Process?> _startProcess;
    private readonly Action _exitApplication;
    private int _isRestarting;

    internal AppRestartService(Func<ProcessStartInfo, Process?> startProcess, Action exitApplication)
    {
        _startProcess = startProcess;
        _exitApplication = exitApplication;
    }

    public async Task<bool> TryRestartAsync()
    {
        // A restart is already in progress.
        if (Interlocked.Exchange(ref _isRestarting, 1) != 0)
            return true;

        bool replacementStarted = false;
        var repairs = Par2RepairService.Instance;
        Task? repairsFinished = null;
        try
        {
            AppLogger.Info("[AppRestartService] command.");

            var executable = ResolveExecutablePath();
            if (executable == null)
            {
                AppLogger.Error("[AppRestartService] Could not determine application executable path for restart.");
                return false;
            }

            repairsFinished = repairs.TryBeginExit();
            if (repairsFinished == null) return false;
            await repairsFinished;
            if (repairs.IsWindowsSessionEnding) return false;

            SaveWindowState();

            try
            {
                var start = CreateStartInfo(executable, Environment.GetCommandLineArgs());
                using var replacement = _startProcess(start);
                replacementStarted = replacement != null;
            }
            catch (Exception ex)
            {
                AppLogger.Error("[AppRestartService] Failed to start new instance", ex);
                return false;
            }

            if (!replacementStarted)
            {
                AppLogger.Error("[AppRestartService] Process start did not return a replacement instance.");
                return false;
            }

            try
            {
                QueueManager.Instance.StopQueue(isExiting: true);
                SettingsService.Instance.SaveSettings();
            }
            catch (Exception ex)
            {
                AppLogger.Error("[AppRestartService] Error during restart preparation", ex);
            }

            _exitApplication();
            return true;
        }
        finally
        {
            if (repairsFinished != null && (!replacementStarted || DownloadPersistenceService.IsTestEnvironment))
                repairs.CancelExit();
            if (!replacementStarted)
                Interlocked.Exchange(ref _isRestarting, 0);
        }
    }

    private static string? ResolveExecutablePath()
    {
        string? executable = Environment.ProcessPath;
        if (File.Exists(executable))
            return executable;

        try
        {
            using var current = Process.GetCurrentProcess();
            executable = current.MainModule?.FileName;
            return File.Exists(executable) ? executable : null;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveWindowState()
    {
        try
        {
            var app = Application.Current;
            if (!DownloadPersistenceService.IsTestEnvironment &&
                app != null &&
                app.Dispatcher.Thread.IsAlive &&
                !app.Dispatcher.HasShutdownStarted &&
                !app.Dispatcher.HasShutdownFinished)
            {
                app.Dispatcher.Invoke(() =>
                {
                    if (app.MainWindow is MainWindow window)
                        window.PrepareForRestart();
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("[AppRestartService] Error saving window state before restart", ex);
        }
    }

    private static void ExitApplication()
    {
        try
        {
            if (Application.Current is { } app)
            {
                // The application releases single-instance ownership after shutdown has saved its state.
                app.Dispatcher.Invoke(() =>
                {
                    if (app.MainWindow is MainWindow window)
                        window.ForceExit();
                    app.Shutdown();
                });
                return;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("[AppRestartService] Error closing the original instance", ex);
        }

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
        using var current = Process.GetCurrentProcess();
        start.ArgumentList.Add(CreateRestartParentArgument(current));
        return start;
    }

    internal static string CreateRestartParentArgument(Process process) =>
        FormattableString.Invariant($"{RestartParentPrefix}{process.Id}:{process.StartTime.ToUniversalTime().Ticks}");

    internal static void WaitForRestartParent(string[] arguments)
    {
        var argument = arguments.FirstOrDefault(value => value.StartsWith(RestartParentPrefix, StringComparison.Ordinal));
        if (argument == null) return;

        var identity = argument[RestartParentPrefix.Length..].Split(':', 2);
        if (identity.Length != 2 ||
            !int.TryParse(identity[0], NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id <= 0 || id == Environment.ProcessId ||
            !long.TryParse(identity[1], NumberStyles.None, CultureInfo.InvariantCulture, out long startedAt))
            return;

        try
        {
            using var parent = Process.GetProcessById(id);
            // A recycled process ID must not keep the replacement waiting.
            _ = parent.SafeHandle;
            if (parent.StartTime.ToUniversalTime().Ticks == startedAt)
                parent.WaitForExit();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // The previous process may have exited before its handle could be opened.
        }
    }
}
