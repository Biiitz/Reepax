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

    public string? ReleaseTag { get; set; }
    public string? SetupAssetUrl { get; set; }
    public long SetupAssetSize { get; set; }
    public string? PortableZipAssetUrl { get; set; }
    public long PortableZipAssetSize { get; set; }

    public string? Sha256ChecksumAssetUrl { get; set; }
    public Dictionary<string, string> ExpectedSha256Hashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? VerifiedSha256 { get; set; }

    public bool HasDirectAsset => !string.IsNullOrEmpty(TargetDownloadUrl);

    public string? TargetDownloadUrl => SettingsService.IsPortableMode
        ? PortableZipAssetUrl
        : SetupAssetUrl;

    public long TargetDownloadSize => SettingsService.IsPortableMode
        ? PortableZipAssetSize
        : SetupAssetSize;

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

public partial class AppUpdateService
{
    public static string AppCurrentVersion { get; } =
        typeof(AppUpdateService).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion.Split('+')[0];
    public const string GitHubApiLatestReleaseUrl = "https://api.github.com/repos/Biiitz/Reepax/releases/latest";

    private static readonly Lazy<AppUpdateService> _instance = new(() => new AppUpdateService());
    public static AppUpdateService Instance => _instance.Value;

    private readonly HttpClient _httpClient;
    private readonly HttpClient _downloadHttpClient;

    public static Action<string, bool>? ApplyUpdateActionOverride { get; set; }

    public AppUpdateService(HttpClient? httpClient = null, HttpClient? downloadHttpClient = null)
    {
        _httpClient = httpClient ?? CreateUpdateClient(TimeSpan.FromSeconds(15));
        _downloadHttpClient = downloadHttpClient ?? CreateUpdateClient(TimeSpan.FromMinutes(20));
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
    public async Task<UpdateInfo?> CheckForUpdatesAsync(string? currentVersion = null, CancellationToken cancellationToken = default)
    {
        currentVersion ??= AppCurrentVersion;
        LastCheckStatus = UpdateCheckStatus.Failed;
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
                                var isDraft = (rel.TryGetProperty("draft", out var d) && d.GetBoolean()) ||
                                    (rel.TryGetProperty("prerelease", out var pr) && pr.GetBoolean());
                                if (!isDraft)
                                {
                                    json = rel.GetRawText();
                                    break;
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                AppLogger.Debug($"[AppUpdateService] No release found or query returned {resp.StatusCode}");
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if ((root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) ||
                (root.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean()))
            {
                LastCheckStatus = UpdateCheckStatus.UpToDate;
                return null;
            }

            if (!root.TryGetProperty("tag_name", out var tagProp))
                return null;

            var tagName = tagProp.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(tagName))
                return null;

            if (!IsNewerVersion(currentVersion, tagName))
            {
                LastCheckStatus = UpdateCheckStatus.UpToDate;
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

            var expectedHashes = ParseSha256Checksums(body);
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

                    if (!IsReleaseAssetUrl(downloadUrl, tagName, assetName)) continue;
                    if (assetName is "setup.exe" or "portable.zip" && asset.TryGetProperty("digest", out var digestProperty) &&
                        digestProperty.ValueKind == JsonValueKind.String)
                    {
                        string digest = digestProperty.GetString()!;
                        if (!Regex.IsMatch(digest, "\\Asha256:[a-fA-F0-9]{64}\\z"))
                            throw new InvalidDataException("Invalid published asset digest.");
                        AddChecksum(expectedHashes, assetName, digest[7..]);
                    }
                    if (assetName.Equals("setup.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        if (setupUrl != null) throw new InvalidDataException("Duplicate update asset.");
                        setupUrl = downloadUrl;
                        setupSize = size;
                    }
                    else if (assetName.Equals("portable.zip", StringComparison.OrdinalIgnoreCase))
                    {
                        if (portableUrl != null) throw new InvalidDataException("Duplicate update asset.");
                        portableUrl = downloadUrl;
                        portableSize = size;
                    }
                    else if (assetName.Equals("SHA256SUMS.txt", StringComparison.Ordinal))
                    {
                        checksumUrl = downloadUrl;
                    }
                }
            }

            LastCheckStatus = UpdateCheckStatus.Available;
            var info = new UpdateInfo
            {
                CurrentVersion = currentVersion,
                NewVersion = tagName.TrimStart('v', 'V'),
                Title = title,
                Changelog = body,
                ReleaseUrl = releaseUrl,
                FormattedDate = formattedDate,
                ReleaseTag = tagName,
                SetupAssetUrl = setupUrl,
                SetupAssetSize = setupSize,
                PortableZipAssetUrl = portableUrl,
                PortableZipAssetSize = portableSize,
                Sha256ChecksumAssetUrl = checksumUrl,
                ExpectedSha256Hashes = expectedHashes
            };
            if (!info.HasDirectAsset || info.TargetDownloadSize <= 0 || checksumUrl == null)
                LastCheckStatus = UpdateCheckStatus.Incomplete;
            return info;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLogger.Debug($"[AppUpdateService] Error checking for updates: {ex.Message}");
            return null;
        }
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
                AddChecksum(result, file, hash);
                continue;
            }

            // Format 2: filename: <hash> or filename = <hash> or filename: `<hash>`
            var m2 = Regex.Match(trimmed, @"(?:^|[-*]\s*)([a-zA-Z0-9_\-\.]+\.(?:exe|zip))\s*[:=]\s*[`""]?([a-fA-F0-9]{64})[`""]?", RegexOptions.IgnoreCase);
            if (m2.Success)
            {
                var file = Path.GetFileName(m2.Groups[1].Value.Trim());
                var hash = m2.Groups[2].Value.ToLowerInvariant();
                AddChecksum(result, file, hash);
                continue;
            }

            // Format 3: SHA256 (filename) = <hash>
            var m3 = Regex.Match(trimmed, @"SHA256\s*\(\s*([a-zA-Z0-9_\-\.]+\.(?:exe|zip))\s*\)\s*=\s*([a-fA-F0-9]{64})", RegexOptions.IgnoreCase);
            if (m3.Success)
            {
                var file = Path.GetFileName(m3.Groups[1].Value.Trim());
                var hash = m3.Groups[2].Value.ToLowerInvariant();
                AddChecksum(result, file, hash);
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
            throw new IOException("Cannot protect the update directory.", ex);
        }
    }

    public class AuthenticodeResult
    {
        public bool IsSigned { get; set; }
        public bool IsValid { get; set; }
        public bool IsUnsigned { get; set; }
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
                    result.IsUnsigned = !result.IsSigned;
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
