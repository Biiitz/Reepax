using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Helpers;
using Reepax.Models;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;

namespace Reepax.Services.Extractor;

public class LinkMetadataResolverService
{
    private static readonly Lazy<LinkMetadataResolverService> _instance = new(() => new LinkMetadataResolverService());
    public static LinkMetadataResolverService Instance => _instance.Value;

    private readonly HttpClient _httpClient;

    private static readonly Regex SizeRegex = new(
        @"(?i)(?:file\s*size|size|größe|download\s*size|datei|taille|tamanho|poids|dimension|storage)[\s:>=<""'\/a-z\(\)]*?([0-9]+(?:[\.,][0-9]+)?\s*(?:TB|GB|MB|KB|Bytes|B|GiB|MiB|KiB|TiB))\b",
        RegexOptions.Compiled);

    private static readonly Regex BracketSizeRegex = new(
        @"\(\s*([0-9]+(?:[\.,][0-9]+)?\s*(?:TB|GB|MB|KB|GiB|MiB|KiB|TiB))\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ElementSizeRegex = new(
        @"<(?:span|b|strong|td|div|p|a|button)[^>]*class=[""'][^""']*(?:size|filesize|download)[^""']*[""'][^>]*>[\s\r\n]*([0-9]+(?:[\.,][0-9]+)?\s*(?:TB|GB|MB|KB|GiB|MiB|KiB|TiB))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BareSizeRegex = new(
        @"\b([0-9]+(?:\.[0-9]+)?)\s*(GB|MB|KB|Bytes|B|GiB|MiB|TB|TiB)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex JsonSizeRegex = new(
        @"""(?:size|file_size|bytes|filesize)""\s*:\s*""?(\d+)""?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DataSizeRegex = new(
        @"data-(?:file-)?size\s*=\s*[""']?(\d+)[""']?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HtmlFileNameRegex = new(
        @"(?:[\s""'=/\\:>]|^)([a-zA-Z0-9_\-\. \(\)\[\]]{3,140}\.(?:rar|zip|7z|bin|exe|iso|tar|gz|mkv|mp4|001|r\d{2}))(?=[\s""'<>]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex OgTitleRegex = new(
        @"<meta\s+(?:property|name)=[""'](?:og:title|twitter:title)[""']\s+content=[""']([^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PageTitleRegex = new(
        @"<title\b[^>]*>([^<]+)</title>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HeadingFileNameRegex = new(
        @"<(?:h[1-4]|span|div|p|td|a)\b[^>]*?(?:class|id)=[""'][^""']*(?:title|file|name|download)[^""']*[""'][^>]*>([^<]+)</(?:h[1-4]|span|div|p|td|a)>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public LinkMetadataResolverService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? HttpUserAgentService.CreateHttpClient(TimeSpan.FromSeconds(15), HttpContentType.BinaryOrAny);

        HttpUserAgentService.UserAgentChanged += _ =>
        {
            HttpUserAgentService.ApplyDefaultBrowserHeaders(_httpClient.DefaultRequestHeaders, null, HttpContentType.BinaryOrAny);
        };
    }

    private static void SafeInvoke(Action action)
    {
        if (System.Windows.Application.Current?.Dispatcher != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            System.Windows.Application.Current.Dispatcher.Invoke(action);
        }
        else
        {
            action();
        }
    }

    public async Task<bool> ResolveItemMetadataAsync(DownloadItem item, CancellationToken cancellationToken = default, bool allowFastHostResolve = true)
    {
        if (item == null)
            return false;

        // If it's a FastHost page and doesn't have a direct download URL yet, resolve it!
        // (only when host resolution is enabled for this package)
        if (allowFastHostResolve && FastHostResolver.IsFastHostUrl(item.OriginalUrl) && (string.IsNullOrWhiteSpace(item.DirectDownloadUrl) || !FastHostResolver.IsDirectDownloadUrl(item.DirectDownloadUrl)))
        {
            var directUrl = await FastHostResolver.ResolveDirectUrlAsync(_httpClient, item.OriginalUrl, cancellationToken);
            if (!string.IsNullOrWhiteSpace(directUrl))
            {
                SafeInvoke(() =>
                {
                    // OriginalUrl remains the page URL (for re-resolving expired links)
                    item.DirectDownloadUrl = directUrl;
                });
            }
        }

        if (item.TotalBytes > 0 && !string.IsNullOrWhiteSpace(item.DirectDownloadUrl))
            return true; // Size & Direct URL already known

        var targetUrl = !string.IsNullOrWhiteSpace(item.DirectDownloadUrl) ? item.DirectDownloadUrl : item.OriginalUrl;
        if (string.IsNullOrWhiteSpace(targetUrl) || !Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri))
            return false;

        // Apply slight jitter to prevent server rate limiting
        await HttpJitterHelper.DelayJitterAsync(50, 150, cancellationToken);

        try
        {
            // Method 1: Fast HTTP HEAD Request
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, uri);
            if (!string.IsNullOrWhiteSpace(item.Cookies)) headRequest.Headers.TryAddWithoutValidation("Cookie", item.Cookies);
            if (!string.IsNullOrWhiteSpace(item.UserAgent)) headRequest.Headers.TryAddWithoutValidation("User-Agent", item.UserAgent);
            if (!string.IsNullOrWhiteSpace(item.Referer)) headRequest.Headers.TryAddWithoutValidation("Referer", item.Referer);

            using var headResponse = await _httpClient.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (headResponse.IsSuccessStatusCode)
            {
                var mediaType = headResponse.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
                bool isHtml = mediaType != null && (mediaType.Contains("html") || mediaType.Contains("text"));

                if (!isHtml && ApplyResponseHeaders(item, headResponse))
                {
                    MarkAsDirectDownloadIfFileResponse(item, targetUrl, headResponse);
                    return true;
                }
            }

            // Method 2: HTTP GET with Range: bytes=0-0 (for servers where HEAD is blocked or lacks Content-Length)
            using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, uri);
            rangeRequest.Headers.Range = new RangeHeaderValue(0, 0);
            if (!string.IsNullOrWhiteSpace(item.Cookies)) rangeRequest.Headers.TryAddWithoutValidation("Cookie", item.Cookies);
            if (!string.IsNullOrWhiteSpace(item.UserAgent)) rangeRequest.Headers.TryAddWithoutValidation("User-Agent", item.UserAgent);
            if (!string.IsNullOrWhiteSpace(item.Referer)) rangeRequest.Headers.TryAddWithoutValidation("Referer", item.Referer);

            using var rangeResponse = await _httpClient.SendAsync(rangeRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (rangeResponse.StatusCode == HttpStatusCode.PartialContent)
            {
                var contentRange = rangeResponse.Content.Headers.ContentRange;
                if (contentRange?.Length != null && contentRange.Length.Value > 0)
                {
                    SafeInvoke(() =>
                    {
                        item.TotalBytes = contentRange.Length.Value;
                    });
                    ApplyContentDispositionFileName(item, rangeResponse);
                    MarkAsDirectDownloadIfFileResponse(item, targetUrl, rangeResponse);
                    return true;
                }
            }
            else if (rangeResponse.IsSuccessStatusCode)
            {
                var mediaType = rangeResponse.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
                bool isHtml = mediaType != null && (mediaType.Contains("html") || mediaType.Contains("text"));

                if (!isHtml && ApplyResponseHeaders(item, rangeResponse))
                {
                    MarkAsDirectDownloadIfFileResponse(item, targetUrl, rangeResponse);
                    return true;
                }
            }

            // Method 3: Hoster / Web Page Parsing (for web download pages)
            var resolved = await ProbeHosterPageAsync(item, targetUrl, cancellationToken, allowFastHostResolve);
            if (resolved)
                return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[LinkMetadataResolver] Konnte Metadaten für '{item.FileName}' ({targetUrl}) nicht auflösen: {ex.Message}");
        }

        return item.TotalBytes > 0;
    }

    public async Task ResolvePackageMetadataAsync(
        DownloadPackage package, 
        IProgress<string>? progress = null, 
        CancellationToken cancellationToken = default)
    {
        if (package == null || package.Items.Count == 0)
            return;

        var itemsToResolve = package.Items.Where(i => 
            i.TotalBytes <= 0 || 
            PackageGrouper.IsGenericOrCrypticName(i.FileName) ||
            i.FileName.StartsWith("download_file", StringComparison.OrdinalIgnoreCase) ||
            i.FileName.Contains("Package (") ||
            (package.AutoResolveHostLinks && FastHostResolver.IsFastHostUrl(i.OriginalUrl) && string.IsNullOrWhiteSpace(i.DirectDownloadUrl))
        ).ToList();

        if (itemsToResolve.Count == 0)
        {
            TryUpdatePackageName(package);
            return;
        }

        int totalCount = itemsToResolve.Count;
        int completedCount = 0;

        using var semaphore = new SemaphoreSlim(3, 3); // Max 3 concurrent probe requests
        var tasks = itemsToResolve.Select(async item =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                await ResolveItemMetadataAsync(item, cancellationToken, package.AutoResolveHostLinks);
            }
            finally
            {
                try
                {
                    semaphore.Release();
                }
                catch (ObjectDisposedException) { }

                var currentCompleted = Interlocked.Increment(ref completedCount);
                progress?.Report($"Analysiere Dateigrößen ({currentCompleted}/{totalCount}) für '{package.Name}'…");
                package.RecalculateAggregates();
            }
        });

        await Task.WhenAll(tasks);
        TryUpdatePackageName(package);
        package.RecalculateAggregates();
    }

    /// <summary>
    /// Marks the item as a direct download (Scenario A) when the server responds with actual
    /// file content (non-HTML or Content-Disposition: attachment) instead of a web page.
    /// </summary>
    private static void MarkAsDirectDownloadIfFileResponse(DownloadItem item, string targetUrl, HttpResponseMessage response)
    {
        if (!string.IsNullOrWhiteSpace(item.DirectDownloadUrl))
            return;
        if (IsHosterWebPage(targetUrl) || FastHostResolver.IsFastHostUrl(targetUrl))
            return;

        var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        bool isHtml = mediaType != null && (mediaType.Contains("html") || mediaType.Contains("text"));
        bool isAttachment = string.Equals(response.Content.Headers.ContentDisposition?.DispositionType, "attachment", StringComparison.OrdinalIgnoreCase);

        if (isAttachment || !isHtml)
        {
            SafeInvoke(() => item.DirectDownloadUrl = item.OriginalUrl);
            AppLogger.Info($"[LinkResolver] Direkter Download erkannt für '{item.FileName}': {targetUrl}");
        }
    }

    private bool ApplyResponseHeaders(DownloadItem item, HttpResponseMessage response)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        if (mediaType != null && (mediaType.Contains("html") || mediaType.Contains("text")))
        {
            return false;
        }

        bool updated = false;

        // Content-Length
        if (response.Content.Headers.ContentLength != null && response.Content.Headers.ContentLength.Value > 0)
        {
            var len = response.Content.Headers.ContentLength.Value;
            SafeInvoke(() => item.TotalBytes = len);
            updated = true;
        }

        // Content-Disposition (clean file name)
        ApplyContentDispositionFileName(item, response);

        // Checksum from ETag or Content-MD5
        if (string.IsNullOrWhiteSpace(item.ExpectedChecksum))
        {
            if (response.Content.Headers.ContentMD5 != null && response.Content.Headers.ContentMD5.Length > 0)
            {
                var md5 = Convert.ToHexString(response.Content.Headers.ContentMD5).ToLowerInvariant();
                SafeInvoke(() => item.ExpectedChecksum = md5);
            }
            else if (response.Headers.ETag != null && !string.IsNullOrWhiteSpace(response.Headers.ETag.Tag))
            {
                var cleanTag = response.Headers.ETag.Tag.Trim('\"', 'W', '/', ' ');
                if (cleanTag.Length is 32 or 64 && Regex.IsMatch(cleanTag, "^[0-9a-fA-F]+$"))
                {
                    var tag = cleanTag.ToLowerInvariant();
                    SafeInvoke(() => item.ExpectedChecksum = tag);
                }
            }
        }

        return updated;
    }

    private static void ApplyContentDispositionFileName(DownloadItem item, HttpResponseMessage response)
    {
        var contentDisposition = response.Content.Headers.ContentDisposition;
        string? name = null;
        if (contentDisposition?.FileNameStar != null)
        {
            name = contentDisposition.FileNameStar.Trim('\"').Trim();
        }
        else if (contentDisposition?.FileName != null)
        {
            name = contentDisposition.FileName.Trim('\"').Trim();
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            SafeInvoke(() => item.Rename(name));
        }
    }

    private static bool IsHosterWebPage(string url)
    {
        var lower = url.ToLowerInvariant();
        return lower.Contains(FastHostResolver.CanonicalDomain) ||
               lower.Contains("1fichier.com") ||
               lower.Contains("mediafire.com") ||
               lower.Contains("megaup.net") ||
               lower.Contains("ddownload.com") ||
               lower.Contains("rapidgator.net") ||
               lower.Contains("gofile.io");
    }

    private async Task<bool> ProbeHosterPageAsync(DownloadItem item, string pageUrl, CancellationToken cancellationToken, bool allowDirectUrlExtraction = true)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, pageUrl);
            using var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseContentRead, cancellationToken);
            if (!res.IsSuccessStatusCode) return false;

            var html = await res.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(html)) return false;

            // Cloudflare challenge pages lack real metadata — skip parsing
            // to avoid storing corrupt file size estimates
            if (FastHostResolver.IsCloudflareChallenge(res.StatusCode, html))
            {
                AppLogger.Info($"[LinkResolver] Cloudflare protection on '{pageUrl}' — metadata will be determined via browser window later.");
                return false;
            }

            // Check if page contains direct download link (e.g. 1fichier, mediafire, gofile, direct host, etc.)
            var directUrl = allowDirectUrlExtraction ? FastHostResolver.ExtractDirectUrlFromHtml(html) : null;
            if (!string.IsNullOrWhiteSpace(directUrl))
            {
                SafeInvoke(() =>
                {
                    // OriginalUrl remains the page URL (for re-resolving expired links)
                    item.DirectDownloadUrl = directUrl;
                });
                AppLogger.Info($"[LinkResolver] Direct download URL resolved for '{item.FileName}': {directUrl}");

                // Probe direct download URL for exact file size and filename
                try
                {
                    using var directReq = new HttpRequestMessage(HttpMethod.Get, directUrl);
                    directReq.Headers.Range = new RangeHeaderValue(0, 0);
                    using var directRes = await _httpClient.SendAsync(directReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (directRes.StatusCode == HttpStatusCode.PartialContent)
                    {
                        var contentRange = directRes.Content.Headers.ContentRange;
                        if (contentRange?.Length != null && contentRange.Length.Value > 0)
                        {
                            var rangeLen = contentRange.Length.Value;
                            SafeInvoke(() => item.TotalBytes = rangeLen);
                            ApplyContentDispositionFileName(item, directRes);
                            return true;
                        }
                    }
                    else if (directRes.IsSuccessStatusCode)
                    {
                        if (ApplyResponseHeaders(item, directRes))
                        {
                            return true;
                        }
                    }
                }
                catch { }
            }

            // Extract filename from HTML if current item filename is generic/cryptic or placeholder
            var probedFileName = ExtractFileNameFromHtml(html, pageUrl);
            if (!string.IsNullOrWhiteSpace(probedFileName) &&
                (PackageGrouper.IsGenericOrCrypticName(item.FileName) || 
                 item.FileName.StartsWith("download_file", StringComparison.OrdinalIgnoreCase) ||
                 item.FileName.Contains(".part") ||
                 item.FileName.Contains("Package (")))
            {
                SafeInvoke(() => item.Rename(probedFileName));
            }

            // 1. Check data-size / json-size in HTML
            var jsonMatch = JsonSizeRegex.Match(html);
            if (jsonMatch.Success && long.TryParse(jsonMatch.Groups[1].Value, out long bytesFromJson) && bytesFromJson > 0)
            {
                SafeInvoke(() => item.TotalBytes = bytesFromJson);
                return true;
            }

            var dataSizeMatch = DataSizeRegex.Match(html);
            if (dataSizeMatch.Success && long.TryParse(dataSizeMatch.Groups[1].Value, out long bytesFromData) && bytesFromData > 0)
            {
                SafeInvoke(() => item.TotalBytes = bytesFromData);
                return true;
            }

            // 2. Check Size Text in HTML
            var sizeMatch = SizeRegex.Match(html);
            if (sizeMatch.Success)
            {
                var parsedBytes = ParseSizeToBytes(sizeMatch.Groups[1].Value);
                if (parsedBytes > 0)
                {
                    SafeInvoke(() => { if (item.TotalBytes <= 0) item.TotalBytes = parsedBytes; });
                    return true;
                }
            }

            // 3. Element size class regex
            var elemMatch = ElementSizeRegex.Match(html);
            if (elemMatch.Success)
            {
                var parsedBytes = ParseSizeToBytes(elemMatch.Groups[1].Value);
                if (parsedBytes > 0)
                {
                    SafeInvoke(() => { if (item.TotalBytes <= 0) item.TotalBytes = parsedBytes; });
                    return true;
                }
            }

            // 4. Bracket size match (e.g. "(1.45 GB)")
            var bracketMatch = BracketSizeRegex.Match(html);
            if (bracketMatch.Success)
            {
                var parsedBytes = ParseSizeToBytes(bracketMatch.Groups[1].Value);
                if (parsedBytes > 0)
                {
                    SafeInvoke(() => { if (item.TotalBytes <= 0) item.TotalBytes = parsedBytes; });
                    return true;
                }
            }

            // 5. Bare size match fallback
            var bareMatch = BareSizeRegex.Match(html);
            if (bareMatch.Success)
            {
                var parsedBytes = ParseSizeToBytes(bareMatch.Value);
                if (parsedBytes > 0)
                {
                    SafeInvoke(() => { if (item.TotalBytes <= 0) item.TotalBytes = parsedBytes; });
                    return true;
                }
            }

            return item.TotalBytes > 0 || !string.IsNullOrWhiteSpace(probedFileName);
        }
        catch { }

        return false;
    }

    public static long ParseSizeToBytes(string? sizeText)
    {
        if (string.IsNullOrWhiteSpace(sizeText))
            return 0;

        var clean = sizeText.Trim();
        // Normalize thousands separators: "1,234.56 MB" or "1.234,56 MB"
        if (Regex.IsMatch(clean, @"^\d{1,3}(,\d{3})+(\.\d+)?"))
        {
            clean = clean.Replace(",", "");
        }
        else if (Regex.IsMatch(clean, @"^\d{1,3}(\.\d{3})+(,\d+)?"))
        {
            clean = clean.Replace(".", "").Replace(",", ".");
        }
        else
        {
            clean = clean.Replace(",", ".");
        }

        var match = Regex.Match(clean, @"(?i)^([0-9]+(?:\.[0-9]+)?)\s*([a-z]+)");
        if (!match.Success)
            return 0;

        if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || value <= 0)
            return 0;

        var unit = match.Groups[2].Value.ToUpperInvariant();

        return unit switch
        {
            "GB" or "GIB" or "G" => (long)(value * 1024L * 1024 * 1024),
            "MB" or "MIB" or "M" => (long)(value * 1024L * 1024),
            "KB" or "KIB" or "K" => (long)(value * 1024L),
            "B" or "BYTES" or "BYTE" => (long)value,
            "TB" or "TIB" or "T" => (long)(value * 1024L * 1024 * 1024 * 1024),
            _ => (long)value
        };
    }

    public static string? ExtractFileNameFromHtml(string html, string pageUrl)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var candidates = new List<string>();

        // 1. og:title / twitter:title
        var ogMatch = OgTitleRegex.Match(html);
        if (ogMatch.Success) candidates.Add(ogMatch.Groups[1].Value);

        // 2. <title>
        var titleMatch = PageTitleRegex.Match(html);
        if (titleMatch.Success) candidates.Add(titleMatch.Groups[1].Value);

        // 3. Heading / element title
        foreach (Match m in HeadingFileNameRegex.Matches(html))
        {
            if (m.Success) candidates.Add(m.Groups[1].Value);
        }

        foreach (var rawCandidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(rawCandidate)) continue;

            var clean = WebUtility.HtmlDecode(rawCandidate).Trim();

            // Strip common website prefix wrappers (e.g. "Download file ...", "Download ...", "Filecrypt - ...")
            clean = Regex.Replace(clean, @"(?i)^(?:download(?:\s+file)?|filecrypt|rapidgator|ddownload|1fichier)[\s\-:]+", "").Trim();

            // If clean candidate directly ends in an archive extension
            if (Regex.IsMatch(clean, @"(?i)\.(?:rar|zip|7z|bin|exe|iso|tar|gz|001|r\d{2})$"))
            {
                var sanitized = PackageGrouper.MakeSafeFileName(clean);
                if (!PackageGrouper.IsGenericOrCrypticName(sanitized))
                {
                    return sanitized;
                }
            }

            // Check if candidate contains a filename matching regex
            var fileMatch = HtmlFileNameRegex.Match(clean);
            if (fileMatch.Success)
            {
                var candidateName = fileMatch.Groups[1].Value.Trim();
                var sanitized = PackageGrouper.MakeSafeFileName(candidateName);
                if (!PackageGrouper.IsGenericOrCrypticName(sanitized))
                {
                    return sanitized;
                }
            }

            // Or if it's an update name even without extension
            if (UpdateDetector.IsUpdate(clean))
            {
                var sanitized = PackageGrouper.MakeSafeFileName(clean + ".rar");
                if (!PackageGrouper.IsGenericOrCrypticName(sanitized))
                {
                    return sanitized;
                }
            }
        }

        // 4. Check entire HTML for archive filename if page is small (< 256 KB)
        if (html.Length < 256 * 1024)
        {
            var match = HtmlFileNameRegex.Match(html);
            if (match.Success)
            {
                var candidateName = match.Groups[1].Value.Trim();
                var sanitized = PackageGrouper.MakeSafeFileName(candidateName);
                if (!PackageGrouper.IsGenericOrCrypticName(sanitized))
                {
                    return sanitized;
                }
            }
        }

        return null;
    }

    public static void TryUpdatePackageName(DownloadPackage package)
    {
        if (package == null)
            return;

        var itemsSnapshot = package.Items.ToArray();
        if (itemsSnapshot.Length == 0)
            return;

        // 1. Check all items for a valid game update name
        // Prefer item FileName over OriginalUrl/DirectDownloadUrl
        string? bestUpdatePkgName = null;

        // First pass: check FileNames of all items
        foreach (var item in itemsSnapshot)
        {
            if (!string.IsNullOrWhiteSpace(item.FileName) && UpdateDetector.IsUpdate(item.FileName))
            {
                var pkgName = UpdateDetector.GetUpdatePackageName(item.FileName);
                if (!string.IsNullOrWhiteSpace(pkgName) && pkgName != "Game - Updates")
                {
                    bestUpdatePkgName = pkgName;
                    break;
                }
            }
        }

        // Second pass: check URLs if no filename produced a meaningful game name
        if (string.IsNullOrWhiteSpace(bestUpdatePkgName))
        {
            foreach (var item in itemsSnapshot)
            {
                var url = !string.IsNullOrWhiteSpace(item.OriginalUrl) ? item.OriginalUrl : item.DirectDownloadUrl;
                if (!string.IsNullOrWhiteSpace(url) && UpdateDetector.IsUpdate(url))
                {
                    var pkgName = UpdateDetector.GetUpdatePackageName(url);
                    if (!string.IsNullOrWhiteSpace(pkgName) && pkgName != "Game - Updates")
                    {
                        bestUpdatePkgName = pkgName;
                        break;
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(bestUpdatePkgName) && bestUpdatePkgName != "Game - Updates")
        {
            if (!string.Equals(package.Name, bestUpdatePkgName, StringComparison.OrdinalIgnoreCase))
            {
                SafeInvoke(() =>
                {
                    package.Rename(bestUpdatePkgName);
                    package.AutoResolveHostLinks = false;
                });
                Services.Storage.DownloadPersistenceService.Instance.RequestSave();
            }
            return;
        }

        // 2. If package has a generic/cryptic/fallback name (e.g., "Filecrypt Package (2 files)", "Rapidgator Package (1 files)", "Download_Paket...", or token directory):
        var currentDirName = Path.GetFileName(package.SaveDirectory?.TrimEnd('\\', '/'));
        bool isPkgNameGeneric = PackageGrouper.IsGenericOrCrypticName(package.Name) ||
            package.Name.Contains("Package (") ||
            package.Name.Contains("Paket (") ||
            package.Name.StartsWith("Download_Paket", StringComparison.OrdinalIgnoreCase) ||
            package.Name.StartsWith("Download_Package", StringComparison.OrdinalIgnoreCase);

        bool isDirNameGeneric = !string.IsNullOrWhiteSpace(currentDirName) && 
            (PackageGrouper.IsGenericOrCrypticName(currentDirName) || LinkExtractor.IsPureNumericOrHash(currentDirName));

        if (isPkgNameGeneric || isDirNameGeneric)
        {
            var cleanCandidates = itemsSnapshot
                .Select(i => PackageGrouper.CleanPackageBaseName(i.FileName, i.OriginalUrl, HosterInfo.DetectHoster(i.OriginalUrl), null))
                .Where(name => !string.IsNullOrWhiteSpace(name) && !PackageGrouper.IsGenericOrCrypticName(name))
                .ToList();

            if (cleanCandidates.Count > 0)
            {
                var bestName = cleanCandidates
                    .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Count())
                    .First().Key;

                if (!string.IsNullOrWhiteSpace(bestName))
                {
                    if (!string.Equals(package.Name, bestName, StringComparison.OrdinalIgnoreCase) || isDirNameGeneric)
                    {
                        SafeInvoke(() => package.Rename(bestName));
                        Services.Storage.DownloadPersistenceService.Instance.RequestSave();
                    }
                }
            }
            else if (isDirNameGeneric && !isPkgNameGeneric)
            {
                // Directory name was a cryptic token, but package name is already clean!
                SafeInvoke(() => package.Rename(package.Name));
                Services.Storage.DownloadPersistenceService.Instance.RequestSave();
            }
        }
    }
}
