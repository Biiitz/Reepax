using System;
using System.Runtime.InteropServices;
using System.Threading;
using Reepax.Services.Storage;

namespace Reepax.Services.SystemIntegration;

/// <summary>
/// Controls Windows power execution states to prevent system standby or sleep
/// while downloads or archive extractions are active, without forcing the display to remain on.
/// </summary>
public sealed class PowerManagementService
{
    private static readonly Lazy<PowerManagementService> _instance = new(() => new PowerManagementService());
    public static PowerManagementService Instance => _instance.Value;

    [Flags]
    public enum ExecutionState : uint
    {
        EsSystemRequired = 0x00000001,
        EsDisplayRequired = 0x00000002,
        EsAwayModeRequired = 0x00000040,
        EsContinuous = 0x80000000
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    [DllImport("PowrProf.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    private int _keepAwakeCount;
    private readonly object _lock = new();

    private PowerManagementService() { }

    /// <summary>
    /// Executes a clean Windows system shutdown.
    /// </summary>
    public void ExecuteShutdown()
    {
        if (DownloadPersistenceService.IsTestEnvironment)
        {
            AppLogger.Info("[PowerManagement] Test environment active: Shutdown suppressed.");
            return;
        }

        try
        {
            AppLogger.Info("[PowerManagement] Performing safe shutdown before initiating Windows shutdown...");
            Download.QueueManager.PerformSafeShutdown();

            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe", "/s /t 0 /f")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("[PowerManagement] Failed to initiate Windows shutdown", ex);
        }
    }

    /// <summary>
    /// Puts Windows into sleep (standby) or hibernate.
    /// </summary>
    public void ExecuteSleepOrHibernate()
    {
        if (DownloadPersistenceService.IsTestEnvironment)
        {
            AppLogger.Info("[PowerManagement] Test environment active: Sleep/Hibernate suppressed.");
            return;
        }

        try
        {
            AppLogger.Info("[PowerManagement] Performing safe shutdown before initiating Windows sleep/hibernate...");
            Download.QueueManager.PerformSafeShutdown();

            if (OperatingSystem.IsWindows())
            {
                ReleaseAll();
                SetSuspendState(false, false, false);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("[PowerManagement] Failed to initiate sleep/hibernate", ex);
        }
    }

    /// <summary>
    /// Performs safe shutdown and closes the Reepax application.
    /// </summary>
    public void ExecuteExitApp()
    {
        try
        {
            AppLogger.Info("[PowerManagement] Exiting Reepax application after downloads completed...");
            Download.QueueManager.PerformSafeShutdown();

            var app = System.Windows.Application.Current;
            if (app?.Dispatcher != null && !app.Dispatcher.HasShutdownStarted)
            {
                app.Dispatcher.Invoke(() =>
                {
                    if (app.MainWindow is MainWindow mw)
                    {
                        mw.ForceExit();
                    }
                    else
                    {
                        app.Shutdown();
                    }
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("[PowerManagement] Failed to exit application", ex);
        }
    }

    /// <summary>
    /// Gets whether Windows sleep prevention is currently active.
    /// </summary>
    public bool IsKeepAwakeActive => Volatile.Read(ref _keepAwakeCount) > 0;

    /// <summary>
    /// Acquires a keep-awake request. As long as at least one operation holds a request,
    /// Windows is instructed not to enter sleep or standby.
    /// </summary>
    public void AcquireKeepAwake(string reason = "")
    {
        lock (_lock)
        {
            var count = Interlocked.Increment(ref _keepAwakeCount);
            if (count == 1)
            {
                ApplyExecutionState(true, reason);
            }
        }
    }

    /// <summary>
    /// Releases a previously acquired keep-awake request.
    /// When the active request count drops to zero, normal OS power saving is restored.
    /// </summary>
    public void ReleaseKeepAwake(string reason = "")
    {
        lock (_lock)
        {
            var count = Interlocked.Decrement(ref _keepAwakeCount);
            if (count <= 0)
            {
                Interlocked.Exchange(ref _keepAwakeCount, 0);
                ApplyExecutionState(false, reason);
            }
        }
    }

    /// <summary>
    /// Immediately releases all keep-awake requests (e.g. on application exit).
    /// </summary>
    public void ReleaseAll()
    {
        lock (_lock)
        {
            Interlocked.Exchange(ref _keepAwakeCount, 0);
            ApplyExecutionState(false, "Application Shutdown");
        }
    }

    private static void ApplyExecutionState(bool enable, string reason)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (enable)
                {
                    // ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_AWAYMODE_REQUIRED
                    // Prevents sleep while allowing the display to shut down according to user settings
                    var previous = SetThreadExecutionState(
                        ExecutionState.EsContinuous |
                        ExecutionState.EsSystemRequired |
                        ExecutionState.EsAwayModeRequired);

                    AppLogger.Info($"[PowerManagement] Windows sleep prevention activated ({reason}). Previous state: 0x{(uint)previous:X8}");
                }
                else
                {
                    // Restores default power management behavior
                    var previous = SetThreadExecutionState(ExecutionState.EsContinuous);
                    AppLogger.Info($"[PowerManagement] Windows sleep prevention released ({reason}). Previous state: 0x{(uint)previous:X8}");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[PowerManagement] Failed to update execution state: {ex.Message}");
            }
        }
    }
}
