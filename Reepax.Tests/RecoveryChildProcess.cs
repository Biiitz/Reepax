using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using Xunit;

namespace Reepax.Tests;

internal sealed class RecoveryChildProcess : IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly Process _process;
    private readonly Task<string> _output;
    private readonly Task<string> _error;

    internal RecoveryChildProcess(string root, string mode, string boundary, string url, int connections)
    {
        string name = "ReepaxRecovery_" + Guid.NewGuid().ToString("N");
        _pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { typeof(RecoveryChildProcess).Assembly.Location, "--recovery-worker", root, name, mode, boundary, url, connections.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);
        try { _process = Process.Start(start) ?? throw new InvalidOperationException("Recovery worker did not start."); }
        catch { _pipe.Dispose(); throw; }
        _output = _process.StandardOutput.ReadToEndAsync();
        _error = _process.StandardError.ReadToEndAsync();
    }

    internal async Task<RecoveryProcessReport> ReceiveAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            await _pipe.WaitForConnectionAsync(timeout.Token);
            using var reader = new StreamReader(_pipe, leaveOpen: true);
            string line = await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("Recovery worker disconnected before reporting its state.");
            var report = JsonSerializer.Deserialize<RecoveryProcessReport>(line) ?? throw new IOException("Recovery worker returned no state.");
            Assert.Equal(_process.Id, report.ProcessId);
            Assert.NotEqual(Environment.ProcessId, report.ProcessId);
            return report;
        }
        catch (Exception ex)
        {
            await KillIfRunningAsync();
            throw new InvalidOperationException("Recovery worker failed: " + await _error + await _output, ex);
        }
    }

    internal async Task KillAtBoundaryAsync()
    {
        Assert.False(_process.HasExited, "Worker exited before the synchronized termination.");
        _process.Kill();
        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(0, _process.ExitCode);
    }

    internal async Task AssertExitedSuccessfullyAsync()
    {
        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(_process.ExitCode == 0, "Recovery worker failed: " + await _error + await _output);
    }

    private async Task KillIfRunningAsync()
    {
        if (_process.HasExited) return;
        try { _process.Kill(); }
        catch (InvalidOperationException) when (_process.HasExited) { }
        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await KillIfRunningAsync();
            await Task.WhenAll(_output, _error);
        }
        finally
        {
            _pipe.Dispose();
            _process.Dispose();
        }
    }
}
