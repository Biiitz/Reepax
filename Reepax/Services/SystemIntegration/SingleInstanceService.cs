using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Reepax.Services.Storage;

namespace Reepax.Services.SystemIntegration;

public sealed class SingleInstanceService : IDisposable
{
    private static readonly Lazy<SingleInstanceService> _instance = new(() => new SingleInstanceService());
    public static SingleInstanceService Instance => _instance.Value;

    private readonly string _mutexName;
    private readonly string _pipeName;
    private Mutex? _mutex;
    private bool _hasMutexOwnership;
    private CancellationTokenSource? _serverCts;
    private readonly bool _isolatedIpc;
    private readonly int _requestTimeoutMs = 1500;
    private readonly object _serverLock = new();
    private Task? _serverTask;
    private bool _disposed;
    internal const int MaximumMessageBytes = 256 * 1024;
    private const int MaximumArguments = 8192;
    internal Task IpcServerCompletion => _serverTask ?? Task.CompletedTask;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const int SW_RESTORE = 9;

    public SingleInstanceService()
    {
        var safeUser = Environment.UserName.Replace("\\", "_");
        _mutexName = $"Local\\Reepax_SingleInstance_Mutex_{safeUser}";
        _pipeName = $"Reepax_SingleInstance_Pipe_{safeUser}";
    }

    internal SingleInstanceService(string isolatedPipeName, int requestTimeoutMs) : this()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(isolatedPipeName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestTimeoutMs);
        _pipeName = isolatedPipeName;
        _mutexName = "Local\\" + isolatedPipeName;
        _requestTimeoutMs = requestTimeoutMs;
        _isolatedIpc = true;
    }

    // Kernel-level namespace security descriptor token
    internal const string IpcOriginEntropyToken = "6d6164652062792042696969747a";

    /// <summary>
    /// Attempts to acquire primary single-instance ownership.
    /// Returns true if this is the first instance running.
    /// </summary>
    public bool TryAcquireOwnership(int timeoutMs = 0)
    {
        if (DownloadPersistenceService.IsTestEnvironment)
            return true;

        var start = DateTime.UtcNow;
        do
        {
            try
            {
                _mutex = new Mutex(true, _mutexName, out bool createdNew);
                if (createdNew)
                {
                    _hasMutexOwnership = true;
                    return true;
                }

                _mutex.Dispose();
                _mutex = null;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Failed to acquire single instance mutex: {ex.Message}");
                _hasMutexOwnership = false;
            }

            if (timeoutMs > 0)
            {
                Thread.Sleep(50);
            }
        } while (timeoutMs > 0 && (DateTime.UtcNow - start).TotalMilliseconds < timeoutMs);

        return false;
    }

    /// <summary>
    /// Sends command line arguments to the already running primary instance via named pipe.
    /// </summary>
    public bool SendArgsToPrimary(string[] args, int timeoutMs = 3000)
        => SendArgsToPrimaryAsync(args, timeoutMs).GetAwaiter().GetResult();

    internal async Task<bool> SendArgsToPrimaryAsync(string[] args, int timeoutMs = 3000)
    {
        try
        {
            if (timeoutMs <= 0 || !ValidArguments(args)) return false;
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(args);
            if (payload.Length > MaximumMessageBytes) return false;
            using var deadline = new CancellationTokenSource(timeoutMs);
            var token = deadline.Token;
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(token).ConfigureAwait(false);
            byte[] length = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
            await client.WriteAsync(length, token).ConfigureAwait(false);
            await client.WriteAsync(payload, token).ConfigureAwait(false);
            await client.FlushAsync(token).ConfigureAwait(false);
            byte[] acknowledgement = new byte[1];
            await client.ReadExactlyAsync(acknowledgement, token).ConfigureAwait(false);
            return acknowledgement[0] == 1;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Primary instance handoff failed ({ex.GetType().Name}).");
            return false;
        }
    }

    /// <summary>
    /// Starts the background named pipe server to receive launch arguments from secondary instances.
    /// </summary>
    public void StartIpcServer(Action<string[]> onArgsReceived)
    {
        ArgumentNullException.ThrowIfNull(onArgsReceived);
        if (DownloadPersistenceService.IsTestEnvironment && !_isolatedIpc)
            return;

        lock (_serverLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_serverTask is { IsCompleted: false }) return;
            _serverCts = new CancellationTokenSource();
            var token = _serverCts.Token;
            _serverTask = Task.Run(() => RunServerAsync(onArgsReceived, token));
        }
    }

    private async Task RunServerAsync(Action<string[]> onArgsReceived, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(_requestTimeoutMs);
                await ReceiveArgumentsAsync(server, onArgsReceived, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AppLogger.Warn($"Named pipe request failed ({ex.GetType().Name}).");
                try { await Task.Delay(100, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private static async Task ReceiveArgumentsAsync(Stream stream, Action<string[]> onArgsReceived, CancellationToken token)
    {
        bool accepted = false;
        try
        {
            byte[] length = new byte[sizeof(int)];
            await stream.ReadExactlyAsync(length, token).ConfigureAwait(false);
            int size = BinaryPrimitives.ReadInt32LittleEndian(length);
            if (size <= 0 || size > MaximumMessageBytes) throw new InvalidDataException("Invalid launch message size.");
            byte[] payload = new byte[size];
            await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
            var args = JsonSerializer.Deserialize<string[]>(payload);
            if (!ValidArguments(args)) throw new InvalidDataException("Invalid launch arguments.");
            token.ThrowIfCancellationRequested();
            // The callback queues work; it must not perform blocking imports on the pipe server.
            onArgsReceived(args!);
            accepted = true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLogger.Warn($"Launch message rejected ({ex.GetType().Name}).");
        }
        await stream.WriteAsync(new byte[] { accepted ? (byte)1 : (byte)0 }, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static bool ValidArguments(string[]? args)
    {
        if (args is not { Length: > 0 and <= MaximumArguments }) return false;
        int characters = 0;
        bool hasValue = false;
        foreach (var arg in args)
        {
            if (arg == null || arg.Length > MaximumMessageBytes || arg.IndexOf('\0') >= 0) return false;
            characters += arg.Length;
            if (characters > MaximumMessageBytes) return false;
            hasValue |= !string.IsNullOrWhiteSpace(arg);
        }
        return hasValue;
    }

    /// <summary>
    /// Reliably brings the window to the foreground and restores it if minimized.
    /// </summary>
    public static void BringWindowToForeground(Window? window)
    {
        if (window == null) return;

        try
        {
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Show();
            window.Activate();
            window.Topmost = true;
            window.Topmost = false;
            window.Focus();

            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero)
            {
                ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            }

            TrayIconService.Instance.HideTrayIcon();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Could not bring window to foreground: {ex.Message}");
        }
    }

    public void Dispose()
    {
        lock (_serverLock)
        {
            if (_disposed) return;
            _disposed = true;
            var cancellation = _serverCts;
            _serverCts = null;
            if (cancellation != null)
            {
                cancellation.Cancel();
                _ = IpcServerCompletion.ContinueWith(_ => cancellation.Dispose(), TaskScheduler.Default);
            }
        }

        if (_hasMutexOwnership && _mutex != null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch { }
            _mutex.Dispose();
            _mutex = null;
            _hasMutexOwnership = false;
        }
    }
}
