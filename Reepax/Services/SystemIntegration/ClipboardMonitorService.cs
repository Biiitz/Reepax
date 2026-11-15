using System;
using System.Collections.Generic;
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

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    private IntPtr _hwnd = IntPtr.Zero;
    private bool _isListening;
    private Func<List<string>, bool>? _onLinksDetected;
    private sealed record InternalCopy(uint Sequence, string Hash);
    private static InternalCopy? _lastInternalCopy;
    private uint _lastProcessedSequence;
    private readonly object _lock = new();

    public static bool IsTestEnvironment { get; set; } = DownloadPersistenceService.IsTestEnvironment;

    public bool IsListening => _isListening;

    private ClipboardMonitorService() { }

    /// <summary>
    /// Attaches the native Windows clipboard format listener to the specified window handle.
    /// </summary>
    public void Start(IntPtr hwnd, Func<List<string>, bool> onLinksDetected)
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
                    AppLogger.Warn($"[ClipboardMonitor] Failed to attach AddClipboardFormatListener", ex);
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
    public static void RegisterInternalCopy(string? text) => RegisterInternalCopy(text, GetClipboardSequenceNumber());

    internal static void RegisterInternalCopy(string? text, uint sequence)
    {
        if (sequence == 0 || string.IsNullOrEmpty(text)) return;
        Volatile.Write(ref _lastInternalCopy, new InternalCopy(sequence, ComputeTextHash(text)));
    }

    /// <summary>
    /// Checks whether this clipboard change came from an internal copy operation.
    /// </summary>
    public static bool IsInternalCopy(string? text) => IsInternalCopy(text, GetClipboardSequenceNumber());

    internal static bool IsInternalCopy(string? text, uint sequence) =>
        !string.IsNullOrEmpty(text) && Volatile.Read(ref _lastInternalCopy) is { } copy &&
        sequence == copy.Sequence && string.Equals(copy.Hash, ComputeTextHash(text), StringComparison.Ordinal);

    /// <summary>
    /// Processes a clipboard update message (WM_CLIPBOARDUPDATE) safely on the UI dispatcher.
    /// </summary>
    public void ProcessClipboardChange()
    {
        if (!_isListening || !SettingsService.Instance.Settings.EnableClipboardMonitor)
            return;

        // Run with a short debounce to allow the copying application to finish writing all clipboard formats
        _ = Task.Run(async () =>
        {
            await Task.Delay(40).ConfigureAwait(false);

            if (Application.Current?.Dispatcher == null || Application.Current.Dispatcher.HasShutdownStarted)
                return;

            await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    if (!_isListening || !SettingsService.Instance.Settings.EnableClipboardMonitor)
                        return;

                    var (sequence, links) = await ReadClipboardLinksAsync(GetClipboardSequenceNumber, ReadClipboardData);
                    if (SettingsService.Instance.Settings.EnableClipboardMonitor && sequence == GetClipboardSequenceNumber())
                        DispatchDetectedLinks(links, sequence);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"[ClipboardMonitor] Error processing clipboard", ex);
                }
            });
        });
    }

    internal void DispatchDetectedLinks(List<string> links, uint sequence)
    {
        if (!_isListening || sequence == 0 || sequence == _lastProcessedSequence || links.Count == 0)
            return;

        if (_onLinksDetected is not { } handler) return;

        uint previous = _lastProcessedSequence;
        // Reserve during modal reentrancy; rejected handoffs must remain retryable.
        _lastProcessedSequence = sequence;
        bool accepted = false;
        try { accepted = handler(links); }
        finally
        {
            if (!accepted && _lastProcessedSequence == sequence)
                _lastProcessedSequence = previous;
        }
    }

    /// <summary>
    /// Safely extracts all valid URLs from the Windows clipboard, checking both HTML and plain text formats.
    /// Retries asynchronously if the clipboard is temporarily locked by another application without blocking the UI dispatcher.
    /// </summary>
    public static async Task<List<string>> ExtractLinksFromClipboardAsync(int maxRetries = 3) =>
        (await ReadClipboardLinksAsync(GetClipboardSequenceNumber, ReadClipboardData, maxRetries)).Links;

    internal static async Task<(uint Sequence, List<string> Links)> ReadClipboardLinksAsync(
        Func<uint> getSequence, Func<(string? Html, string? Text)> readData, int maxRetries = 3)
    {
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                if (TryReadClipboardLinks(getSequence, readData) is { } snapshot)
                    return snapshot;
            }
            catch (COMException) when (attempt < maxRetries - 1)
            {
                // Another process may temporarily hold the clipboard.
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[ClipboardMonitor] Clipboard read failed", ex);
                break;
            }
            if (attempt < maxRetries - 1) await Task.Delay(30);
        }

        return (0, []);
    }

    /// <summary>
    /// Synchronous fallback for clipboard extraction.
    /// </summary>
    public static List<string> ExtractLinksFromClipboard(int maxRetries = 3)
    {
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                if (TryReadClipboardLinks(GetClipboardSequenceNumber, ReadClipboardData) is { } snapshot)
                    return snapshot.Links;
            }
            catch (COMException) when (attempt < maxRetries - 1)
            {
                // Another process may temporarily hold the clipboard.
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[ClipboardMonitor] Clipboard read failed", ex);
                break;
            }
            if (attempt < maxRetries - 1) Thread.Sleep(30);
        }

        return [];
    }

    private static (string? Html, string? Text) ReadClipboardData()
    {
        var data = Clipboard.GetDataObject();
        return (data?.GetData(DataFormats.Html) as string, data?.GetData(DataFormats.UnicodeText) as string);
    }

    private static (uint Sequence, List<string> Links)? TryReadClipboardLinks(
        Func<uint> getSequence, Func<(string? Html, string? Text)> readData)
    {
        uint sequence = getSequence();
        if (sequence == 0) return (0, []);

        var (html, text) = readData();
        // Rendering a delayed format can also change the sequence during a read.
        if (sequence != getSequence()) return null;

        return (sequence, IsInternalCopy(text, sequence) || IsInternalCopy(html, sequence)
            ? [] : ExtractLinksFromData(html, text));
    }

    /// <summary>
    /// Pure helper to extract and deduplicate all valid links from HTML and text representations.
    /// </summary>
    public static List<string> ExtractLinksFromData(string? html, string? text)
    {
        var seen = new HashSet<string>(DownloadUrlComparer.Instance);
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
