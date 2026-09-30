using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Reepax.Services.AdBlock;
using Reepax.Services.AdBlock.Native;
using Xunit;

namespace Reepax.Tests;

public class AdBlockEngineTests
{
    [Fact]
    public async Task UpdateFilterListsAsync_WhenDisposedDuringNativeCompilation_FreesNewPointerAndDoesNotRetain()
    {
        if (!AdBlockRustNative.IsAvailable)
        {
            return;
        }

        var engine = new AdBlockRustEngine();
        var freedPointers = new List<IntPtr>();
        var lockObj = new object();

        engine.OnNativeEngineFreedForTesting = ptr =>
        {
            lock (lockObj)
            {
                freedPointers.Add(ptr);
            }
        };

        engine.FilterListFetcherForTesting = _ => Task.FromResult<string?>("||adservice.network/banner/*\n||analytics-tracker.org^");

        IntPtr capturedCompiledPtr = IntPtr.Zero;
        engine.OnEngineCompiledForTesting = ptr =>
        {
            capturedCompiledPtr = ptr;
            // Simulate Dispose() being invoked while background compilation finishes
            // before acquiring lock in UpdateFilterListsAsync
            engine.Dispose();
        };

        var result = await engine.UpdateFilterListsAsync();

        Assert.Equal(FilterUpdateResult.Failed, result);
        Assert.NotEqual(IntPtr.Zero, capturedCompiledPtr);
        Assert.True(engine.IsDisposed);
        Assert.Equal(IntPtr.Zero, engine.EngineHandle);

        lock (lockObj)
        {
            Assert.Contains(capturedCompiledPtr, freedPointers);
        }
    }

    [Fact]
    public async Task UpdateFilterListsAsync_WhenAlreadyDisposed_ReturnsFailedImmediatelyWithoutCompiling()
    {
        var engine = new AdBlockRustEngine();
        engine.Dispose();

        bool compilationInvoked = false;
        engine.OnEngineCompiledForTesting = _ => compilationInvoked = true;
        engine.FilterListFetcherForTesting = _ => Task.FromResult<string?>("||sample-ad.com^");

        var result = await engine.UpdateFilterListsAsync();

        Assert.Equal(FilterUpdateResult.Failed, result);
        Assert.False(compilationInvoked);
        Assert.True(engine.IsDisposed);
        Assert.Equal(IntPtr.Zero, engine.EngineHandle);
    }

    [Fact]
    public async Task UpdateFilterListsAsync_ConcurrentDisposeAndApply_LeavesCleanZeroHandle()
    {
        if (!AdBlockRustNative.IsAvailable)
        {
            return;
        }

        for (int i = 0; i < 5; i++)
        {
            var engine = new AdBlockRustEngine();
            var freedPointers = new List<IntPtr>();
            var lockObj = new object();

            engine.OnNativeEngineFreedForTesting = ptr =>
            {
                lock (lockObj)
                {
                    freedPointers.Add(ptr);
                }
            };

            engine.FilterListFetcherForTesting = async ct =>
            {
                await Task.Delay(10, ct);
                return "||test-network-ad.org^\n||telemetry-collector.com^";
            };

            var updateTask = engine.UpdateFilterListsAsync();
            var disposeTask = Task.Run(async () =>
            {
                await Task.Delay(5);
                engine.Dispose();
            });

            await Task.WhenAll(updateTask, disposeTask);

            Assert.True(engine.IsDisposed);
            Assert.Equal(IntPtr.Zero, engine.EngineHandle);
            Assert.False(engine.IsNativeAvailable);
        }
    }

    [Fact]
    public void DisposedEngine_OperationsSafelyFallbackWithoutCrash()
    {
        var engine = new AdBlockRustEngine();
        engine.Dispose();

        Assert.True(engine.IsDisposed);
        Assert.Equal(IntPtr.Zero, engine.EngineHandle);
        Assert.False(engine.IsNativeAvailable);

        var blocked = engine.ShouldBlock("https://doubleclick.net/ad.js", "https://example.com", "script", out var isRedirect);
        Assert.False(blocked);
        Assert.False(isRedirect);

        var (css, script) = engine.GetCosmeticResources("https://example.com");
        Assert.NotNull(css);
        Assert.NotNull(script);
    }

    [Fact]
    public void MultipleDisposeCalls_IdempotentAndSafe()
    {
        var engine = new AdBlockRustEngine();
        engine.Dispose();
        engine.Dispose();
        engine.Dispose();

        Assert.True(engine.IsDisposed);
        Assert.Equal(IntPtr.Zero, engine.EngineHandle);
    }
}
