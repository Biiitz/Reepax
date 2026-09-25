using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;

namespace Reepax.Services.Extractor;

public static class FastHostResolver
{
    private const string HostToken = "fuckingfast";
    public const string CanonicalDomain = "fuckingfast.co";
    public const string DomainRegexPattern = @"fuckingfast\.co|fuckingfast\.net|fuckingfast";

    private static readonly Regex WindowOpenRegex = new(
        $@"(?i)window\.open\s*\(\s*[""'](https?://(?:[a-z0-9\-\.]+\.)?{HostToken}\.(?:co|net)/dl/[^""']+)[""']",
        RegexOptions.Compiled);

    private static readonly Regex BareDlRegex = new(
        $@"(?i)(https?://(?:dl[0-9]*\.)?{HostToken}\.(?:co|net)/dl/[^\s""'<>]+)",
        RegexOptions.Compiled);

    private static readonly Regex LocationDlRegex = new(
        $@"(?i)location(?:\.href)?\s*=\s*[""'](https?://(?:[a-z0-9\-\.]+\.)?{HostToken}\.(?:co|net)/dl/[^""']+)[""']",
        RegexOptions.Compiled);

    private static readonly Regex GenericDlRegex = new(
        $@"(?i)https?://dl[0-9]*\.{HostToken}\.(?:co|net)/[^\s""'<>]+",
        RegexOptions.Compiled);

    private static readonly Regex AnchorDlRegex = new(
        $@"(?i)<a[^>]+href\s*=\s*[""'](https?://(?:dl[0-9]*\.)?{HostToken}\.(?:co|net)/[^""']+)[""']",
        RegexOptions.Compiled);

    private static readonly Regex AnyWindowOpenRegex = new(
        $@"(?i)window\.open\s*\(\s*[""'](https?://(?:[a-z0-9\-\.]+\.)?{HostToken}\.(?:co|net)/[^""']+)[""']",
        RegexOptions.Compiled);

    public static bool IsFastHostUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        var host = uri.Host.ToLowerInvariant();
        var primaryDomain = $"{HostToken}.co";
        var altDomain = $"{HostToken}.net";
        return host == primaryDomain || host.EndsWith($".{primaryDomain}", StringComparison.OrdinalIgnoreCase) ||
               host == altDomain || host.EndsWith($".{altDomain}", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDirectDownloadUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (!IsFastHostUrl(url))
            return false;

        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath.ToLowerInvariant();
        return path.StartsWith("/dl/") || host.StartsWith("dl.") || host.StartsWith("dl1.") || host.StartsWith("dl2.");
    }

    public static string? ExtractDirectUrlFromHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var patterns = new[] { WindowOpenRegex, BareDlRegex, LocationDlRegex, GenericDlRegex, AnchorDlRegex, AnyWindowOpenRegex };
        foreach (var pattern in patterns)
        {
            var m = pattern.Match(html);
            if (m.Success)
            {
                var candidate = WebUtility.HtmlDecode(m.Groups.Count > 1 ? m.Groups[1].Value.Trim() : m.Value.Trim());
                if (IsFastHostUrl(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Detects Cloudflare challenge pages (403 + "Just a moment" / challenges.cloudflare.com).
    /// </summary>
    public static bool IsCloudflareChallenge(HttpStatusCode statusCode, string? html)
    {
        if (string.IsNullOrEmpty(html))
            return false;

        bool looksLikeChallenge = html.Contains("Just a moment...", StringComparison.OrdinalIgnoreCase) ||
                                  html.Contains("challenges.cloudflare.com", StringComparison.OrdinalIgnoreCase) ||
                                  html.Contains("cf-chl-", StringComparison.OrdinalIgnoreCase) ||
                                  html.Contains("Attention Required! | Cloudflare", StringComparison.OrdinalIgnoreCase);

        return looksLikeChallenge && (statusCode == HttpStatusCode.Forbidden ||
                                      statusCode == HttpStatusCode.ServiceUnavailable ||
                                      statusCode == HttpStatusCode.TooManyRequests ||
                                      (int)statusCode == 521 || (int)statusCode == 522 || (int)statusCode == 523 ||
                                      html.Contains("<title>Just a moment", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<string?> ResolveDirectUrlAsync(
        HttpClient httpClient, 
        string pageUrl, 
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pageUrl) || !IsFastHostUrl(pageUrl))
            return null;

        if (IsDirectDownloadUrl(pageUrl))
            return pageUrl;

        try
        {
            var requestUrl = pageUrl;
            var hashIdx = requestUrl.IndexOf('#');
            if (hashIdx > 0) requestUrl = requestUrl[..hashIdx];

            using var req = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            using var res = await httpClient.SendAsync(req, HttpCompletionOption.ResponseContentRead, cancellationToken);

            var html = await res.Content.ReadAsStringAsync(cancellationToken);

            // Cloudflare challenge ("Just a moment...") cannot be resolved via plain HTTP
            if (IsCloudflareChallenge(res.StatusCode, html))
            {
                AppLogger.Info($"[FastHostResolver] Cloudflare protection detected on '{pageUrl}' — browser window fallback will handle it.");
                return null;
            }

            if (!res.IsSuccessStatusCode) return null;

            var directUrl = ExtractDirectUrlFromHtml(html);
            if (!string.IsNullOrWhiteSpace(directUrl))
            {
                AppLogger.Info($"[FastHostResolver] Resolved '{pageUrl}' -> '{directUrl}'");
            }
            return directUrl;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[FastHostResolver] Could not resolve '{pageUrl}': {ex.Message}");
            return null;
        }
    }

    public static async Task ResolveLinksInParallelAsync(
        HttpClient httpClient, 
        List<ExtractedLink> links, 
        IProgress<string>? progress = null, 
        CancellationToken cancellationToken = default)
    {
        if (links == null || links.Count == 0) return;

        var hostLinks = links.Where(l => IsFastHostUrl(l.Url)).ToList();
        if (hostLinks.Count == 0) return;

        int total = hostLinks.Count;
        int completed = 0;

        using var semaphore = new SemaphoreSlim(6, 6);
        var tasks = hostLinks.Select(async link =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var directUrl = await ResolveDirectUrlAsync(httpClient, link.Url, cancellationToken);
                if (!string.IsNullOrWhiteSpace(directUrl))
                {
                    // Only set direct link — original page URL is preserved
                    // so expired links can be re-resolved later.
                    link.DirectDownloadUrl = directUrl;
                }
            }
            finally
            {
                semaphore.Release();
                var count = Interlocked.Increment(ref completed);
                progress?.Report($"Direktdownloads werden aufgelöst ({count}/{total})…");
            }
        });

        await Task.WhenAll(tasks);
    }
}
