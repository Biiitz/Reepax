using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Helpers;
using Reepax.Models;
using Reepax.Services.SystemIntegration;

namespace Reepax.Services.Extractor;

public class WebPageExtractionResult
{
    public string PageTitle { get; set; } = "Download Package";
    public string? TotalSizeText { get; set; }
    public long? TotalSizeBytes { get; set; }
    public List<ExtractedLink> Links { get; } = new();
}

/// <summary>
/// Fetches a web page and extracts download links
/// along with filenames and the page title.
/// </summary>
public static class WebPageLinkExtractor
{
    private static readonly HttpClient _httpClient = HttpUserAgentService.CreateHttpClient(TimeSpan.FromSeconds(25), HttpContentType.Html);

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
        // synchronize default headers of the extractor client.
        HttpUserAgentService.UserAgentChanged += _ =>
        {
            HttpUserAgentService.ApplyDefaultBrowserHeaders(_httpClient.DefaultRequestHeaders, null, HttpContentType.Html);
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

        var candidate = matches[0].Value.TrimEnd('.', ',', ';', '!', ')', ']');
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            // If it's a known download hoster, treat as download link, not a webpage to crawl
            if (HosterInfo.IsKnownHoster(candidate) || FastHostResolver.IsFastHostUrl(candidate))
                return null;

            var path = uri.AbsolutePath.ToLowerInvariant();
            // Ignore direct archive/media/binary downloads
            if (path.EndsWith(".rar") || path.EndsWith(".zip") || path.EndsWith(".7z") ||
                path.EndsWith(".iso") || path.EndsWith(".tar") || path.EndsWith(".gz") ||
                path.EndsWith(".bin") || path.EndsWith(".exe"))
            {
                return null;
            }

            return candidate;
        }

        return null;
    }

    /// <summary>
    /// Loads the web page and extracts page title + download links.
    /// </summary>
    public static async Task<WebPageExtractionResult> ExtractAsync(string pageUrl, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri))
        {
            throw new ArgumentException("Ungültige URL.", nameof(pageUrl));
        }

        progress?.Report("Webseite wird geladen…");

        // Anti-rate-limiting jitter delay before request
        await HttpJitterHelper.DelayJitterAsync(150, 400, cancellationToken);

        var html = await _httpClient.GetStringAsync(pageUrl, cancellationToken);

        var result = new WebPageExtractionResult { PageTitle = ExtractPageTitle(html) };

        var sizeMatch = SizeRegex.Match(html);
        if (sizeMatch.Success)
        {
            result.TotalSizeText = sizeMatch.Groups[1].Value.Trim();
            result.TotalSizeBytes = LinkMetadataResolverService.ParseSizeToBytes(result.TotalSizeText);
        }

        progress?.Report($"'{result.PageTitle}': Links werden extrahiert…");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                    progress?.Report($"Paste-Seite wird geladen: {pasteUrl}…");
                    var pasteHtml = await _httpClient.GetStringAsync(pasteUrl, cancellationToken);
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
            progress?.Report($"{result.Links.Count} Links gefunden – Direktdownloads werden aufgelöst…");
            await FastHostResolver.ResolveLinksInParallelAsync(_httpClient, result.Links, progress, cancellationToken);
        }

        progress?.Report($"{result.Links.Count} Links gefunden – Paket wird erstellt…");
        return result;
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
