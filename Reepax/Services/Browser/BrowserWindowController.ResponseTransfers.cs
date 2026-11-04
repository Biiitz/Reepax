using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Storage;

namespace Reepax.Services.Browser;

public partial class BrowserWindowController
{
    private CoreWebView2DevToolsProtocolEventReceiver? _responseReceiver;
    private DownloadItem? _responseItem;
    internal event Action? ResponseTransferStarted;

    private async Task ConfigureResponseTransfersAsync()
    {
        _responseReceiver = _webView.CoreWebView2.GetDevToolsProtocolEventReceiver("Fetch.requestPaused");
        _responseReceiver.DevToolsProtocolEventReceived += OnResponsePaused;
        await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Fetch.enable",
            "{\"patterns\":[{\"resourceType\":\"Document\",\"requestStage\":\"Response\"}]}");
    }

    private async void OnResponsePaused(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        string? requestId = null;
        BrowserResponseStream? stream = null;
        bool claimed = false;
        try
        {
            using var message = JsonDocument.Parse(args.ParameterObjectAsJson);
            var root = message.RootElement;
            requestId = root.GetProperty("requestId").GetString()!;
            if (_isDisposed) return;
            if (!root.TryGetProperty("responseStatusCode", out var code) || code.GetInt32() is < 200 or >= 300 ||
                !root.TryGetProperty("responseHeaders", out var headers) || !IsDownloadResponse(headers))
            {
                await ContinueResponseAsync(requestId);
                return;
            }
            if (_downloadClaimed || _queue.GetWindowItem(_windowId) is not { IsUnavailableForProcessing: false } item)
            {
                await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Fetch.failRequest",
                    JsonSerializer.Serialize(new { requestId, errorReason = "Aborted" }));
                return;
            }
            _downloadClaimed = claimed = true;
            _responseItem = item;
            using var opened = JsonDocument.Parse(await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync(
                "Fetch.takeResponseBodyAsStream", JsonSerializer.Serialize(new { requestId })));
            stream = new BrowserResponseStream(_webView.CoreWebView2, _webView.Dispatcher, requestId,
                opened.RootElement.GetProperty("stream").GetString()!);
            var original = root.GetProperty("request");
            using var request = new HttpRequestMessage(new HttpMethod(original.GetProperty("method").GetString()!),
                original.GetProperty("url").GetString());
            using var response = new HttpResponseMessage((HttpStatusCode)code.GetInt32()) { Content = new StreamContent(stream), RequestMessage = request };
            if (request.Method == HttpMethod.Get && request.RequestUri is { Scheme: "http" or "https" } address)
            {
                item.CookieContext = await GetCookiesForUrlAsync(address.AbsoluteUri);
                item.Cookies = item.CookieContext?.GetCookieHeader();
                item.UserAgent = SystemIntegration.HttpUserAgentService.CurrentUserAgent;
                item.Referer = null;
                if (original.TryGetProperty("headers", out var requestHeaders))
                {
                    foreach (var header in requestHeaders.EnumerateObject())
                    {
                        if (header.Name.Equals("Referer", StringComparison.OrdinalIgnoreCase)) item.Referer = header.Value.GetString();
                        if (header.Name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) item.UserAgent = header.Value.GetString();
                    }
                }
            }
            foreach (var header in headers.EnumerateArray())
            {
                string name = header.GetProperty("name").GetString()!;
                string value = header.GetProperty("value").GetString()!;
                if (!response.Headers.TryAddWithoutValidation(name, value)) response.Content.Headers.TryAddWithoutValidation(name, value);
            }
            // Chromium exposes decoded response bytes through its response stream.
            if (response.Content.Headers.ContentEncoding.Any())
            {
                response.Content.Headers.ContentEncoding.Clear();
                response.Content.Headers.ContentLength = null;
                response.Headers.Remove("Content-Digest");
                response.Headers.Remove("Repr-Digest");
            }
            ResponseTransferStarted?.Invoke();
            await _queue.OnBrowserResponseAsync(_windowId, response);
        }
        catch (Exception error)
        {
            AppLogger.Error("[Browser] Original response transfer failed.", error);
            if (claimed && !_isDisposed && _queue.GetWindowItem(_windowId) != null)
                _queue.OnWindowNavigationFailed(_windowId, error.Message);
            else if (!claimed && requestId != null && !_isDisposed)
            {
                try { await ContinueResponseAsync(requestId); } catch (Exception) { }
            }
        }
        finally
        {
            if (stream != null) await stream.DisposeAsync();
            if (claimed) _responseItem = null;
        }
    }

    private Task<string> ContinueResponseAsync(string requestId)
        => _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Fetch.continueRequest", JsonSerializer.Serialize(new { requestId }));

    private static bool IsDownloadResponse(JsonElement headers)
    {
        foreach (var header in headers.EnumerateArray())
        {
            string? name = header.GetProperty("name").GetString();
            string value = header.GetProperty("value").GetString() ?? "";
            if (name?.Equals("Content-Disposition", StringComparison.OrdinalIgnoreCase) == true &&
                value.Split(';')[0].Trim().Equals("attachment", StringComparison.OrdinalIgnoreCase)) return true;
            if (name?.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) == true)
            {
                string mime = value.Split(';')[0].Trim().ToLowerInvariant();
                if (mime.StartsWith("application/", StringComparison.Ordinal) && mime is not
                    ("application/pdf" or "application/json" or "application/ld+json" or "application/xml" or
                     "application/xhtml+xml" or "application/javascript" or "application/ecmascript") &&
                    !mime.EndsWith("+json", StringComparison.Ordinal) && !mime.EndsWith("+xml", StringComparison.Ordinal)) return true;
            }
        }
        return false;
    }

    private void StopResponseTransfers()
    {
        if (_responseReceiver != null) _responseReceiver.DevToolsProtocolEventReceived -= OnResponsePaused;
        if (_responseItem != null) _ = DownloadEngine.Instance.CancelOrPauseDownload(_responseItem.Id);
    }
}
