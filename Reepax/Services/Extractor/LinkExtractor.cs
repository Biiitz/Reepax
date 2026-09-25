using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Reepax.Models;

namespace Reepax.Services.Extractor;

public class ExtractedLink
{
    public string Url { get; set; } = string.Empty;
    public string? DirectDownloadUrl { get; set; }
    public string RawFileName { get; set; } = string.Empty;
    public string? ContextTitle { get; set; }
    public HosterInfo Hoster { get; set; } = new();
}

public static partial class LinkExtractor
{
    private static readonly Regex UrlRegex = new(
        @"(?:https?:\/\/|(?:www\.)|(?:[a-zA-Z0-9\-]+\.(?:co|com|net|org|to|cc|io|sx|me|is|la|ws|su|party|tech|watch)\/))[a-zA-Z0-9\-\._~:\/\?#\[\]@!\$&'\(\)\*\+,;=%]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TrailingPunctuationRegex = new(
        @"[\.,;\)\]\}\>'""]+$",
        RegexOptions.Compiled);

    private static readonly Regex BbCodeMarkdownRegex = new(
        @"\[\/?[a-zA-Z0-9=\-_]+\]|[*_~`#]+",
        RegexOptions.Compiled);

    private static readonly Regex HrefAnchorRegex = new(
        @"<a\b[^>]*?href\s*=\s*[""']([^""']+)[""'][^>]*>(.*?)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex HtmlTagRegex = new(
        @"<[^>]+>",
        RegexOptions.Compiled);

    private static readonly Regex PureHexRegex = new(
        @"^[0-9a-fA-F]+$",
        RegexOptions.Compiled);

    private static readonly Regex PureDigitsRegex = new(
        @"^\d+$",
        RegexOptions.Compiled);

    public static readonly Regex TokenWordRegex = new(
        @"[a-zA-Z0-9_\-\+]{20,}",
        RegexOptions.Compiled);

    private static readonly Regex ArchiveOrMediaExtRegex = new(
        @"(?i)\.(?:rar|zip|7z|tar|gz|iso|bin|exe|pkg|mkv|mp4|avi|mov|flv|wmv|mp3|flac|wav|pdf|epub|001|002|r\d{2})$",
        RegexOptions.Compiled);


    public static List<ExtractedLink> ExtractLinks(string rawText)
    {
        var result = new List<ExtractedLink>();
        if (string.IsNullOrWhiteSpace(rawText))
            return result;

        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 0. Browser HTML fragment (drag & drop selection): isolate genuine content,
        //    preventing fragment headers (Version/StartHTML/...) from leaking as junk
        rawText = IsolateHtmlFragment(rawText);

        // 1. Extract href anchors directly from HTML markup (the actual URLs;
        //    the visible plaintext of a browser selection often omits target URLs)
        foreach (Match anchor in HrefAnchorRegex.Matches(rawText))
        {
            var cleanedUrl = CleanUrl(anchor.Groups[1].Value);
            if (string.IsNullOrWhiteSpace(cleanedUrl) ||
                !cleanedUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                continue;

            if (seenUrls.Add(cleanedUrl))
            {
                // Use anchor text as context title if readable (not a bare URL)
                var anchorText = WebUtility.HtmlDecode(HtmlTagRegex.Replace(anchor.Groups[2].Value, " ")).Trim();
                string? title = null;
                if (anchorText.Length >= 3 && !anchorText.Contains("http", StringComparison.OrdinalIgnoreCase))
                {
                    var cleanedTitle = CleanHeadingLine(anchorText);
                    if (!string.IsNullOrWhiteSpace(cleanedTitle) && cleanedTitle.Length >= 3)
                    {
                        title = cleanedTitle;
                    }
                }

                result.Add(new ExtractedLink
                {
                    Url = cleanedUrl,
                    RawFileName = ExtractFileNameFromUrl(cleanedUrl),
                    ContextTitle = title,
                    Hoster = HosterInfo.DetectHoster(cleanedUrl)
                });
            }
        }

        // 2. Strip HTML tags and scan remaining plaintext line by line
        var plainText = HtmlTagRegex.Replace(rawText, "\n");

        var lines = plainText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        string? currentContextTitle = null;

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmedLine))
                continue;

            var matches = UrlRegex.Matches(trimmedLine);
            if (matches.Count == 0)
            {
                // This line might be a title / release name header
                var cleanHeading = CleanHeadingLine(trimmedLine);
                if (!string.IsNullOrWhiteSpace(cleanHeading) && cleanHeading.Length >= 3)
                {
                    currentContextTitle = cleanHeading;
                }
            }
            else
            {
                foreach (Match match in matches)
                {
                    var cleanedUrl = CleanUrl(match.Value);
                    if (string.IsNullOrWhiteSpace(cleanedUrl))
                        continue;

                    if (seenUrls.Add(cleanedUrl))
                    {
                        var hoster = HosterInfo.DetectHoster(cleanedUrl);
                        var rawFileName = ExtractFileNameFromUrl(cleanedUrl);

                        result.Add(new ExtractedLink
                        {
                            Url = cleanedUrl,
                            RawFileName = rawFileName,
                            ContextTitle = currentContextTitle,
                            Hoster = hoster
                        });
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Normalizes input text for dialog input field: HTML fragments/markup
    /// (browser copy or drag & drop) are transformed into a clean link list (one URL per
    /// line). Pure text is preserved intact (retaining context titles).
    /// </summary>
    public static string NormalizeInputText(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return rawText;

        bool looksLikeHtml = rawText.Contains("StartFragment", StringComparison.OrdinalIgnoreCase) ||
                             rawText.Contains("<a ", StringComparison.OrdinalIgnoreCase) ||
                             rawText.Contains("<html", StringComparison.OrdinalIgnoreCase);
        if (!looksLikeHtml)
            return rawText;

        var links = ExtractLinks(rawText);
        if (links.Count == 0)
            return rawText;

        return string.Join(Environment.NewLine, links.Select(l => l.Url));
    }

    /// <summary>
    /// Isolates genuine content of a browser HTML fragment (between
    /// StartFragment/EndFragment markers) to prevent metadata headers from being interpreted as text.
    /// </summary>
    private static string IsolateHtmlFragment(string text)
    {
        const string startMarker = "<!--StartFragment-->";
        const string endMarker = "<!--EndFragment-->";

        var start = text.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
        var end = text.IndexOf(endMarker, StringComparison.OrdinalIgnoreCase);
        if (start >= 0 && end > start)
        {
            return text[(start + startMarker.Length)..end];
        }
        return text;
    }

    private static string CleanHeadingLine(string line)
    {
        // Strip BBCode and Markdown tags: [b], [/b], [url=...], **, ##
        var clean = BbCodeMarkdownRegex.Replace(line, " ").Trim();

        // Strip common conversational introductory phrases
        clean = Regex.Replace(clean, @"(?i)^(?:here\s+are\s+the\s+(?:download\s+)?(?:files|links)\s+for|hier\s+sind\s+die\s+(?:download\s+)?(?:dateien|links)\s+f[üu]r|download\s+links?\s+for|links?\s+for|files?\s+for)\s*:?\s*", "");

        // Strip common prefixes: "Download:", "Name:", "Title:", "Release:", "Links:"
        clean = Regex.Replace(clean, @"(?i)^(?:download[s]?|name|title|release|links|file|files|mirror[s]?|pw|password)\s*:\s*", "");
        clean = clean.Trim(' ', '-', '_', ':', '|', ',', ';', '/', '\\');

        if (clean.EndsWith(")") && !clean.Contains("(")) clean = clean[..^1].Trim();
        if (clean.EndsWith("]") && !clean.Contains("[")) clean = clean[..^1].Trim();
        if (clean.StartsWith("(") && !clean.Contains(")")) clean = clean[1..].Trim();
        if (clean.StartsWith("[") && !clean.Contains("]")) clean = clean[1..].Trim();

        return clean;
    }

    public static string CleanUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        url = TrailingPunctuationRegex.Replace(url.Trim(), "");

        // Remove html entities if any
        url = WebUtility.HtmlDecode(url);

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        return url;
    }

    public static string ExtractFileNameFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);

            // 1. Check query string for filename/file parameter first
            var query = uri.Query;
            if (!string.IsNullOrEmpty(query))
            {
                var queryMatch = Regex.Match(query, @"(?:file|name|filename|title)=([^&]+)", RegexOptions.IgnoreCase);
                if (queryMatch.Success)
                {
                    var queryFileName = WebUtility.UrlDecode(queryMatch.Groups[1].Value);
                    if (!string.IsNullOrWhiteSpace(queryFileName) && !IsPureNumericOrHash(queryFileName))
                    {
                        return SanitizeFileName(queryFileName);
                    }
                }
            }

            // 1b. Check URL fragment (#filename.rar) - some hosters provide the filename there
            if (!string.IsNullOrEmpty(uri.Fragment) && uri.Fragment.Length > 1)
            {
                var fragmentName = WebUtility.UrlDecode(uri.Fragment.TrimStart('#'));
                if (!string.IsNullOrWhiteSpace(fragmentName) && fragmentName.Contains('.') && !IsPureNumericOrHash(fragmentName))
                {
                    return SanitizeFileName(fragmentName);
                }
            }

            // 1c. Hoster-specific check: FastHost direct download
            // Direct links look like https://dl1.../dl/<TOKEN>
            // If the URL has no valid fragment filename, the /dl/ segment without archive extension is an opaque token, NEVER a filename!
            if (FastHostResolver.IsDirectDownloadUrl(url))
            {
                return "download_file";
            }

            // 2. Check path segments
            var pathSegments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (pathSegments.Length > 0)
            {
                for (int i = pathSegments.Length - 1; i >= 0; i--)
                {
                    var seg = WebUtility.UrlDecode(pathSegments[i]);

                    if (seg.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                        seg = seg[..^5];
                    else if (seg.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
                        seg = seg[..^4];
                    else if (seg.EndsWith(".php", StringComparison.OrdinalIgnoreCase))
                        seg = seg[..^4];

                    // Check if segment has a file extension or looks like a file name
                    if (seg.Contains('.') && !IsPureNumericOrHash(seg))
                    {
                        return SanitizeFileName(seg);
                    }
                }

                // If no dot segment found, check if last segment is non-cryptic and not a generic action endpoint
                var lastSegment = WebUtility.UrlDecode(pathSegments[^1]);
                if (lastSegment.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) lastSegment = lastSegment[..^5];
                else if (lastSegment.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)) lastSegment = lastSegment[..^4];
                else if (lastSegment.EndsWith(".php", StringComparison.OrdinalIgnoreCase)) lastSegment = lastSegment[..^4];

                if (!string.IsNullOrWhiteSpace(lastSegment) && 
                    !IsPureNumericOrHash(lastSegment) && 
                    lastSegment.Length > 2 &&
                    lastSegment.Length <= 40 &&
                    !IsGenericActionEndpoint(lastSegment))
                {
                    return SanitizeFileName(lastSegment);
                }
            }

            return "download_file";
        }
        catch
        {
            return "download_file";
        }
    }

    public static bool HasRecognizedArchiveOrMediaExtension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        return ArchiveOrMediaExtRegex.IsMatch(fileName.Trim());
    }

    private static bool IsGenericActionEndpoint(string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) return true;
        return segment.Equals("dl", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("file", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("files", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("get", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("view", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("download", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("downloads", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("d", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("f", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("u", StringComparison.OrdinalIgnoreCase);
    }

    public static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return "download_file";
        var clean = Path.GetFileName(fileName.Trim()).Trim('\"', '\'', ' ', '\t');
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            clean = clean.Replace(c, '_');
        }
        return string.IsNullOrWhiteSpace(clean) ? "download_file" : clean;
    }

    public static bool IsPureNumericOrHash(string str)
    {
        if (string.IsNullOrWhiteSpace(str))
            return true;

        var trimmed = str.Trim();

        // 1. Generic action endpoints or placeholder
        if (IsGenericActionEndpoint(trimmed) || 
            trimmed.Equals("download_file", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Strip extension first if any
        var nameWithoutExt = Path.GetFileNameWithoutExtension(trimmed);
        if (string.IsNullOrWhiteSpace(nameWithoutExt))
            nameWithoutExt = trimmed;

        nameWithoutExt = nameWithoutExt.Trim();

        // 2. Pure digits: e.g. "823791283" or "123456"
        if (PureDigitsRegex.IsMatch(nameWithoutExt))
            return true;

        // 3. Pure hex hash: e.g. "a8f9b2c3d4e5f6" or md5 / sha1 / sha256
        if (nameWithoutExt.Length >= 8 && PureHexRegex.IsMatch(nameWithoutExt))
            return true;

        // 4. Generic action endpoints without extension
        if (IsGenericActionEndpoint(nameWithoutExt))
            return true;

        // 5. Direct link tokens (high-entropy alphanumeric hash tokens without spaces or with mixed base64)
        // e.g. "y9PN0SnSOrlAL VPqdI HnfkzH7uS7rpsC8nKLtiMlZPDgPin0Ar-tWT9ZJ-fD6lDCMmQ3FhrA2B79qnCIhoC7LNv8IkTwElQaYaGMRKfHEXQkWCzJW8pfEf5MkNHb7TJ8xkKDkqG5dskmlx6s9jBU7m7yJLevY"
        if (!HasRecognizedArchiveOrMediaExtension(str))
        {
            // Continuous opaque token chunk >= 35 chars without spaces or with base64 symbols
            if (nameWithoutExt.Length >= 35 && !nameWithoutExt.Contains('.'))
            {
                var words = nameWithoutExt.Split(new[] { ' ', '+', '_' }, StringSplitOptions.RemoveEmptyEntries);
                if (words.Any(w => w.Length >= 25 && Regex.IsMatch(w, @"[0-9]") && Regex.IsMatch(w, @"[a-z]") && Regex.IsMatch(w, @"[A-Z]")))
                {
                    return true;
                }
                if (nameWithoutExt.Length >= 50 && !nameWithoutExt.Contains(' '))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
