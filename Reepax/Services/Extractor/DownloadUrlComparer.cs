using System;
using System.Collections.Generic;

namespace Reepax.Services.Extractor;

internal sealed class DownloadUrlComparer : IEqualityComparer<string>
{
    internal static DownloadUrlComparer Instance { get; } = new();

    private DownloadUrlComparer() { }

    public bool Equals(string? x, string? y) =>
        StringComparer.Ordinal.Equals(GetIdentityKey(x), GetIdentityKey(y));

    public int GetHashCode(string obj) => StringComparer.Ordinal.GetHashCode(GetIdentityKey(obj)!);

    private static string? GetIdentityKey(string? url)
    {
        if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeFtp))
            return url;

        int authorityStart = url.IndexOf("://", StringComparison.Ordinal);
        if (authorityStart < 0) return url;
        authorityStart += 3;
        int resourceStart = url.IndexOfAny(new[] { '/', '?', '#' }, authorityStart);
        if (resourceStart < 0) resourceStart = url.Length;
        string authority = url[authorityStart..resourceStart];
        int hostStart = authority.LastIndexOf('@') + 1;
        string resource = url[resourceStart..];
        if (resource.Length == 0 || resource[0] != '/') resource = "/" + resource;

        // Normalize only scheme and host; preserve credentials and the raw resource, including signed values.
        return uri.Scheme + "://" + authority[..hostStart] + authority[hostStart..].ToLowerInvariant() + resource;
    }
}
