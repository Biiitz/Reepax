using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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
}

public class AppUpdateService
{
    public const string AppCurrentVersion = "26.9.1";
    public const string GitHubApiLatestReleaseUrl = "https://api.github.com/repos/Biiitz/Reepax/releases/latest";

    private static readonly Lazy<AppUpdateService> _instance = new(() => new AppUpdateService());
    public static AppUpdateService Instance => _instance.Value;

    private readonly HttpClient _httpClient;

    public AppUpdateService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? HttpUserAgentService.CreateHttpClient(TimeSpan.FromSeconds(15), HttpContentType.Html);
    }

    /// <summary>
    /// Parses an application release version string into components: Year, Month, Release, Patch.
    /// Supports the primary YY.M.Release format (e.g. "26.9.1", "26.9.2", "26.10.1")
    /// as well as legacy date formats ("24.9.26", "9.926", "2026.09.10").
    /// </summary>
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

        // Pattern 2: Primary Scheme YY.M.Release[.Patch] or YYYY.M.Release[.Patch] (e.g. 26.9.1, 26.9.2, 26.10.1, 2026.9.1)
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

            return new UpdateInfo
            {
                CurrentVersion = currentVersion,
                NewVersion = tagName.TrimStart('v', 'V'),
                Title = title,
                Changelog = body,
                ReleaseUrl = releaseUrl,
                FormattedDate = formattedDate
            };
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"[AppUpdateService] Error checking for updates: {ex.Message}");
            return null;
        }
    }
}
