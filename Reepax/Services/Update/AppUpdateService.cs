using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Reepax.Services.Download;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;

namespace Reepax.Services.Update;

public class UpdateInfo
{
    public string CurrentVersion { get; set; } = AppUpdateService.AppCurrentVersion;
    public string NewVersion { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Changelog { get; set; } = string.Empty;
    public string ReleaseUrl { get; set; } = "https://github.com/Biiitz/Reepax/releases";
    public string FormattedDate { get; set; } = string.Empty;

    public string? SetupAssetUrl { get; set; }
    public long SetupAssetSize { get; set; }
    public string? PortableZipAssetUrl { get; set; }
    public long PortableZipAssetSize { get; set; }

    public bool HasDirectAsset => !string.IsNullOrEmpty(TargetDownloadUrl);

    public string? TargetDownloadUrl => SettingsService.IsPortableMode
        ? (PortableZipAssetUrl ?? SetupAssetUrl)
        : (SetupAssetUrl ?? PortableZipAssetUrl);

    public long TargetDownloadSize => SettingsService.IsPortableMode
        ? (PortableZipAssetSize > 0 ? PortableZipAssetSize : SetupAssetSize)
        : (SetupAssetSize > 0 ? SetupAssetSize : PortableZipAssetSize);

    public string TargetFileName => SettingsService.IsPortableMode ? "portable.zip" : "setup.exe";
}

public class AppUpdateService
{
    public const string AppCurrentVersion = "26.10.2";
    public const string GitHubApiLatestReleaseUrl = "https://api.github.com/repos/Biiitz/Reepax/releases/latest";

    private static readonly Lazy<AppUpdateService> _instance = new(() => new AppUpdateService());
    public static AppUpdateService Instance => _instance.Value;

    private readonly HttpClient _httpClient;
    private readonly HttpClient _downloadHttpClient;

    public static Action<string, bool>? ApplyUpdateActionOverride { get; set; }

    public AppUpdateService(HttpClient? httpClient = null, HttpClient? downloadHttpClient = null)
    {
        _httpClient = httpClient ?? HttpUserAgentService.CreateHttpClient(TimeSpan.FromSeconds(15), HttpContentType.Html);
        _downloadHttpClient = downloadHttpClient ?? HttpUserAgentService.CreateHttpClient(TimeSpan.FromMinutes(20), HttpContentType.BinaryOrAny);
    }

    /// Parses an application release version string into components: Year, Month, Patch.
    public static bool TryParseAppVersion(string? input, out int year, out int month, out int release, out int patch)
    {
        year = 0;
        month = 0;
        release = 0;
        patch = 0;

        if (string.IsNullOrWhiteSpace(input)) return false;

        var cleaned = input.Trim();
        var prefixMatch = Regex.Match(cleaned, @"^(?:reepax[_\-\s]*|release[_\-\s]*|v)+", RegexOptions.IgnoreCase);
        if (prefixMatch.Success)
        {
            cleaned = cleaned.Substring(prefixMatch.Length).Trim();
        }
        cleaned = cleaned.TrimStart('v', 'V');

        // Pattern 1: Day.MonthYear legacy (e.g. 9.926, 10.926, 15.926, 1.1026)
        var mLegacyShort = Regex.Match(cleaned, @"^(\d{1,2})\.(\d{1,2})(\d{2})(?:\.(\d+))?$");
        if (mLegacyShort.Success)
        {
            if (int.TryParse(mLegacyShort.Groups[1].Value, out int day) &&
                int.TryParse(mLegacyShort.Groups[2].Value, out int m) &&
                int.TryParse(mLegacyShort.Groups[3].Value, out int yrShort))
            {
                if (m < 1 || m > 12 || day < 1 || day > 31)
                    return false;

                year = 2000 + yrShort;
                month = m;
                release = day;
                if (mLegacyShort.Groups[4].Success) int.TryParse(mLegacyShort.Groups[4].Value, out patch);
                return true;
            }
        }

        // Pattern 2: Primary Scheme YY.M.Release[.Patch] or YYYY.M.Release[.Patch]
        var mDot = Regex.Match(cleaned, @"^(\d{1,4})\.(\d{1,2})\.(\d{1,4})(?:\.(\d+))?$");
        if (mDot.Success)
        {
            if (int.TryParse(mDot.Groups[1].Value, out int p1) &&
                int.TryParse(mDot.Groups[2].Value, out int p2) &&
                int.TryParse(mDot.Groups[3].Value, out int p3))
            {
                if (p2 < 1 || p2 > 12)
                    return false;

                if (mDot.Groups[4].Success) int.TryParse(mDot.Groups[4].Value, out patch);

                bool isP3Year = (p3 >= 20 && p3 <= 99) || p3 >= 2020;
                bool isP1Year = (p1 >= 25 && p1 <= 99) || p1 >= 2020;

                if (isP3Year && (!isP1Year || (p1 <= 31 && p3 == 26 && p1 != 26)))
                {
                    if (p1 < 1 || p1 > 31) return false;
                    year = p3 < 100 ? 2000 + p3 : p3;
                    month = p2;
                    release = p1;
                    return true;
                }
                else if (isP1Year)
                {
                    if (p3 < 0) return false;
                    year = p1 < 100 ? 2000 + p1 : p1;
                    month = p2;
                    release = p3;
                    return true;
                }
                else
                {
                    year = 2000 + Math.Min(p1, 99);
                    month = p2;
                    release = p3;
                    return true;
                }
            }
        }

        // Pattern 3: YYYY-MM-DD or YYYY.MM.DD
        var mIso = Regex.Match(cleaned, @"^(\d{4})[\.\-](\d{1,2})[\.\-](\d{1,2})(?:\.(\d+))?$");
        if (mIso.Success)
        {
            if (int.TryParse(mIso.Groups[1].Value, out int yr) &&
                int.TryParse(mIso.Groups[2].Value, out int m) &&
                int.TryParse(mIso.Groups[3].Value, out int d))
            {
                if (m < 1 || m > 12 || d < 1 || d > 31)
                    return false;

                year = yr;
                month = m;
                release = d;
                if (mIso.Groups[4].Success) int.TryParse(mIso.Groups[4].Value, out patch);
                return true;
            }
        }

        // Fallback: System.Version
        if (Version.TryParse(cleaned, out var v) && v.Build >= 0 && v.Minor >= 1 && v.Minor <= 12)
        {
            year = v.Major < 100 ? 2000 + v.Major : v.Major;
            month = v.Minor;
            release = Math.Max(0, v.Build);
            patch = Math.Max(0, v.Revision);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses a version string into a DateTime (Year, Month, Release/Day) and patch.
    /// </summary>
    public static bool TryParseVersionDate(string? input, out DateTime date, out int patch)
    {
        date = DateTime.MinValue;
        patch = 0;

        if (TryParseAppVersion(input, out int year, out int month, out int release, out int p))
        {
            try
            {
                int daysInMonth = DateTime.DaysInMonth(year, month);
                int day = Math.Clamp(release, 1, daysInMonth);
                date = new DateTime(year, month, day);
                patch = p;
                return true;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a candidate version is strictly newer than the current version.
    /// </summary>
    public static bool IsNewerVersion(string currentVersion, string candidateVersion)
    {
        if (string.IsNullOrWhiteSpace(candidateVersion)) return false;

        string c1 = currentVersion.Trim().TrimStart('v', 'V');
        string c2 = candidateVersion.Trim().TrimStart('v', 'V');

        if (string.Equals(c1, c2, StringComparison.OrdinalIgnoreCase))
            return false;

        if (TryParseAppVersion(c1, out int y1, out int m1, out int r1, out int p1) &&
            TryParseAppVersion(c2, out int y2, out int m2, out int r2, out int p2))
        {
            if (y2 != y1) return y2 > y1;
            if (m2 != m1) return m2 > m1;
            if (r2 != r1) return r2 > r1;
            return p2 > p1;
        }

        return false;
    }

    /// <summary>
    /// Checks GitHub Releases for a newer version.
    /// Returns UpdateInfo if an update is available, or null if up-to-date or on error.
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdatesAsync(string currentVersion = AppCurrentVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, GitHubApiLatestReleaseUrl);
            req.Headers.Accept.ParseAdd("application/vnd.github.v3+json");
            req.Headers.UserAgent.ParseAdd("Reepax-AppUpdateChecker");

            using var resp = await _httpClient.SendAsync(req, cancellationToken);
            string? json = null;

            if (resp.IsSuccessStatusCode)
            {
                json = await resp.Content.ReadAsStringAsync(cancellationToken);
            }
            else if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Fallback: Query release list if /releases/latest returns 404
                try
                {
                    using var fallbackReq = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/Biiitz/Reepax/releases?per_page=5");
                    fallbackReq.Headers.Accept.ParseAdd("application/vnd.github.v3+json");
                    fallbackReq.Headers.UserAgent.ParseAdd("Reepax-AppUpdateChecker");
                    using var fallbackResp = await _httpClient.SendAsync(fallbackReq, cancellationToken);
                    if (fallbackResp.IsSuccessStatusCode)
                    {
                        var listJson = await fallbackResp.Content.ReadAsStringAsync(cancellationToken);
                        using var listDoc = JsonDocument.Parse(listJson);
                        if (listDoc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var rel in listDoc.RootElement.EnumerateArray())
                            {
                                var isDraft = rel.TryGetProperty("draft", out var d) && d.GetBoolean();
                                if (!isDraft)
                                {
                                    json = rel.GetRawText();
                                    break;
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                AppLogger.Debug($"[AppUpdateService] No release found or query returned {resp.StatusCode}");
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagProp))
                return null;

            var tagName = tagProp.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(tagName))
                return null;

            if (!IsNewerVersion(currentVersion, tagName))
            {
                AppLogger.Debug($"[AppUpdateService] Current version '{currentVersion}' is up to date with latest release '{tagName}'.");
                return null;
            }

            var title = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? tagName : tagName;
            var body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? string.Empty : string.Empty;
            var releaseUrl = root.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() ?? "https://github.com/Biiitz/Reepax/releases" : "https://github.com/Biiitz/Reepax/releases";

            string formattedDate = string.Empty;
            if (root.TryGetProperty("published_at", out var pubProp) && pubProp.TryGetDateTime(out var dt))
            {
                formattedDate = dt.ToLocalTime().ToString("dd.MM.yyyy");
            }
            else if (TryParseVersionDate(tagName, out var dateFromTag, out _))
            {
                formattedDate = dateFromTag.ToString("dd.MM.yyyy");
            }

            string? setupUrl = null;
            long setupSize = 0;
            string? portableUrl = null;
            long portableSize = 0;

            if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsProp.EnumerateArray())
                {
                    var assetName = asset.TryGetProperty("name", out var np) ? np.GetString() : null;
                    var downloadUrl = asset.TryGetProperty("browser_download_url", out var dp) ? dp.GetString() : null;
                    var size = asset.TryGetProperty("size", out var sp) && sp.TryGetInt64(out var s) ? s : 0L;

                    if (string.IsNullOrWhiteSpace(assetName) || string.IsNullOrWhiteSpace(downloadUrl))
                        continue;

                    if (assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        if (assetName.IndexOf("setup", StringComparison.OrdinalIgnoreCase) >= 0 || setupUrl == null)
                        {
                            setupUrl = downloadUrl;
                            setupSize = size;
                        }
                    }
                    else if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        if (assetName.IndexOf("portable", StringComparison.OrdinalIgnoreCase) >= 0 || portableUrl == null)
                        {
                            portableUrl = downloadUrl;
                            portableSize = size;
                        }
                    }
                }
            }

            return new UpdateInfo
            {
                CurrentVersion = currentVersion,
                NewVersion = tagName.TrimStart('v', 'V'),
                Title = title,
                Changelog = body,
                ReleaseUrl = releaseUrl,
                FormattedDate = formattedDate,
                SetupAssetUrl = setupUrl,
                SetupAssetSize = setupSize,
                PortableZipAssetUrl = portableUrl,
                PortableZipAssetSize = portableSize
            };
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"[AppUpdateService] Error checking for updates: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Downloads the update payload file to a temporary location.
    /// </summary>
    public async Task<string> DownloadUpdateAsync(
        UpdateInfo updateInfo,
        IProgress<(long bytesDownloaded, long totalBytes)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var downloadUrl = updateInfo.TargetDownloadUrl;
        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new InvalidOperationException("No download URL available for update.");

        var updateDir = Path.Combine(Path.GetTempPath(), "Reepax", "Update");
        if (Directory.Exists(updateDir))
        {
            try { Directory.Delete(updateDir, recursive: true); } catch { }
        }
        Directory.CreateDirectory(updateDir);

        var targetPath = Path.Combine(updateDir, updateInfo.TargetFileName);

        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        request.Headers.UserAgent.ParseAdd("Reepax-AppUpdater");

        using var response = await _downloadHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? updateInfo.TargetDownloadSize;

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
            totalRead += bytesRead;
            progress?.Report((totalRead, totalBytes));
        }

        return targetPath;
    }

    /// <summary>
    /// Extracts the downloaded portable zip archive, preserving user data.
    /// </summary>
    public async Task<string> PreparePortableUpdateAsync(string zipFilePath, CancellationToken cancellationToken = default)
    {
        var baseDir = Path.GetDirectoryName(zipFilePath)!;
        var extractDir = Path.Combine(baseDir, "extracted");
        if (Directory.Exists(extractDir))
        {
            try { Directory.Delete(extractDir, recursive: true); } catch { }
        }
        Directory.CreateDirectory(extractDir);

        await Task.Run(() =>
        {
            System.IO.Compression.ZipFile.ExtractToDirectory(zipFilePath, extractDir, overwriteFiles: true);

            var extractedDataDir = Path.Combine(extractDir, "Data");
            if (Directory.Exists(extractedDataDir))
            {
                try { Directory.Delete(extractedDataDir, recursive: true); } catch { }
            }
        }, cancellationToken);

        return extractDir;
    }

    /// <summary>
    /// Launches a completely hidden update script to replace files or run the installer silently,
    /// then terminates the current process safely.
    /// </summary>
    public static bool ApplyUpdate(string updatePath, bool isPortable)
    {
        if (DownloadPersistenceService.IsTestEnvironment && ApplyUpdateActionOverride != null)
        {
            ApplyUpdateActionOverride.Invoke(updatePath, isPortable);
            return true;
        }

        try
        {
            var targetExe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(targetExe) || !File.Exists(targetExe))
            {
                try { targetExe = Process.GetCurrentProcess().MainModule?.FileName; } catch { }
            }

            if (string.IsNullOrEmpty(targetExe) || !File.Exists(targetExe))
            {
                AppLogger.Error("[AppUpdateService] Target application executable could not be resolved.");
                return false;
            }

            int procId = Environment.ProcessId;
            var appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
            var tempDir = Path.Combine(Path.GetTempPath(), "Reepax", "Update");
            Directory.CreateDirectory(tempDir);
            var scriptPath = Path.Combine(tempDir, "apply_update.ps1");

            string scriptContent = isPortable
                ? GeneratePortableUpdateScript(procId, updatePath, appDir, targetExe)
                : GenerateInstallerUpdateScript(procId, updatePath, targetExe);

            File.WriteAllText(scriptPath, scriptContent, System.Text.Encoding.UTF8);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false
            };

            Process.Start(psi);

            PerformSafeShutdownAndExit();
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("[AppUpdateService] Failed to apply update", ex);
            return false;
        }
    }

    public static string GeneratePortableUpdateScript(int procId, string sourceDir, string targetDir, string targetExe)
    {
        static string Esc(string s) => s.Replace("'", "''");

        return $@"
$procId = {procId}
if ($procId -gt 0) {{
    try {{ Wait-Process -Id $procId -Timeout 30 -ErrorAction SilentlyContinue }} catch {{}}
}}
Start-Sleep -Milliseconds 600

$source = '{Esc(sourceDir)}'
$dest = '{Esc(targetDir)}'
$exe = '{Esc(targetExe)}'

$copied = $false
for ($i = 0; $i -lt 10; $i++) {{
    try {{
        Copy-Item -Path ""$source\*"" -Destination ""$dest"" -Recurse -Force -ErrorAction Stop
        $copied = $true
        break
    }} catch {{
        Start-Sleep -Milliseconds 800
    }}
}}

if (Test-Path ""$exe"") {{
    Start-Process -FilePath ""$exe"" -ArgumentList ""--restart""
}}

try {{
    Start-Sleep -Seconds 2
    Remove-Item -Path ""$source"" -Recurse -Force -ErrorAction SilentlyContinue
}} catch {{}}
";
    }

    public static string GenerateInstallerUpdateScript(int procId, string setupPath, string targetExe)
    {
        static string Esc(string s) => s.Replace("'", "''");

        return $@"
$procId = {procId}
if ($procId -gt 0) {{
    try {{ Wait-Process -Id $procId -Timeout 30 -ErrorAction SilentlyContinue }} catch {{}}
}}
Start-Sleep -Milliseconds 600

$setup = '{Esc(setupPath)}'
$exe = '{Esc(targetExe)}'

Start-Process -FilePath ""$setup"" -ArgumentList ""/VERYSILENT /SUPPRESSMSGBOXES /FORCECLOSEAPPLICATIONS"" -Wait

Start-Sleep -Milliseconds 1200
$running = Get-Process -Name ""Reepax"" -ErrorAction SilentlyContinue
if (-not $running -and (Test-Path ""$exe"")) {{
    Start-Process -FilePath ""$exe"" -ArgumentList ""--restart""
}}

try {{
    Start-Sleep -Seconds 3
    Remove-Item -Path ""$setup"" -Force -ErrorAction SilentlyContinue
}} catch {{}}
";
    }

    public static void PerformSafeShutdownAndExit()
    {
        try
        {
            var app = Application.Current;
            if (!DownloadPersistenceService.IsTestEnvironment &&
                app?.Dispatcher != null &&
                app.Dispatcher.Thread?.IsAlive == true &&
                !app.Dispatcher.HasShutdownStarted &&
                !app.Dispatcher.HasShutdownFinished)
            {
                if (app.Dispatcher.CheckAccess())
                {
                    if (app.MainWindow is MainWindow mw)
                    {
                        mw.PrepareForRestart();
                    }
                }
                else
                {
                    app.Dispatcher.Invoke(() =>
                    {
                        if (app.MainWindow is MainWindow mw)
                        {
                            mw.PrepareForRestart();
                        }
                    });
                }
            }
        }
        catch { }

        try
        {
            QueueManager.Instance.StopQueue(isExiting: true);
            SettingsService.Instance.SaveSettings();
        }
        catch { }

        try
        {
            TrayIconService.Instance.HideTrayIcon();
            TrayIconService.Instance.Dispose();
        }
        catch { }

        try
        {
            SingleInstanceService.Instance.Dispose();
        }
        catch { }

        if (DownloadPersistenceService.IsTestEnvironment)
            return;

        try
        {
            var app = Application.Current;
            app?.Dispatcher?.Invoke(() =>
            {
                if (app.MainWindow is MainWindow mw)
                {
                    mw.ForceExit();
                }
                app.Shutdown();
            });
        }
        catch { }

        Environment.Exit(0);
    }
}
