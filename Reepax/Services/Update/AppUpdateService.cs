using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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

    public string? Sha256ChecksumAssetUrl { get; set; }
    public Dictionary<string, string> ExpectedSha256Hashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? VerifiedSha256 { get; set; }

    public bool HasDirectAsset => !string.IsNullOrEmpty(TargetDownloadUrl);

    public string? TargetDownloadUrl => SettingsService.IsPortableMode
        ? (PortableZipAssetUrl ?? SetupAssetUrl)
        : (SetupAssetUrl ?? PortableZipAssetUrl);

    public long TargetDownloadSize => SettingsService.IsPortableMode
        ? (PortableZipAssetSize > 0 ? PortableZipAssetSize : SetupAssetSize)
        : (SetupAssetSize > 0 ? SetupAssetSize : PortableZipAssetSize);

    public string TargetFileName => SettingsService.IsPortableMode ? "portable.zip" : "setup.exe";

    public string? GetExpectedSha256(string fileName)
    {
        if (ExpectedSha256Hashes.TryGetValue(fileName, out var hash))
            return hash;
        return null;
    }

    public void SetExpectedSha256(string fileName, string hash)
    {
        ExpectedSha256Hashes[fileName] = hash.Trim().ToLowerInvariant();
    }
}

public class AppUpdateService
{
    public const string AppCurrentVersion = "26.10.4";
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
            string? checksumUrl = null;

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
                    else if (assetName.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase) ||
                             assetName.Equals("sha256sums.txt", StringComparison.OrdinalIgnoreCase) ||
                             assetName.Equals("checksums.txt", StringComparison.OrdinalIgnoreCase) ||
                             assetName.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase) ||
                             assetName.Equals("hashes.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        checksumUrl = downloadUrl;
                    }
                }
            }

            var expectedHashes = ParseSha256Checksums(body);

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
                PortableZipAssetSize = portableSize,
                Sha256ChecksumAssetUrl = checksumUrl,
                ExpectedSha256Hashes = expectedHashes
            };
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"[AppUpdateService] Error checking for updates: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Downloads the update payload file to a temporary location and rigorously verifies its size,
    /// SHA-256 checksum against published metadata, and digital signature.
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
        RestrictDirectoryAccessToCurrentUser(updateDir);

        var targetPath = Path.Combine(updateDir, updateInfo.TargetFileName);

        // Fetch SHA256 checksum file if asset URL is available and hashes not yet parsed
        if (!string.IsNullOrWhiteSpace(updateInfo.Sha256ChecksumAssetUrl) && 
            !updateInfo.ExpectedSha256Hashes.ContainsKey(updateInfo.TargetFileName))
        {
            try
            {
                using var checksumReq = new HttpRequestMessage(HttpMethod.Get, updateInfo.Sha256ChecksumAssetUrl);
                checksumReq.Headers.UserAgent.ParseAdd("Reepax-AppUpdater");
                using var checksumResp = await _httpClient.SendAsync(checksumReq, cancellationToken);
                if (checksumResp.IsSuccessStatusCode)
                {
                    var checksumContent = await checksumResp.Content.ReadAsStringAsync(cancellationToken);
                    var parsed = ParseSha256Checksums(checksumContent);
                    foreach (var kvp in parsed)
                    {
                        updateInfo.ExpectedSha256Hashes[kvp.Key] = kvp.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[AppUpdateService] Could not fetch checksum asset: {ex.Message}");
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        request.Headers.UserAgent.ParseAdd("Reepax-AppUpdater");

        using var response = await _downloadHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? updateInfo.TargetDownloadSize;

        await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                totalRead += bytesRead;
                progress?.Report((totalRead, totalBytes));
            }
        }

        // --- Integrity & Security Verification ---
        var fi = new FileInfo(targetPath);
        if (!fi.Exists)
        {
            throw new FileNotFoundException("Downloaded update file does not exist on disk.", targetPath);
        }

        // 1. File Size Verification
        if (updateInfo.TargetDownloadSize > 0 && fi.Length != updateInfo.TargetDownloadSize)
        {
            try { File.Delete(targetPath); } catch { }
            throw new InvalidDataException(
                $"Update file size mismatch: Expected {updateInfo.TargetDownloadSize} bytes, but received {fi.Length} bytes. File is incomplete or corrupted.");
        }

        // 2. SHA-256 Checksum Verification
        string computedHash;
        using (var sha256 = SHA256.Create())
        using (var verifyStream = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var hashBytes = await sha256.ComputeHashAsync(verifyStream, cancellationToken);
            computedHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        updateInfo.VerifiedSha256 = computedHash;

        if (updateInfo.ExpectedSha256Hashes.TryGetValue(updateInfo.TargetFileName, out var expectedHash) &&
            !string.IsNullOrWhiteSpace(expectedHash))
        {
            if (!string.Equals(computedHash, expectedHash.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(targetPath); } catch { }
                throw new InvalidDataException(
                    $"Update integrity check failed! SHA-256 mismatch for '{updateInfo.TargetFileName}'. Expected: {expectedHash.Trim().ToLowerInvariant()}, Actual: {computedHash}. Update aborted for security reasons.");
            }
            AppLogger.Info($"[AppUpdateService] SHA-256 integrity check verified successfully for '{updateInfo.TargetFileName}' ({computedHash}).");
        }
        else
        {
            AppLogger.Warn($"[AppUpdateService] No published SHA-256 hash found for '{updateInfo.TargetFileName}'. Computed hash: {computedHash}. Size check passed.");
        }

        // 3. Authenticode Digital Signature Verification (for .exe on Windows)
        if (targetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && OperatingSystem.IsWindows())
        {
            var authResult = VerifyAuthenticodeSignature(targetPath);
            if (authResult.IsTampered)
            {
                try { File.Delete(targetPath); } catch { }
                throw new InvalidDataException(
                    $"Authenticode signature verification failed: Executable has been tampered with or corrupted ({authResult.StatusMessage}). Update aborted.");
            }
            if (authResult.IsSigned)
            {
                AppLogger.Info($"[AppUpdateService] Authenticode digital signature verified: Subject='{authResult.Subject}', Issuer='{authResult.Issuer}', Valid={authResult.IsValid}.");
            }
            else
            {
                AppLogger.Debug($"[AppUpdateService] Executable is not Authenticode-signed ({authResult.StatusMessage}). Relying on verified SHA-256 hash.");
            }
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

            var extractedExe = Path.Combine(extractDir, "Reepax.exe");
            if (!File.Exists(extractedExe))
            {
                throw new InvalidDataException("Extracted portable update archive does not contain Reepax.exe. Update payload is corrupt.");
            }
        }, cancellationToken);

        return extractDir;
    }

    /// <summary>
    /// Launches a completely hidden update script to replace files or run the installer silently,
    /// then terminates the current process safely.
    /// </summary>
    public static bool ApplyUpdate(string updatePath, bool isPortable, string? expectedSha256 = null)
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

            // Anti-TOCTOU: If an expected SHA-256 hash is supplied and updatePath is an installer file, re-verify hash
            if (!string.IsNullOrWhiteSpace(expectedSha256) && File.Exists(updatePath))
            {
                using var sha256 = SHA256.Create();
                using var fs = new FileStream(updatePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var hash = Convert.ToHexString(sha256.ComputeHash(fs)).ToLowerInvariant();
                if (!string.Equals(hash, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Error($"[AppUpdateService] Pre-launch integrity check failed: Expected {expectedSha256}, Actual {hash}. Aborting update.");
                    try { File.Delete(updatePath); } catch { }
                    return false;
                }
            }

            int procId = Environment.ProcessId;
            var appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
            var tempDir = Path.Combine(Path.GetTempPath(), "Reepax", "Update");
            Directory.CreateDirectory(tempDir);
            RestrictDirectoryAccessToCurrentUser(tempDir);
            var scriptPath = Path.Combine(tempDir, "apply_update.ps1");

            string scriptContent = isPortable
                ? GeneratePortableUpdateScript(procId, updatePath, appDir, targetExe)
                : GenerateInstallerUpdateScript(procId, updatePath, targetExe, expectedSha256);

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

    public static string GenerateInstallerUpdateScript(int procId, string setupPath, string targetExe, string? expectedSha256 = null)
    {
        static string Esc(string s) => s.Replace("'", "''");

        string integrityCheck = !string.IsNullOrWhiteSpace(expectedSha256)
            ? $@"
$expectedHash = '{Esc(expectedSha256.Trim().ToLowerInvariant())}'
if (Test-Path ""$setup"") {{
    $currentHash = (Get-FileHash -Path ""$setup"" -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($currentHash -ne $expectedHash) {{
        Remove-Item -Path ""$setup"" -Force -ErrorAction SilentlyContinue
        exit 1
    }}
}}
"
            : "";

        return $@"
$procId = {procId}
if ($procId -gt 0) {{
    try {{ Wait-Process -Id $procId -Timeout 30 -ErrorAction SilentlyContinue }} catch {{}}
}}
Start-Sleep -Milliseconds 600

$setup = '{Esc(setupPath)}'
$exe = '{Esc(targetExe)}'
{integrityCheck}
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

    public static Dictionary<string, string> ParseSha256Checksums(string? content)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(content)) return result;

        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("#") || trimmed.StartsWith("//")) continue;

            // Format 1: standard sha256sum: <hash>  <filename> or <hash> *<filename>
            var m1 = Regex.Match(trimmed, @"^([a-fA-F0-9]{64})\s+[*]?([^\s]+)$");
            if (m1.Success)
            {
                var hash = m1.Groups[1].Value.ToLowerInvariant();
                var file = Path.GetFileName(m1.Groups[2].Value.Trim());
                result[file] = hash;
                continue;
            }

            // Format 2: filename: <hash> or filename = <hash> or filename: `<hash>`
            var m2 = Regex.Match(trimmed, @"(?:^|[-*]\s*)([a-zA-Z0-9_\-\.]+\.(?:exe|zip))\s*[:=]\s*[`""]?([a-fA-F0-9]{64})[`""]?", RegexOptions.IgnoreCase);
            if (m2.Success)
            {
                var file = Path.GetFileName(m2.Groups[1].Value.Trim());
                var hash = m2.Groups[2].Value.ToLowerInvariant();
                result[file] = hash;
                continue;
            }

            // Format 3: SHA256 (filename) = <hash>
            var m3 = Regex.Match(trimmed, @"SHA256\s*\(\s*([a-zA-Z0-9_\-\.]+\.(?:exe|zip))\s*\)\s*=\s*([a-fA-F0-9]{64})", RegexOptions.IgnoreCase);
            if (m3.Success)
            {
                var file = Path.GetFileName(m3.Groups[1].Value.Trim());
                var hash = m3.Groups[2].Value.ToLowerInvariant();
                result[file] = hash;
            }
        }
        return result;
    }

    private static void RestrictDirectoryAccessToCurrentUser(string directoryPath)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var dInfo = new DirectoryInfo(directoryPath);
            var dSecurity = new System.Security.AccessControl.DirectorySecurity();
            var currentUser = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            if (currentUser != null)
            {
                dSecurity.SetOwner(currentUser);
                dSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                dSecurity.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    currentUser,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow));
                dInfo.SetAccessControl(dSecurity);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"[AppUpdateService] Could not set restricted ACLs on update folder: {ex.Message}");
        }
    }

    public class AuthenticodeResult
    {
        public bool IsSigned { get; set; }
        public bool IsValid { get; set; }
        public bool IsTampered { get; set; }
        public string? Subject { get; set; }
        public string? Issuer { get; set; }
        public int ErrorCode { get; set; }
        public string StatusMessage { get; set; } = string.Empty;
    }

    private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("{00AAC56B-CD44-11d0-8CC2-00C04FC295EE}");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_IGNORE = 0;
    private const uint WTD_SAFER_FLAG = 0x00000100;

    private const int ERROR_SUCCESS = 0;
    private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    private const int TRUST_E_BAD_DIGEST = unchecked((int)0x80096010);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(
        IntPtr hwnd,
        [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID,
        ref WINTRUST_DATA pWVTData);

    public static AuthenticodeResult VerifyAuthenticodeSignature(string filePath)
    {
        var result = new AuthenticodeResult { StatusMessage = "Not checked" };
        if (!OperatingSystem.IsWindows() || !File.Exists(filePath))
        {
            return result;
        }

#pragma warning disable SYSLIB0057
        try
        {
            using var cert = X509Certificate.CreateFromSignedFile(filePath);
            result.IsSigned = true;
            result.Subject = cert.Subject;
            result.Issuer = cert.Issuer;
        }
        catch (CryptographicException)
        {
            result.IsSigned = false;
        }
        catch
        {
            result.IsSigned = false;
        }
#pragma warning restore SYSLIB0057

        try
        {
            var wfi = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = filePath,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero
            };

            var wfiPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            try
            {
                Marshal.StructureToPtr(wfi, wfiPtr, false);

                var wtd = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    pPolicyCallbackData = IntPtr.Zero,
                    pSIPClientData = IntPtr.Zero,
                    dwUIChoice = WTD_UI_NONE,
                    fdwRevocationChecks = WTD_REVOKE_NONE,
                    dwUnionChoice = WTD_CHOICE_FILE,
                    pFile = wfiPtr,
                    dwStateAction = WTD_STATEACTION_IGNORE,
                    hWVTStateData = IntPtr.Zero,
                    pwszURLReference = null,
                    dwProvFlags = WTD_SAFER_FLAG,
                    dwUIContext = 0,
                    pSignatureSettings = IntPtr.Zero
                };

                int trustResult = WinVerifyTrust(new IntPtr(-1), WINTRUST_ACTION_GENERIC_VERIFY_V2, ref wtd);
                result.ErrorCode = trustResult;

                if (trustResult == ERROR_SUCCESS)
                {
                    result.IsValid = true;
                    result.StatusMessage = "Signature is valid and trusted";
                }
                else if (trustResult == TRUST_E_BAD_DIGEST)
                {
                    result.IsTampered = true;
                    result.StatusMessage = "Signature hash mismatch: Binary has been tampered with or corrupted!";
                }
                else if (trustResult == TRUST_E_NOSIGNATURE)
                {
                    result.IsSigned = false;
                    result.StatusMessage = "No digital signature present";
                }
                else
                {
                    result.StatusMessage = $"WinVerifyTrust status: 0x{trustResult:X8}";
                }
            }
            finally
            {
                Marshal.FreeHGlobal(wfiPtr);
            }
        }
        catch (Exception ex)
        {
            result.StatusMessage = $"WinVerifyTrust exception: {ex.Message}";
        }

        return result;
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
