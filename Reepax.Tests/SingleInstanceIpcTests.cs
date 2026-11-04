using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using Reepax.Services.SystemIntegration;
using Xunit;

namespace Reepax.Tests;

public sealed class SingleInstanceIpcTests : IAsyncLifetime
{
    private readonly List<SingleInstanceService> _services = new();

    [Fact]
    public async Task StalledClient_DoesNotPreventTheNextArgumentHandoff()
    {
        var service = CreateService(out var pipe, requestTimeoutMs: 250);
        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.StartIpcServer(args => received.TrySetResult(args));
        using var stalled = new NamedPipeClientStream(".", pipe, PipeDirection.Out, PipeOptions.Asynchronous);
        await stalled.ConnectAsync(5000);
        bool sent = await Task.Run(() => service.SendArgsToPrimary(["--activate"], timeoutMs: 2000));
        Assert.True(sent);
        Assert.Equal(new[] { "--activate" }, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialMessage_ExpiresAndTheNextHandoffSucceeds(bool partialBody)
    {
        var service = CreateService(out var pipe, requestTimeoutMs: 250);
        var received = new List<string[]>();
        service.StartIpcServer(received.Add);
        using var stalled = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await stalled.ConnectAsync(5000);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, 100);
        await stalled.WriteAsync(partialBody ? prefix : prefix[..2], limit.Token);
        if (partialBody) await stalled.WriteAsync(new byte[] { (byte)'[' }, limit.Token);
        Assert.True(await service.SendArgsToPrimaryAsync(["sample.repx"], timeoutMs: 2000));
        Assert.Equal(new[] { "sample.repx" }, Assert.Single(received));
    }

    [Fact]
    public async Task Arguments_RoundTripWithoutSplittingUnicodeQuotesOrNewlines()
    {
        var service = CreateService(out _);
        string[] expected = ["--activate", "sample file.repx", "\u00e4\u4e2d.repx", "quoted\"value", "line\nvalue"];
        var received = new List<string[]>();
        service.StartIpcServer(received.Add);
        Assert.True(await service.SendArgsToPrimaryAsync(expected));
        Assert.Equal(expected, Assert.Single(received));
    }

    [Fact]
    public async Task LongUnicodeArguments_FitTheBoundedProtocol()
    {
        var service = CreateService(out _);
        string[] expected = [new string('\u4e2d', 32000)];
        var received = new List<string[]>();
        service.StartIpcServer(received.Add);
        Assert.True(await service.SendArgsToPrimaryAsync(expected));
        Assert.Equal(expected, Assert.Single(received));
    }

    [Fact]
    public async Task ConcurrentLaunches_AreEachAcceptedExactlyOnce()
    {
        var service = CreateService(out _);
        var received = new List<string>();
        service.StartIpcServer(args => received.AddRange(args));
        string[] expected = Enumerable.Range(0, 8).Select(index => $"sample-{index}.repx").ToArray();
        bool[] accepted = await Task.WhenAll(expected.Select(arg => service.SendArgsToPrimaryAsync([arg])));
        Assert.All(accepted, Assert.True);
        Assert.Equal(expected.OrderBy(arg => arg, StringComparer.Ordinal), received.OrderBy(arg => arg, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(24)]
    public async Task ConcurrentLaunches_WithLimitedWorkerThreads_AreEachAcceptedExactlyOnce(int count)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { typeof(SingleInstanceIpcTests).Assembly.Location, "--ipc-contention-worker", count.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The isolated IPC worker did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(process.ExitCode == 0, "IPC worker failed: " + await error + await output);
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(output, error);
        }
    }

    [Fact]
    public async Task CompleteFrame_IsAcknowledgedWithoutWaitingForTheClientToClose()
    {
        var service = CreateService(out var pipe);
        var received = new List<string[]>();
        service.StartIpcServer(received.Add);
        using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(limit.Token);
        await WriteFrameAsync(client, Encoding.UTF8.GetBytes("[\"sample.repx\"]"), limit.Token);
        Assert.Equal(1, await ReadAcknowledgementAsync(client, limit.Token));
        Assert.True(client.IsConnected);
        Assert.Equal(new[] { "sample.repx" }, Assert.Single(received));
    }

    [Theory]
    [InlineData("oversized")]
    [InlineData("negative")]
    [InlineData("invalid-json")]
    [InlineData("null-element")]
    [InlineData("empty-array")]
    [InlineData("nested-array")]
    public async Task MalformedFrame_IsRejectedAndDoesNotStopTheServer(string fault)
    {
        var service = CreateService(out var pipe);
        var received = new List<string[]>();
        service.StartIpcServer(received.Add);
        using (var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous))
        using (var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            await client.ConnectAsync(limit.Token);
            if (fault is "oversized" or "negative")
            {
                byte[] prefix = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(prefix, fault == "oversized" ? SingleInstanceService.MaximumMessageBytes + 1 : -1);
                await client.WriteAsync(prefix, limit.Token);
            }
            else
            {
                string json = fault switch
                {
                    "null-element" => "[null]",
                    "empty-array" => "[]",
                    "nested-array" => "[[\"nested\"]]",
                    _ => "{broken"
                };
                await WriteFrameAsync(client, Encoding.UTF8.GetBytes(json), limit.Token);
            }
            Assert.Equal(0, await ReadAcknowledgementAsync(client, limit.Token));
        }
        Assert.Empty(received);
        Assert.True(await service.SendArgsToPrimaryAsync(["--activate"]));
        Assert.Single(received);
    }

    [Fact]
    public async Task RejectedCallback_IsReportedToTheSenderAndLaterRequestsStillWork()
    {
        var service = CreateService(out _);
        int calls = 0;
        service.StartIpcServer(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("Synthetic rejection.");
        });
        Assert.False(await service.SendArgsToPrimaryAsync(["sample.repx"]));
        Assert.True(await service.SendArgsToPrimaryAsync(["--activate"]));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnresponsivePeer_HasABoundedSendDeadline(bool largePayload)
    {
        var service = CreateService(out var pipe);
        using var receiver = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, inBufferSize: 128, outBufferSize: 128);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connection = receiver.WaitForConnectionAsync(limit.Token);
        var timer = Stopwatch.StartNew();
        var send = service.SendArgsToPrimaryAsync([largePayload ? new string('x', 128 * 1024) : "--activate"], timeoutMs: 200);
        await connection;
        Assert.False(await send.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task MissingPrimary_HasABoundedConnectionDeadline()
    {
        var service = CreateService(out _);
        Assert.False(await service.SendArgsToPrimaryAsync(["--activate"], timeoutMs: 150).WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("null")]
    [InlineData("many")]
    [InlineData("long")]
    [InlineData("escaped")]
    public async Task InvalidOutgoingArguments_AreRejectedBeforeDelivery(string fault)
    {
        var service = CreateService(out _);
        var received = new List<string[]>();
        service.StartIpcServer(received.Add);
        string[] args = fault switch
        {
            "empty" => [],
            "null" => [null!],
            "many" => Enumerable.Repeat("x", 8193).ToArray(),
            "long" => [new string('x', SingleInstanceService.MaximumMessageBytes + 1)],
            _ => [new string('\u4e2d', SingleInstanceService.MaximumMessageBytes / 4)]
        };
        Assert.False(await service.SendArgsToPrimaryAsync(args));
        Assert.Empty(received);
    }

    [Fact]
    public async Task RepeatedStartAndShutdown_DoNotLeaveAListenerBehind()
    {
        var service = CreateService(out var pipe);
        int first = 0, second = 0;
        service.StartIpcServer(_ => first++);
        service.StartIpcServer(_ => second++);
        Assert.True(await service.SendArgsToPrimaryAsync(["--activate"]));
        Assert.Equal(1, first);
        Assert.Equal(0, second);
        service.Dispose();
        await service.IpcServerCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        service.Dispose();
        var successor = new SingleInstanceService(pipe, requestTimeoutMs: 1000);
        _services.Add(successor);
        successor.StartIpcServer(_ => second++);
        Assert.True(await successor.SendArgsToPrimaryAsync(["--activate"]));
        Assert.Equal(1, second);
    }

    [Fact]
    public async Task Shutdown_CancelsAnIncompleteRequest()
    {
        var service = CreateService(out var pipe);
        int calls = 0;
        service.StartIpcServer(_ => calls++);
        using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        service.Dispose();
        await service.IpcServerCompletion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, calls);
    }

    private SingleInstanceService CreateService(out string pipe, int requestTimeoutMs = 1500)
    {
        pipe = "InstanceCase_" + Guid.NewGuid().ToString("N");
        var service = new SingleInstanceService(pipe, requestTimeoutMs);
        _services.Add(service);
        return service;
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken token)
    {
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, token);
        await stream.WriteAsync(payload, token);
        await stream.FlushAsync(token);
    }

    private static async Task<byte> ReadAcknowledgementAsync(Stream stream, CancellationToken token)
    {
        byte[] response = new byte[1];
        await stream.ReadExactlyAsync(response, token);
        return response[0];
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var service in _services) service.Dispose();
        await Task.WhenAll(_services.Select(service => service.IpcServerCompletion)).WaitAsync(TimeSpan.FromSeconds(5));
    }
}
