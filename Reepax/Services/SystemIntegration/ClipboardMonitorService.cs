using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Reepax.Models;
using Reepax.Services.Extractor;
using Reepax.Services.Storage;

namespace Reepax.Services.SystemIntegration;

/// <summary>
/// Monitors Windows clipboard for copied download links (including HTML fragments,
/// href tags/attributes, markdown, and plain text) using the modern, non-breaking
/// Win32 AddClipboardFormatListener API.
/// </summary>
public sealed class ClipboardMonitorService : IDisposable
{
    private static readonly Lazy<ClipboardMonitorService> _instance = new(() => new ClipboardMonitorService());
    public static ClipboardMonitorService Instance => _instance.Value;

    public const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private IntPtr _hwnd = IntPtr.Zero;
    private bool _isListening;
    private Action<List<string>>? _onLinksDetected;
    private static string? _lastInternalCopiedHash;
    private static DateTime _lastInternalCopiedTime = DateTime.MinValue;
    private string? _lastProcessedUrlsKey;
    private readonly object _lock = new();

    public static bool IsTestEnvironment { get; set; } = DownloadPersistenceService.IsTestEnvironment;

    public bool IsListening => _isListening;

    private ClipboardMonitorService() { }

    /// <summary>
    /// Attaches the native Windows clipboard format listener to the specified window handle.
    /// </summary>
    public void Start(IntPtr hwnd, Action<List<string>> onLinksDetected)
    {
        lock (_lock)
        {
            _onLinksDetected = onLinksDetected;
            if (hwnd == IntPtr.Zero || _isListening) return;

            _hwnd = hwnd;
            if (!IsTestEnvironment && OperatingSystem.IsWindows())
            {
                try
                {
                    if (AddClipboardFormatListener(_hwnd))
                    {
                        _isListening = true;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"[ClipboardMonitor] Failed to attach AddClipboardFormatListener: {ex.Message}");
                }
            }
            else
            {
                _isListening = true;
            }
        }
    }

    /// <summary>
    /// Detaches the native Windows clipboard format listener.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (!_isListening) return;

            if (_hwnd != IntPtr.Zero && !IsTestEnvironment && OperatingSystem.IsWindows())
            {
                try
                {
                    RemoveClipboardFormatListener(_hwnd);
                }
                catch { }
            }

            _hwnd = IntPtr.Zero;
            _isListening = false;
        }
    }

    /// <summary>
    /// Registers text that was placed onto the clipboard by Reepax itself (e.g. Copy Link, Copy Path),
    /// preventing the clipboard monitor from self-triggering.
    /// </summary>
    public static void RegisterInternalCopy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _lastInternalCopiedHash = ComputeTextHash(text);
        _lastInternalCopiedTime = DateTime.UtcNow;
    }

    /// <summary>
    /// Checks whether the supplied text matches a recent internal copy operation.
    /// </summary>
    public static bool IsInternalCopy(string? text)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(_lastInternalCopiedHash))
            return false;

        // Internal copy suppression expires after 3 seconds
        if ((DateTime.UtcNow - _lastInternalCopiedTime).TotalSeconds > 3)
        {
            _lastInternalCopiedHash = null;
            return false;
        }

        return string.Equals(_lastInternalCopiedHash, ComputeTextHash(text), StringComparison.Ordinal);
    }

    /// <summary>
    /// Processes a clipboard update message (WM_CLIPBOARDUPDATE) safely on the UI dispatcher.
    /// </summary>
    public void ProcessClipboardChange()
    {
        if (!SettingsService.Instance.Settings.EnableClipboardMonitor)
            return;

        // Run with a short debounce to allow the copying application to finish writing all clipboard formats
        _ = Task.Run(async () =>
        {
            await Task.Delay(40).ConfigureAwait(false);

            if (Application.Current?.Dispatcher == null || Application.Current.Dispatcher.HasShutdownStarted)
                return;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (!SettingsService.Instance.Settings.EnableClipboardMonitor)
                        return;

                    var links = ExtractLinksFromClipboard();
                    if (links.Count == 0)
                        return;

                    var key = string.Join("|", links);
                    if (string.Equals(_lastProcessedUrlsKey, key, StringComparison.OrdinalIgnoreCase))
                    {
                        // Duplicate trigger for the same links
                        return;
                    }

                    _lastProcessedUrlsKey = key;
                    _onLinksDetected?.Invoke(links);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"[ClipboardMonitor] Error processing clipboard: {ex.Message}");
                }
            });
        });
    }

    /// <summary>
    /// Safely extracts all valid URLs from the Windows clipboard, checking both HTML and plain text formats.
    /// Retries if the clipboard is temporarily locked by another application.
    /// </summary>
    public static List<string> ExtractLinksFromClipboard(int maxRetries = 3)
    {
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                string? html = null;
                string? text = null;

                if (Clipboard.ContainsData(DataFormats.Html))
                {
                    html = Clipboard.GetData(DataFormats.Html) as string;
                }

                if (Clipboard.ContainsText())
                {
                    text = Clipboard.GetText();
                }

                // If this matches an internal Reepax copy operation, suppress
                if (IsInternalCopy(text) || IsInternalCopy(html))
                {
                    return new List<string>();
                }

                return ExtractLinksFromData(html, text);
            }
            catch (COMException) when (attempt < maxRetries - 1)
            {
                // CLIPBRD_E_CANT_OPEN: another process holds the clipboard momentarily
                Thread.Sleep(30);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[ClipboardMonitor] Clipboard read failed: {ex.Message}");
                break;
            }
        }

        return new List<string>();
    }

    /// <summary>
    /// Pure helper to extract and deduplicate all valid links from HTML and text representations.
    /// </summary>
    public static List<string> ExtractLinksFromData(string? html, string? text)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        void AddLinks(IEnumerable<ExtractedLink> extracted)
        {
            foreach (var link in extracted)
            {
                if (!string.IsNullOrWhiteSpace(link.Url) &&
                    HosterInfo.IsFileHosterUrl(link.Url) &&
                    seen.Add(link.Url))
                {
                    result.Add(link.Url);
                }
            }
        }

        // 1. Extract from HTML (captures href="..." targets even when display text is plain name)
        if (!string.IsNullOrWhiteSpace(html))
        {
            AddLinks(LinkExtractor.ExtractLinks(html, fileHostersOnly: true));
        }

        // 2. Extract from plain text (captures raw URLs, href: text lines, etc.)
        if (!string.IsNullOrWhiteSpace(text))
        {
            AddLinks(LinkExtractor.ExtractLinks(text, fileHostersOnly: true));
        }

        return result;
    }

    private static string ComputeTextHash(string text)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash);
    }

    public void Dispose()
    {
        Stop();
    }
}
