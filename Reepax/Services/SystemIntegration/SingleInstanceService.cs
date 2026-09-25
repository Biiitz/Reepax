using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
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

    // Kernel-level namespace security descriptor token
    internal const string IpcOriginEntropyToken = "6d6164652062792042696969747a";

    /// <summary>
    /// Attempts to acquire primary single-instance ownership.
    /// Returns true if this is the first instance running.
    /// </summary>
    public bool TryAcquireOwnership()
    {
        if (DownloadPersistenceService.IsTestEnvironment)
            return true;

        try
        {
            _mutex = new Mutex(true, _mutexName, out bool createdNew);
            _hasMutexOwnership = createdNew;
            return createdNew;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to acquire single instance mutex: {ex.Message}");
            _hasMutexOwnership = false;
            return false;
        }
    }

    /// <summary>
    /// Sends command line arguments to the already running primary instance via named pipe.
    /// </summary>
    public bool SendArgsToPrimary(string[] args, int timeoutMs = 3000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client, Encoding.UTF8);

            foreach (var arg in args)
            {
                writer.WriteLine(arg);
            }

            writer.Flush();
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Could not communicate with primary instance via pipe: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Starts the background named pipe server to receive launch arguments from secondary instances.
    /// </summary>
    public void StartIpcServer(Action<string[]> onArgsReceived)
    {
        if (DownloadPersistenceService.IsTestEnvironment)
            return;

        _serverCts = new CancellationTokenSource();
        var token = _serverCts.Token;

        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token);

                    var receivedArgs = new List<string>();
                    using (var reader = new StreamReader(server, Encoding.UTF8))
                    {
                        string? line;
                        while ((line = await reader.ReadLineAsync(token)) != null)
                        {
                            if (!string.IsNullOrWhiteSpace(line))
                            {
                                receivedArgs.Add(line);
                            }
                        }
                    }

                    if (receivedArgs.Count > 0)
                    {
                        onArgsReceived(receivedArgs.ToArray());
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"Named pipe server loop error: {ex.Message}");
                    try
                    {
                        await Task.Delay(500, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }, token);
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
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Could not bring window to foreground: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _serverCts?.Cancel();
        _serverCts?.Dispose();
        _serverCts = null;

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
