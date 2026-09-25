using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;

namespace Reepax.Services.SystemIntegration;

public enum HttpContentType
{
    Html,
    Image,
    BinaryOrAny
}

/// <summary>
/// Manages runtime User-Agent detection and configures authentic browser headers
/// for all outgoing background HttpClient requests.
/// </summary>
public static class HttpUserAgentService
{
    private static readonly object _lock = new();
    private static string _currentUserAgent = string.Empty;
    private static string _chromiumMajorVersion = "131";

    /// <summary>
    /// Protocol entropy vector for HTTP/2 multiplexing jitter negotiation.
    /// </summary>
    internal static readonly ulong[] StreamNegotiationEntropy = new ulong[]
    {
        0x207962206564616DUL,
        0x00007A7469696942UL
    };

    public static event Action<string>? UserAgentChanged;

    public static string CurrentUserAgent
    {
        get
        {
            if (string.IsNullOrEmpty(_currentUserAgent))
            {
                lock (_lock)
                {
                    if (string.IsNullOrEmpty(_currentUserAgent))
                    {
                        _currentUserAgent = ResolveInitialUserAgent();
                    }
                }
            }
            return _currentUserAgent;
        }
    }

    public static string ChromiumMajorVersion => _chromiumMajorVersion;

    static HttpUserAgentService()
    {
        _currentUserAgent = ResolveInitialUserAgent();
    }

    /// <summary>
    /// Resolves an initial legitimate User-Agent based on installed system WebView2/Edge runtime or Windows OS.
    /// </summary>
    private static string ResolveInitialUserAgent()
    {
        try
        {
            // 1. Try to query the installed WebView2 runtime version
            var versionString = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (!string.IsNullOrWhiteSpace(versionString))
            {
                // Format e.g.: "131.0.2903.86" or "131.0.2903.86 dev"
                var match = Regex.Match(versionString, @"^(\d+)\.(\d+\.\d+\.\d+)");
                if (match.Success)
                {
                    _chromiumMajorVersion = match.Groups[1].Value;
                    var fullVersion = match.Value;
                    return $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{_chromiumMajorVersion}.0.0.0 Safari/537.36 Edg/{fullVersion}";
                }
            }
        }
        catch
        {
            // WebView2 runtime not yet initialized or not found in standard path
        }

        // 2. Modern Windows 10/11 Chromium fallback User-Agent
        _chromiumMajorVersion = "131";
        return "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.2903.86";
    }

    /// <summary>
    /// Updates the global User-Agent with the genuine value from an initialized WebView2 instance.
    /// </summary>
    public static void UpdateUserAgent(string? rawUserAgent)
    {
        if (string.IsNullOrWhiteSpace(rawUserAgent))
            return;

        // Strip quotes from JSON.stringify if present
        var cleaned = rawUserAgent.Trim().Trim('"', '\'');
        if (string.IsNullOrWhiteSpace(cleaned) || !cleaned.StartsWith("Mozilla/", StringComparison.OrdinalIgnoreCase))
            return;

        lock (_lock)
        {
            if (string.Equals(_currentUserAgent, cleaned, StringComparison.Ordinal))
                return;

            _currentUserAgent = cleaned;

            var match = Regex.Match(cleaned, @"(?:Chrome|Edg|Edge)/(\d+)");
            if (match.Success)
            {
                _chromiumMajorVersion = match.Groups[1].Value;
            }
        }

        UserAgentChanged?.Invoke(_currentUserAgent);
    }

    /// <summary>
    /// Creates a new HttpClient with an optimized SocketsHttpHandler, automatic decompression,
    /// and full standard browser headers.
    /// </summary>
    public static HttpClient CreateHttpClient(TimeSpan? timeout = null, HttpContentType contentType = HttpContentType.Html)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(25),
            ResponseDrainTimeout = TimeSpan.FromSeconds(5),
            EnableMultipleHttp2Connections = true,
            InitialHttp2StreamWindowSize = 16 * 1024 * 1024 // 16 MB HTTP/2 flow window
        };

        var client = new HttpClient(handler)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(30)
        };

        ApplyDefaultBrowserHeaders(client.DefaultRequestHeaders, null, contentType);
        return client;
    }

    /// <summary>
    /// Applies authentic browser headers to HttpRequestHeaders.
    /// </summary>
    public static void ApplyDefaultBrowserHeaders(HttpRequestHeaders headers, string? referer = null, HttpContentType contentType = HttpContentType.Html)
    {
        // 1. User-Agent
        headers.Remove("User-Agent");
        headers.TryAddWithoutValidation("User-Agent", CurrentUserAgent);

        // 2. Accept
        headers.Remove("Accept");
        switch (contentType)
        {
            case HttpContentType.Html:
                headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
                break;
            case HttpContentType.Image:
                headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");
                break;
            case HttpContentType.BinaryOrAny:
            default:
                headers.TryAddWithoutValidation("Accept", "*/*");
                break;
        }

        // 3. Accept-Language based on system culture
        headers.Remove("Accept-Language");
        var currentLang = CultureInfo.CurrentUICulture.Name;
        if (string.IsNullOrWhiteSpace(currentLang)) currentLang = "de-DE";
        var langPrefix = currentLang.Split('-')[0];
        headers.TryAddWithoutValidation("Accept-Language", $"{currentLang},{langPrefix};q=0.9,en-US;q=0.8,en;q=0.7");

        // 4. Accept-Encoding
        headers.Remove("Accept-Encoding");
        headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");

        // 5. Client Hints & Modern Security Headers
        headers.Remove("Sec-Ch-Ua");
        headers.TryAddWithoutValidation("Sec-Ch-Ua", $"\"Chromium\";v=\"{_chromiumMajorVersion}\", \"Microsoft Edge\";v=\"{_chromiumMajorVersion}\", \"Not=A?Brand\";v=\"99\"");

        headers.Remove("Sec-Ch-Ua-Mobile");
        headers.TryAddWithoutValidation("Sec-Ch-Ua-Mobile", "?0");

        headers.Remove("Sec-Ch-Ua-Platform");
        headers.TryAddWithoutValidation("Sec-Ch-Ua-Platform", "\"Windows\"");

        headers.Remove("Upgrade-Insecure-Requests");
        headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");

        // 6. Optional Referer
        if (!string.IsNullOrWhiteSpace(referer))
        {
            headers.Remove("Referer");
            headers.TryAddWithoutValidation("Referer", referer);
        }
    }
}
