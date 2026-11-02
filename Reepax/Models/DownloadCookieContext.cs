using System.Net;
using System.Text.Json.Serialization;

namespace Reepax.Models;

public sealed class DownloadCookie
{
    public string Name { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public string Domain { get; init; } = string.Empty;
    public string Path { get; init; } = "/";
    public bool HostOnly { get; init; } = true;
    public bool Secure { get; init; }
    public bool HttpOnly { get; init; }
    public DateTime? ExpiresUtc { get; init; }
}

public sealed class DownloadCookieContext
{
    private CookieContainer? _container;

    public string SourceUrl { get; init; } = string.Empty;
    public DownloadCookie[] Cookies { get; init; } = [];

    [JsonIgnore]
    internal CookieContainer Container => LazyInitializer.EnsureInitialized(ref _container, CreateContainer);

    public static DownloadCookieContext FromBrowserCookies(string sourceUrl, IEnumerable<Cookie> cookies)
        => new()
        {
            SourceUrl = sourceUrl,
            Cookies = cookies.Select(cookie => new DownloadCookie
            {
                Name = cookie.Name,
                Value = cookie.Value,
                Domain = cookie.Domain,
                Path = cookie.Path,
                HostOnly = !cookie.Domain.StartsWith('.'),
                Secure = cookie.Secure,
                HttpOnly = cookie.HttpOnly,
                ExpiresUtc = cookie.Expires == DateTime.MinValue ? null : cookie.Expires.ToUniversalTime()
            }).ToArray()
        };

    internal static DownloadCookieContext FromHeader(string? sourceUrl, string? header)
    {
        if (!TryGetHttpUri(sourceUrl, out var source) || string.IsNullOrWhiteSpace(header))
            return new DownloadCookieContext { SourceUrl = sourceUrl ?? string.Empty };

        // Legacy headers have no scope metadata; never grant them to sibling hosts.
        var cookies = new List<DownloadCookie>();
        foreach (var pair in header.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = pair.IndexOf('=');
            if (separator <= 0) continue;
            cookies.Add(new DownloadCookie
            {
                Name = pair[..separator].Trim(),
                Value = pair[(separator + 1)..].Trim(),
                Domain = source.Host,
                HostOnly = true,
                Secure = source.Scheme == Uri.UriSchemeHttps
            });
        }
        return new DownloadCookieContext { SourceUrl = source.AbsoluteUri, Cookies = cookies.ToArray() };
    }

    internal string? GetCookieHeader()
        => TryGetHttpUri(SourceUrl, out var source) ? Container.GetCookieHeader(source) : null;

    internal static bool TryGetHttpUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) &&
            (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            uri = parsed;
            return true;
        }
        uri = null!;
        return false;
    }

    private CookieContainer CreateContainer()
    {
        var states = Cookies ?? [];
        var count = Math.Max(300, states.Length);
        var maximumSize = states.Length == 0 ? 4096 : Math.Max(4096,
            states.Max(cookie => (cookie.Name?.Length ?? 0) + (cookie.Value?.Length ?? 0) + 32));
        var container = new CookieContainer(count, count, maximumSize);
        if (!TryGetHttpUri(SourceUrl, out var source)) return container;
        foreach (var state in states)
        {
            try
            {
                var cookie = new Cookie(state.Name, state.Value, state.Path)
                {
                    Secure = state.Secure,
                    HttpOnly = state.HttpOnly
                };
                if (state.ExpiresUtc.HasValue) cookie.Expires = state.ExpiresUtc.Value.ToUniversalTime();
                if (state.HostOnly)
                {
                    if (!string.Equals(state.Domain, source.Host, StringComparison.OrdinalIgnoreCase)) continue;
                }
                else cookie.Domain = state.Domain;
                container.Add(source, cookie);
            }
            catch (CookieException)
            {
                // Invalid persisted cookies must not broaden scope or abort a download.
            }
        }
        return container;
    }
}
