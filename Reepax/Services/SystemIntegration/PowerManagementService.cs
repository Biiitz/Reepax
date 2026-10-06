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

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    private struct TOKEN_PRIVILEGES
    {
        public int PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privileges;
    }

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;
    private const string SE_SHUTDOWN_NAME = "SeShutdownPrivilege";

    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr TokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool DisableAllPrivileges,
        ref TOKEN_PRIVILEGES NewState,
        uint BufferLength,
        IntPtr PreviousState,
        IntPtr ReturnLength);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentProcess();

    private int _keepAwakeCount;
    private readonly object _lock = new();

    private PowerManagementService() { }

    /// <summary>
    /// Enables the SeShutdownPrivilege in the current process access token.
    /// Required by Windows for SetSuspendState and other shutdown/power operations.
    /// </summary>
    public static bool EnableShutdownPrivilege()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        IntPtr tokenHandle = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out tokenHandle))
            {
                var error = Marshal.GetLastWin32Error();
                AppLogger.Warn($"[PowerManagement] OpenProcessToken failed with error: {error}");
                return false;
            }

            if (!LookupPrivilegeValue(null, SE_SHUTDOWN_NAME, out var luid))
            {
                var error = Marshal.GetLastWin32Error();
                AppLogger.Warn($"[PowerManagement] LookupPrivilegeValue failed with error: {error}");
                return false;
            }

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new LUID_AND_ATTRIBUTES
                {
                    Luid = luid,
                    Attributes = SE_PRIVILEGE_ENABLED
                }
            };

            if (!AdjustTokenPrivileges(tokenHandle, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                AppLogger.Warn($"[PowerManagement] AdjustTokenPrivileges failed with error: {error}");
                return false;
            }

            var win32Err = Marshal.GetLastWin32Error();
            if (win32Err != 0)
            {
                AppLogger.Warn($"[PowerManagement] AdjustTokenPrivileges completed with status code: {win32Err}");
            }

            AppLogger.Info("[PowerManagement] Successfully enabled SeShutdownPrivilege.");
            return win32Err == 0;
        }
        catch (Exception ex)
        {
            AppLogger.Error("[PowerManagement] Failed to enable SeShutdownPrivilege", ex);
            return false;
        }
        finally
        {
            if (tokenHandle != IntPtr.Zero)
            {
                CloseHandle(tokenHandle);
            }
        }
    }

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
                EnableShutdownPrivilege();

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

                bool privilegeEnabled = EnableShutdownPrivilege();
                if (!privilegeEnabled)
                {
                    AppLogger.Warn("[PowerManagement] Could not enable SeShutdownPrivilege; SetSuspendState might fail.");
                }

                bool success = SetSuspendState(false, false, false);
                if (!success)
                {
                    int win32Error = Marshal.GetLastWin32Error();
                    AppLogger.Warn($"[PowerManagement] SetSuspendState returned false with Win32 error code {win32Error}. Attempting fallback via rundll32...");

                    try
                    {
                        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0")
                        {
                            CreateNoWindow = true,
                            UseShellExecute = false
                        });
                    }
                    catch (Exception fallbackEx)
                    {
                        AppLogger.Error("[PowerManagement] Fallback to rundll32 failed", fallbackEx);
                    }
                }
                else
                {
                    AppLogger.Info("[PowerManagement] SetSuspendState successfully initiated system sleep/standby.");
                }
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
