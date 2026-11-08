using Reepax.Services.SystemIntegration;
using Xunit;

namespace Reepax.Tests;

public sealed class ClipboardDispatchTests : IDisposable
{
    private const string First = "https://files.example.invalid/first.zip";
    private const string Second = "https://files.example.invalid/second.zip";
    private readonly bool _previousTestEnvironment = ClipboardMonitorService.IsTestEnvironment;
    private readonly ClipboardMonitorService _monitor =
        (ClipboardMonitorService)Activator.CreateInstance(typeof(ClipboardMonitorService), nonPublic: true)!;

    public ClipboardDispatchTests() => ClipboardMonitorService.IsTestEnvironment = true;

    [Fact]
    public void RejectedHandoff_CanBeRetriedAndPreservesPreviousDuplicateProtection()
    {
        bool acceptSecond = false;
        var received = new List<string>();
        bool Accept(List<string> links)
        {
            received.Add(Assert.Single(links));
            return links[0] != Second || acceptSecond;
        }
        _monitor.Start(new IntPtr(1), links => Accept(links));
        _monitor.DispatchDetectedLinks(new() { First });
        _monitor.DispatchDetectedLinks(new() { Second });
        _monitor.DispatchDetectedLinks(new());
        _monitor.DispatchDetectedLinks(new() { First });
        _monitor.DispatchDetectedLinks(new() { Second });
        acceptSecond = true;
        _monitor.DispatchDetectedLinks(new() { Second });
        _monitor.DispatchDetectedLinks(new() { Second });
        Assert.Equal(new[] { First, Second, Second, Second }, received);
    }

    [Fact]
    public void FailedHandoff_DoesNotPreventRetry()
    {
        int attempts = 0;
        bool Accept(List<string> links)
        {
            if (++attempts == 1) throw new InvalidOperationException("Sample handoff failure.");
            return true;
        }
        _monitor.Start(new IntPtr(1), links => Accept(links));
        Assert.Throws<InvalidOperationException>(() => _monitor.DispatchDetectedLinks(new() { First }));
        _monitor.DispatchDetectedLinks(new() { First });
        _monitor.DispatchDetectedLinks(new() { First });
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void NestedHandoff_SuppressesBurstsAndKeepsTheLatestAcceptedSet()
    {
        var received = new List<string>();
        bool Accept(List<string> links)
        {
            received.Add(Assert.Single(links));
            _monitor.DispatchDetectedLinks(new(links));
            if (links[0] == First) _monitor.DispatchDetectedLinks(new() { Second });
            return true;
        }
        _monitor.Start(new IntPtr(1), links => Accept(links));
        _monitor.DispatchDetectedLinks(new() { First });
        _monitor.DispatchDetectedLinks(new() { Second });
        Assert.Equal(new[] { First, Second }, received);
    }

    [Fact]
    public void RejectedHandoff_AfterListenerRestartCanBeAccepted()
    {
        bool accepted = false;
        int attempts = 0;
        bool Accept(List<string> links) { attempts++; return accepted; }
        _monitor.Start(new IntPtr(1), links => Accept(links));
        _monitor.DispatchDetectedLinks(new() { First });
        _monitor.Stop();
        Assert.False(_monitor.IsListening);
        accepted = true;
        _monitor.Start(new IntPtr(1), links => Accept(links));
        Assert.True(_monitor.IsListening);
        _monitor.DispatchDetectedLinks(new() { First });
        _monitor.DispatchDetectedLinks(new() { First });
        Assert.Equal(2, attempts);
    }

    public void Dispose()
    {
        _monitor.Dispose();
        ClipboardMonitorService.IsTestEnvironment = _previousTestEnvironment;
    }
}
