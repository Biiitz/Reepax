using System.Net;
using System.Net.Http;
using Reepax.Models;

namespace Reepax.Services.SystemIntegration;

internal sealed class CookieRedirectHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    private const int MaximumRedirects = 10;
    private static readonly HttpRequestOptionsKey<DownloadCookieContext> CookieContextKey = new("DownloadCookieContext");

    internal static void ApplyCookies(HttpRequestMessage request, DownloadItem item)
        => request.Options.Set(CookieContextKey, item.GetCookieContext());

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var cookies = request.Options.TryGetValue(CookieContextKey, out var context)
            ? context.Container : new CookieContainer();
        for (int redirects = 0; ; redirects++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            request.Headers.Remove("Cookie");
            var header = cookies.GetCookieHeader(request.RequestUri!);
            if (header.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", header);

            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.RequestMessage ??= request;
            if (response.Headers.TryGetValues("Set-Cookie", out var values))
            {
                foreach (var value in values)
                {
                    try { cookies.SetCookies(request.RequestUri!, value); }
                    catch (CookieException) { }
                }
            }

            if (redirects >= MaximumRedirects || !TryGetRedirect(request, response, out var target))
                return response;

            response.Dispose();
            request.RequestUri = target;
            request.Headers.Authorization = null;
            request.Headers.Host = null;
        }
    }

    private static bool TryGetRedirect(HttpRequestMessage request, HttpResponseMessage response, out Uri target)
    {
        target = null!;
        // Transfer requests only use GET and HEAD, with no body to replay.
        if ((request.Method != HttpMethod.Get && request.Method != HttpMethod.Head) || request.Content != null ||
            response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect) ||
            response.Headers.Location is not { } location ||
            !Uri.TryCreate(request.RequestUri, location, out var resolved) ||
            (resolved.Scheme != Uri.UriSchemeHttp && resolved.Scheme != Uri.UriSchemeHttps) ||
            (request.RequestUri!.Scheme == Uri.UriSchemeHttps && resolved.Scheme != Uri.UriSchemeHttps))
            return false;

        target = resolved;
        return true;
    }
}
