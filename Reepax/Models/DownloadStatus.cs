namespace Reepax.Models;

public enum DownloadStatus
{
    Queued,
    WaitingForBrowser,
    // Legacy (persistence compatibility): former fixed embedded slots
    InBrowserSlot1,
    InBrowserSlot2,
    SolvingCaptcha,
    Downloading,
    Paused,
    Completed,
    Failed,
    Aborted,
    // Item is handled in an external browser window (Scenario C)
    InBrowser
}
