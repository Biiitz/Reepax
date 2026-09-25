using System;
using Reepax.Models;

namespace Reepax.Services.Browser;

/// <summary>
/// Abstraction of the external browser window pool (Scenario C).
/// Enables unit testing of QueueManager without real WebView2 windows.
/// </summary>
public interface IBrowserWindowHost
{
    /// <summary>Number of currently open browser windows.</summary>
    int ActiveWindowCount { get; }

    /// <summary>Triggered when window capacity becomes available again (window closed or opening cooldown expired).</summary>
    event Action? WindowCapacityChanged;

    /// <summary>
    /// Opens a new external browser window for the item.
    /// hidden=true: Window stays invisible (e.g. auto-resolving FastHost)
    /// and becomes visible automatically on timeout.
    /// Returns false if an opening cooldown is currently active
    /// (WindowCapacityChanged is fired once expired).
    /// Must be invoked on the UI thread.
    /// </summary>
    bool TryOpenWindow(DownloadItem item, bool hidden, out int windowId);

    /// <summary>Closes the window with the specified ID (if open).</summary>
    void CloseWindow(int windowId);

    /// <summary>Closes all open browser windows.</summary>
    void CloseAllWindows();
}
