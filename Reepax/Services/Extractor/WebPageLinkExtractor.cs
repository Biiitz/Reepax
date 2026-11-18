using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Helpers;
using Reepax.Models;
using Reepax.Services.Localization;
using Reepax.Services.SystemIntegration;

namespace Reepax.Services.Extractor;

public readonly record struct WebPageExtractionProgress(string Key, params object[] Arguments)
{
    internal string GetLocalizedText() => Loc.Format(Key, Arguments);
}

public class WebPageExtractionResult
{
    public string PageTitle { get; set; } = "Download Package";
    public string? TotalSizeText { get; set; }
    public long? TotalSizeBytes { get; set; }
    public List<ExtractedLink> Links { get; } = new();
    internal ExtractedLink? DirectDownload { get; init; }
    internal string? DirectDownloadChecksum { get; init; }
}

/// <summary>
/// Fetches a web page and extracts download links
/// along with filenames and the page title.
/// </summary>
public static class WebPageLinkExtractor
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(25);
    private static HttpClient _httpClient = CreateClient();

    // When a response turns out to be a file, stop after the headers.
    private static HttpClient CreateClient() =>
        HttpUserAgentService.CreateHttpClient(RequestTimeout, HttpContentType.Html, maxResponseDrainSize: 0);

    private static readonly Regex PageUrlRegex = new(
        @"https?://[^\s""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    private static readonly Regex SizeRegex = new(
        @"(?i)(?:Size|Download\s*Size)[\s:>-]*(?:from\s*)?([0-9\.]+\s*(?:GB|MB|GiB|MiB))",
        RegexOptions.Compiled);

    private static readonly Regex AnchorRegex = new(
        @"<a[^>]+href\s*=\s*[""'](https?://[^""']+)[""'][^>]*>(.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex BareUrlRegex = new(
        @"https?://[^\s""'<>)]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TitleRegex = new(
        @"<title[^>]*>(.*?)</title>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TagRegex = new(@"<[^>]+>", RegexOptions.Compiled);

    static WebPageLinkExtractor()
    {
        // When User-Agent updates live (e.g. from WebView2 initialization),
        // atomically exchange the HttpClient instance so in-flight requests are not corrupted.
        HttpUserAgentService.UserAgentChanged += _ =>
        {
            Interlocked.Exchange(ref _httpClient, CreateClient());
        };
    }

    /// <summary>
    /// Searches the input text for a web page URL.
    /// </summary>
    public static string? FindExtractablePageUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var matches = PageUrlRegex.Matches(text);
        if (matches.Count != 1)
            return null;

        var candidate = LinkExtractor.CleanUrl(matches[0].Value.TrimEnd('.', ',', ';', '!', ')', ']'));
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            // If it's a known download hoster, treat as download link, not a webpage to crawl
            if (HosterInfo.IsKnownHoster(candidate) || FastHostResolver.IsFastHostUrl(candidate))
                return null;

            if (LinkExtractor.HasRecognizedArchiveOrMediaExtension(Uri.UnescapeDataString(uri.AbsolutePath)))
                return null;

            return candidate;
        }

        return null;
    }

    /// <summary>
    /// Loads the web page and extracts page title + download links.
    /// </summary>
    public static async Task<WebPageExtractionResult> ExtractAsync(string pageUrl,
        IProgress<WebPageExtractionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri))
        {
            throw new ArgumentException("Ungültige URL.", nameof(pageUrl));
        }

        progress?.Report(new("Busy_LoadingPage"));

        // Anti-rate-limiting jitter delay before request
        await HttpJitterHelper.DelayJitterAsync(150, 400, cancellationToken);

        var client = Volatile.Read(ref _httpClient);
        var (html, file) = await ReadPageOrFileAsync(client, pageUrl, cancellationToken);
        if (file != null) return file;

        var result = new WebPageExtractionResult { PageTitle = ExtractPageTitle(html) };

        var sizeMatch = SizeRegex.Match(html);
        if (sizeMatch.Success)
        {
            result.TotalSizeText = sizeMatch.Groups[1].Value.Trim();
            result.TotalSizeBytes = LinkMetadataResolverService.ParseSizeToBytes(result.TotalSizeText);
        }

        progress?.Report(new("Busy_ExtractingPageLinks", result.PageTitle));

        var seen = new HashSet<string>(DownloadUrlComparer.Instance);

        // Primary: links matching known hosters or direct downloads
        foreach (Match match in AnchorRegex.Matches(html))
        {
            var url = WebUtility.HtmlDecode(match.Groups[1].Value.Trim());
            if (!HosterInfo.IsKnownHoster(url) && !FastHostResolver.IsFastHostUrl(url))
                continue;

            if (!seen.Add(url))
                continue;

            var anchorText = WebUtility.HtmlDecode(TagRegex.Replace(match.Groups[2].Value, "")).Trim();
            var fileName = LooksLikeFileName(anchorText) ? anchorText : ExtractFileNameFromFragment(url);
            result.Links.Add(new ExtractedLink
            {
                Url = url,
                RawFileName = fileName,
                Hoster = HosterInfo.DetectHoster(url)
            });
        }

        // Fallback 1: bare URLs in page body (e.g. in scripts or lists)
        if (result.Links.Count == 0)
        {
            foreach (Match match in BareUrlRegex.Matches(html))
            {
                var url = WebUtility.HtmlDecode(match.Value.Trim());
                if (!HosterInfo.IsKnownHoster(url) && !FastHostResolver.IsFastHostUrl(url))
                    continue;

                if (!seen.Add(url))
                    continue;

                result.Links.Add(new ExtractedLink
                {
                    Url = url,
                    RawFileName = ExtractFileNameFromFragment(url),
                    Hoster = HosterInfo.DetectHoster(url)
                });
            }
        }

        // Fallback 2: Paste sites (e.g. pastebin.com, rentry.co, paste.ee)
        if (result.Links.Count == 0)
        {
            var pasteMatches = Regex.Matches(html, @"https?://(?:pastebin\.com|rentry\.co|paste\.ee)/[^\s""'<>)]+", RegexOptions.IgnoreCase);
            foreach (Match pMatch in pasteMatches)
            {
                var pasteUrl = WebUtility.HtmlDecode(pMatch.Value.Trim());
                if (!Uri.TryCreate(pasteUrl, UriKind.Absolute, out var pasteUri))
                    continue;

                var pHost = pasteUri.Host.ToLowerInvariant();
                if (pHost != "pastebin.com" && pHost != "www.pastebin.com" &&
                    pHost != "rentry.co" && pHost != "www.rentry.co" &&
                    pHost != "paste.ee" && pHost != "www.paste.ee")
                {
                    continue;
                }

                try
                {
                    progress?.Report(new("Busy_LoadingPastePage", pasteUrl));
                    var pasteHtml = await client.GetStringAsync(pasteUrl, cancellationToken);
                    foreach (Match m in BareUrlRegex.Matches(pasteHtml))
                    {
                        var url = WebUtility.HtmlDecode(m.Value.Trim());
                        if (!HosterInfo.IsKnownHoster(url) && !FastHostResolver.IsFastHostUrl(url))
                            continue;

                        if (!seen.Add(url))
                            continue;

                        result.Links.Add(new ExtractedLink
                        {
                            Url = url,
                            RawFileName = ExtractFileNameFromFragment(url),
                            Hoster = HosterInfo.DetectHoster(url)
                        });
                    }
                }
                catch { }

                if (result.Links.Count > 0)
                    break;
            }
        }

        if (result.Links.Count > 0)
        {
            progress?.Report(new("Busy_ResolvingPageLinks", result.Links.Count));
            await FastHostResolver.ResolveLinksInParallelAsync(client, result.Links, progress, cancellationToken);
        }

        progress?.Report(new("Busy_CreatingPagePackage", result.Links.Count));
        return result;
    }

    private static async Task<(string Html, WebPageExtractionResult? File)> ReadPageOrFileAsync(
        HttpClient client, string url, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestTimeout);
        var token = deadline.Token;

        using var head = new HttpRequestMessage(HttpMethod.Head, url);
        head.Headers.AcceptEncoding.ParseAdd("identity");
        try
        {
            using var response = await client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.IsSuccessStatusCode &&
                ReadFileHeaders(url, response.Content.Headers, assumeFileWithoutContentType: false) is { } file)
                return (string.Empty, file);
        }
        catch (HttpRequestException)
        {
            // Some origins reject HEAD at the transport level.
        }

        using var get = new HttpRequestMessage(HttpMethod.Get, url);
        using var content = await client.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, token);
        content.EnsureSuccessStatusCode();
        if (content.StatusCode == HttpStatusCode.NoContent) return (string.Empty, null);
        if (ReadFileHeaders(url, content.Content.Headers, assumeFileWithoutContentType: true) is { } download)
            return (string.Empty, download);

        return (await content.Content.ReadAsStringAsync(token), null);
    }

    private static WebPageExtractionResult? ReadFileHeaders(
        string url, HttpContentHeaders headers, bool assumeFileWithoutContentType)
    {
        var disposition = headers.ContentDisposition;
        var type = headers.ContentType?.MediaType?.ToLowerInvariant();
        bool attachment = string.Equals(disposition?.DispositionType, "attachment", StringComparison.OrdinalIgnoreCase);
        if (!attachment && (type is "text/html" or "application/xhtml+xml" || type == null && !assumeFileWithoutContentType))
            return null;

        string? fileName = (disposition?.FileNameStar ?? disposition?.FileName)?.Trim('"').Trim();
        fileName = string.IsNullOrWhiteSpace(fileName) ? LinkExtractor.ExtractFileNameFromUrl(url) : LinkExtractor.SanitizeFileName(fileName);
        if (LinkExtractor.IsPureNumericOrHash(fileName))
        {
            fileName = "downloaded-file" + (type switch
            {
                "application/pdf" => ".pdf",
                "video/mp4" => ".mp4",
                "text/plain" => ".txt",
                _ => ".bin"
            });
        }

        return new WebPageExtractionResult
        {
            DirectDownload = new ExtractedLink
            {
                Url = url, DirectDownloadUrl = url, RawFileName = fileName, Hoster = HosterInfo.DetectHoster(url)
            },
            TotalSizeBytes = headers.ContentLength,
            DirectDownloadChecksum = headers.ContentLength != null && headers.ContentEncoding.Count == 0 && headers.ContentMD5 is { Length: 16 } md5
                ? Convert.ToHexString(md5) : null
        };
    }

    /// <summary>
    /// Reads the title from the page &lt;title&gt;.
    /// </summary>
    internal static string ExtractPageTitle(string html)
    {
        var match = TitleRegex.Match(html);
        var name = match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : string.Empty;

        // Trim common generic page suffixes
        name = Regex.Replace(name, @"\s*[–\-|]\s*(?:Download|Free Download|Home|Index).*$", string.Empty, RegexOptions.IgnoreCase).Trim();

        name = PackageGrouper.SanitizeCleanName(name);

        return string.IsNullOrWhiteSpace(name) ? "Download Package" : name;
    }

    /// <summary>
    /// Fallback: links often carry the filename inside the URL fragment (#file.part01.rar).
    /// </summary>
    private static string ExtractFileNameFromFragment(string url)
    {
        var hashIndex = url.IndexOf('#');
        if (hashIndex < 0 || hashIndex >= url.Length - 1)
            return string.Empty;

        var fragment = Uri.UnescapeDataString(url.Substring(hashIndex + 1)).Trim();
        var clean = Path.GetFileName(fragment);
        return LooksLikeFileName(clean) ? clean : string.Empty;
    }

    private static bool LooksLikeFileName(string text)
    {
        return text.Length is > 0 and <= 150
            && text.Contains('.')
            && !text.Contains("://")
            && !text.Contains('/')
            && !text.Contains('\\')
            && !text.Contains("..")
            && !text.Contains('<')
            && !text.Contains('>');
    }
}
