using System.Text.Json.Serialization;

namespace Reepax.Models;

public partial class DownloadItem
{
    private DownloadCookieContext? _cookieContext;

    [JsonIgnore]
    public DownloadCookieContext? CookieContext
    {
        get => Volatile.Read(ref _cookieContext);
        set => Volatile.Write(ref _cookieContext, value);
    }

    internal DownloadCookieContext GetCookieContext()
    {
        if (CookieContext is { } context) return context;
        var sourceUrl = string.IsNullOrWhiteSpace(DirectDownloadUrl) ? OriginalUrl : DirectDownloadUrl;
        var created = DownloadCookieContext.FromHeader(sourceUrl, Cookies);
        return Interlocked.CompareExchange(ref _cookieContext, created, null) ?? created;
    }

    partial void OnCookiesChanged(string? value) => CookieContext = null;
}
