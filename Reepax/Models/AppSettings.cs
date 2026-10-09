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
    public bool MinimizeToTrayOnClose { get; set; } = false;
    public bool StartWithWindows { get; set; } = false;
    public bool EnableCompletionNotifications { get; set; } = false;
    public bool EnableCompletionSound { get; set; } = false;
    public CompletionSoundTrigger CompletionSoundTrigger { get; set; } = CompletionSoundTrigger.EntirePackage;
    public string SelectedCompletionSound { get; set; } = "1.mp3";

    private int _completionSoundVolume = 80;
    public int CompletionSoundVolume
    {
        get => _completionSoundVolume;
        set
        {
            _completionSoundVolume = value;
            _hasExplicitCompletionVolume = true;
        }
    }

    public bool EnableErrorSound { get; set; } = false;
    public string SelectedErrorSound { get; set; } = "1.mp3";

    private int _errorSoundVolume = 80;
    public int ErrorSoundVolume
    {
        get => _errorSoundVolume;
        set
        {
            _errorSoundVolume = value;
            _hasExplicitErrorVolume = true;
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    private bool _hasExplicitCompletionVolume;

    [System.Text.Json.Serialization.JsonIgnore]
    private bool _hasExplicitErrorVolume;

    [System.Text.Json.Serialization.JsonPropertyName("SoundVolume")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacySoundVolume
    {
        get => null;
        set
        {
            if (value.HasValue)
            {
                if (!_hasExplicitCompletionVolume)
                    _completionSoundVolume = value.Value;
                if (!_hasExplicitErrorVolume)
                    _errorSoundVolume = value.Value;
            }
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public int SoundVolume
    {
        get => CompletionSoundVolume;
        set
        {
            CompletionSoundVolume = value;
            ErrorSoundVolume = value;
        }
    }
    public bool AutoCollapseCompletedPackages { get; set; } = true;
    public bool EnableFileLogging { get; set; } = false;
    public bool EnableClipboardMonitor { get; set; } = false;
    public PostDownloadAction PostDownloadAction { get; set; } = PostDownloadAction.None;
    public DownloadStatusFilter SelectedStatusFilter { get; set; } = DownloadStatusFilter.All;
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
    public List<string> ExtractionPasswords { get; set; } = new();
    public bool IsArchivePasswordsExpanded { get; set; } = false;

    public static List<string> SanitizeExtractionPasswords(IEnumerable<string>? passwords)
    {
        if (passwords == null)
        {
            return new List<string>();
        }

        var list = new List<string>();
        foreach (var pwd in passwords)
        {
            if (string.IsNullOrWhiteSpace(pwd))
                continue;

            var trimmed = pwd.Trim();
            if (!list.Contains(trimmed, StringComparer.Ordinal))
            {
                list.Add(trimmed);
            }
        }

        return list;
    }

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
    public double ColWidthName { get; set; } = 220;
    public double ColWidthHoster { get; set; } = 75;
    public double ColWidthSavePath { get; set; } = 140;
    public double ColWidthSize { get; set; } = 75;
    public double ColWidthProgress { get; set; } = 110;
    public double ColWidthSpeed { get; set; } = 80;
    public double ColWidthEta { get; set; } = 65;
    public double ColWidthStatus { get; set; } = 95;
    public double ColWidthAddedDate { get; set; } = 95;
    public double ColWidthCompletedDate { get; set; } = 95;
    public double ColWidthChecksum { get; set; } = 90;
    public double ColWidthActions { get; set; } = 95;

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

    // TreeListView Column Order
    public static readonly string[] DefaultColumnOrder = new[]
    {
        "Name",
        "Hoster",
        "SavePath",
        "Size",
        "Progress",
        "Speed",
        "Eta",
        "Status",
        "AddedDate",
        "CompletedDate",
        "Checksum",
        "Actions"
    };

    public List<string> ColumnOrder { get; set; } = new(DefaultColumnOrder);

    public static List<string> SanitizeColumnOrder(IEnumerable<string>? order)
    {
        var list = new List<string>();
        if (order != null)
        {
            foreach (var col in order)
            {
                if (DefaultColumnOrder.Contains(col, StringComparer.OrdinalIgnoreCase) &&
                    !list.Contains(col, StringComparer.OrdinalIgnoreCase))
                {
                    // Use canonical casing from DefaultColumnOrder
                    string canonical = DefaultColumnOrder.First(c => string.Equals(c, col, StringComparison.OrdinalIgnoreCase));
                    list.Add(canonical);
                }
            }
        }
        foreach (var def in DefaultColumnOrder)
        {
            if (!list.Contains(def, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(def);
            }
        }
        return list;
    }

    public static readonly string[] ValidCompletionSounds = ["1.mp3", "2.mp3", "3.mp3", "4.mp3", "5.mp3", "6.mp3"];
    public static readonly string[] ValidErrorSounds = ["1.mp3", "2.mp3", "3.mp3"];

    public static void SanitizeSoundSettings(AppSettings? settings)
    {
        if (settings == null) return;

        // Completion sound normalization
        if (string.IsNullOrWhiteSpace(settings.SelectedCompletionSound))
        {
            settings.SelectedCompletionSound = "1.mp3";
        }
        else
        {
            var raw = settings.SelectedCompletionSound.Trim();
            if (!raw.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                raw += ".mp3";
            }
            if (ValidCompletionSounds.Contains(raw, StringComparer.OrdinalIgnoreCase))
            {
                settings.SelectedCompletionSound = ValidCompletionSounds.First(s => string.Equals(s, raw, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                settings.SelectedCompletionSound = "1.mp3";
            }
        }

        // Error sound normalization
        if (string.IsNullOrWhiteSpace(settings.SelectedErrorSound))
        {
            settings.SelectedErrorSound = "1.mp3";
        }
        else
        {
            var raw = settings.SelectedErrorSound.Trim();
            if (!raw.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                raw += ".mp3";
            }
            if (ValidErrorSounds.Contains(raw, StringComparer.OrdinalIgnoreCase))
            {
                settings.SelectedErrorSound = ValidErrorSounds.First(s => string.Equals(s, raw, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                settings.SelectedErrorSound = "1.mp3";
            }
        }

        // Volume clamping
        settings.CompletionSoundVolume = Math.Clamp(settings.CompletionSoundVolume, 0, 100);
        settings.ErrorSoundVolume = Math.Clamp(settings.ErrorSoundVolume, 0, 100);
    }
}

public enum PostDownloadAction
{
    None = 0,
    Shutdown = 1,
    Sleep = 2,
    ExitApp = 3
}

public enum CompletionSoundTrigger
{
    EntirePackage = 0,
    IndividualFiles = 1
}
