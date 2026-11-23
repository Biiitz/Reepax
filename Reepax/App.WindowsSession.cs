using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Reepax.Services.Download;
using Reepax.Services.Storage;
using Reepax.Services.Verification;

namespace Reepax;

public partial class App
{
    private const uint WmQueryEndSession = 0x0011;
    internal const int WmEndSession = 0x0016;
    private const uint WmNcDestroy = 0x0082;
    private WindowSubclassProc? _sessionWindowProc;
    private bool _endingWindowsSession;

    internal void InstallWindowsSessionHandler()
    {
        try
        {
            // WPF 8 handles WM_QUERYENDSESSION on its private parking window.
            var parkingWindow = typeof(Application).GetField("_parkingHwnd", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(this);
            if (parkingWindow?.GetType().GetProperty("Handle")?.GetValue(parkingWindow) is nint handle && handle != 0)
            {
                _sessionWindowProc ??= SessionWindowProc;
                if (SetWindowSubclass(handle, _sessionWindowProc, 0, 0)) return;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("[App] Failed to install the Windows session handler.", ex);
            return;
        }
        AppLogger.Warn("[App] Windows session handler unavailable. Using WPF session shutdown.");
    }

    private nint SessionWindowProc(nint handle, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == WmQueryEndSession) return 1;
        if (message == WmEndSession)
        {
            if (wParam != 0) CompleteWindowsSession();
            return 0;
        }
        if (message == WmNcDestroy)
            RemoveWindowSubclass(handle, _sessionWindowProc!, id);

        return DefSubclassProc(handle, message, wParam, lParam);
    }

    internal void CompleteWindowsSession()
    {
        if (!_isPrimaryInstance || _endingWindowsSession) return;
        _endingWindowsSession = true;

        try
        {
            FinishSessionRepairs();
            if (MainWindow is MainWindow window) window.ForceExit();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("[App] Failed to close the main window at Windows session end.", ex);
        }
        finally
        {
            QueueManager.PerformSafeShutdown();
            Shutdown();
        }
    }

    private static void FinishSessionRepairs()
    {
        var finished = Par2RepairService.Instance.BeginWindowsSessionExit();
        if (!finished.IsCompleted)
        {
            // The session can end as soon as this message returns. Keep the dispatcher running for PAR2 cleanup.
            var frame = new DispatcherFrame();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var timeout = deadline.Token.Register(() => frame.Continue = false);
            _ = finished.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        if (finished.IsCompleted)
            finished.GetAwaiter().GetResult();
        else
            AppLogger.Warn("[App] PAR2 cleanup did not finish within the Windows session timeout.");
    }

    private delegate nint WindowSubclassProc(nint handle, uint message, nint wParam, nint lParam, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint handle, WindowSubclassProc callback, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint handle, WindowSubclassProc callback, nuint id);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint handle, uint message, nint wParam, nint lParam);
}
