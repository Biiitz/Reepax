using System;
using System.IO;

namespace Reepax.Models;

public class AppSettings
{
    public string DefaultDownloadDirectory { get; set; } = 
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { } profile && !string.IsNullOrWhiteSpace(profile)
            ? Path.Combine(profile, "Downloads")
            : string.Empty;

    public int MaxConcurrentBackgroundDownloads { get; set; } = 2;
    public int ConnectionsPerDownload { get; set; } = 5; // 1-20 parallel connections (chunks) per file
    public int MaxConcurrentDownloads 
    { 
        get => MaxConcurrentBackgroundDownloads; 
        set => MaxConcurrentBackgroundDownloads = value; 
    }
    public bool IsDarkMode { get; set; } = true;
    public bool EnableForcedDarkMode { get; set; } = true;
    public bool EnableAdBlocker { get; set; } = true;
    public double SpeedLimitMBps { get; set; } = 0; // 0 = unlimited
    public long SpeedLimitBytesPerSecond { get; set; } = 0; // 0 = unlimited
    public bool AutoStartDownloads { get; set; } = false;
    public bool EnableAutoHostResolver { get; set; } = false;
    public string AccentColorHex { get; set; } = "#3B82F6";
    public bool IsColorPaletteExpanded { get; set; } = true;
    public bool MinimizeToTrayOnClose { get; set; } = false;
    public bool StartWithWindows { get; set; } = false;
    public bool EnableCompletionNotifications { get; set; } = false;
    public bool AutoCollapseCompletedPackages { get; set; } = true;
    public bool EnableFileLogging { get; set; } = false;
    public string Language { get; set; } = "en"; // "en" (default) or "de"
    public Dictionary<string, string> CustomShortcuts { get; set; } = new();

    // Core Engine Security & Origin Descriptor
    [System.Text.Json.Serialization.JsonPropertyName("securityDescriptor")]
    public string SecurityDescriptor { get; set; } = "made by Biiitz";

    // Archive Extractor & Integrity Settings
    public bool AutoExtractArchives { get; set; } = false;
    public bool DeleteArchiveAfterExtraction { get; set; } = false;
    public bool MoveArchiveToRecycleBin { get; set; } = false; // true = send to recycle bin, false = permanent delete
    public bool? LowResourceExtraction { get; set; } = false; // Always false by default; enabled only when manually toggled
    public bool EnableChecksumVerification { get; set; } = true;
    public int MaxAutoRetryOnCorruption { get; set; } = 3;
    public bool AutoPar2Repair { get; set; } = false;
    public bool DeletePar2AfterExtraction { get; set; } = false;

    // Game Installation Directory Settings
    public bool CreateGameInstallFolder { get; set; } = false;
    public string GameInstallDirectory { get; set; } = GetDefaultGameInstallDirectory();

    private static string GetDefaultGameInstallDirectory()
    {
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        return Directory.Exists(systemDrive) 
            ? Path.Combine(systemDrive, "Games") 
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games");
    }

    // Window Geometry & State
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 780;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool IsWindowMaximized { get; set; } = false;
    public double AddLinksWindowWidth { get; set; } = 780;
    public double AddLinksWindowHeight { get; set; } = 520;
    public double? AddLinksWindowLeft { get; set; }
    public double? AddLinksWindowTop { get; set; }
    public bool IsAddLinksWindowMaximized { get; set; } = false;
    public double UpdateDialogWidth { get; set; } = 600;
    public double UpdateDialogHeight { get; set; } = 450;
    public double? UpdateDialogLeft { get; set; }
    public double? UpdateDialogTop { get; set; }
    public bool IsUpdateDialogMaximized { get; set; } = false;

    // TreeListView Column Widths
    public double ColWidthName { get; set; } = 340;
    public double ColWidthHoster { get; set; } = 120;
    public double ColWidthSavePath { get; set; } = 180;
    public double ColWidthSize { get; set; } = 95;
    public double ColWidthProgress { get; set; } = 180;
    public double ColWidthSpeed { get; set; } = 105;
    public double ColWidthEta { get; set; } = 85;
    public double ColWidthStatus { get; set; } = 160;
    public double ColWidthAddedDate { get; set; } = 130;
    public double ColWidthCompletedDate { get; set; } = 130;
    public double ColWidthChecksum { get; set; } = 110;
    public double ColWidthActions { get; set; } = 135;

    [System.Text.Json.Serialization.JsonPropertyName("_engineSignature")]
    public string EngineSignature { get; set; } = "made by Biiitz";

    // TreeListView Column Visibility
    public bool ShowColName { get; set; } = true;
    public bool ShowColHoster { get; set; } = true;
    public bool ShowColSavePath { get; set; } = false;
    public bool ShowColSize { get; set; } = true;
    public bool ShowColProgress { get; set; } = true;
    public bool ShowColSpeed { get; set; } = true;
    public bool ShowColEta { get; set; } = true;
    public bool ShowColStatus { get; set; } = true;
    public bool ShowColAddedDate { get; set; } = false;
    public bool ShowColCompletedDate { get; set; } = false;
    public bool ShowColChecksum { get; set; } = false;
    public bool ShowColActions { get; set; } = true;
}
