using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Views;

namespace Reepax.Services.Browser;

/// <summary>
/// Manages the pool of external browser windows (Scenario C).
/// Windows are opened when a link is dispatched, and closed upon successful
/// download interception or user close.
/// The maximum count is limited via QueueManager ("Simultaneous Downloads").
/// Must be invoked on the UI thread.
/// </summary>
public class BrowserWindowManager : IBrowserWindowHost
{
    private readonly Dictionary<int, BrowserWindow> _windows = new();
    private int _nextWindowId;
    private DateTime _lastWindowOpenedUtc = DateTime.MinValue;
    private bool _cooldownNotificationScheduled;

    public event Action? WindowCapacityChanged;

    public int ActiveWindowCount => _windows.Count;

    public bool TryOpenWindow(DownloadItem item, bool hidden, out int windowId)
    {
        windowId = 0;

        // Minor cooldown (400-900 ms jitter) between window openings to prevent hosters
        // from being overwhelmed by burst requests.
        var elapsed = DateTime.UtcNow - _lastWindowOpenedUtc;
        var cooldown = TimeSpan.FromMilliseconds(Random.Shared.Next(400, 901));
        if (elapsed < cooldown)
        {
            ScheduleCapacityNotification(cooldown - elapsed);
            return false;
        }

        var id = ++_nextWindowId;
        var window = new BrowserWindow(id, item, hidden);
        _windows[id] = window;
        _lastWindowOpenedUtc = DateTime.UtcNow;

        window.Closed += (_, _) =>
        {
            _windows.Remove(id);
            // Report item back to the queue (no-op if the download was already intercepted)
            QueueManager.Instance.OnBrowserWindowClosed(id);
            WindowCapacityChanged?.Invoke();
        };

        windowId = id;
        window.Show();
        if (window.WindowState != System.Windows.WindowState.Minimized)
        {
            window.Activate();
            window.Focus();
        }
        return true;
    }

    public void CloseWindow(int windowId)
    {
        if (_windows.TryGetValue(windowId, out var window))
        {
            try { window.Close(); } catch { }
        }
    }

    public void CloseAllWindows()
    {
        foreach (var window in _windows.Values.ToArray())
        {
            try { window.Close(); } catch { }
        }
        _windows.Clear();
    }

    private void ScheduleCapacityNotification(TimeSpan delay)
    {
        if (_cooldownNotificationScheduled)
            return;

        _cooldownNotificationScheduled = true;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay); } catch { }
            _cooldownNotificationScheduled = false;
            WindowCapacityChanged?.Invoke();
        });
    }
}
