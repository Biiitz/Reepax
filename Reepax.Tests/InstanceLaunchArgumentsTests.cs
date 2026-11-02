using System.IO;
using System.Text.Json;
using Reepax.Services.Localization;
using Reepax.Services.SystemIntegration;
using Xunit;

namespace Reepax.Tests;

public sealed class InstanceLaunchArgumentsTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ForwardLaunch_PreservesArgumentsAndReportsUnconfirmedRequests(bool accepted, bool empty)
    {
        string[] args = empty ? [] : ["sample.repx", "another file.repx"];
        string[]? forwarded = null;
        int notifications = 0, sends = 0;
        bool result = App.TryForwardLaunchArguments(args, value =>
        {
            sends++;
            forwarded = value;
            return accepted;
        }, () => notifications++);
        Assert.Equal(accepted, result);
        Assert.Equal(1, sends);
        Assert.Equal(accepted ? 0 : 1, notifications);
        Assert.Equal(empty ? new[] { "--activate" } : args, forwarded);
    }

    [Fact]
    public async Task PendingArguments_RemainQueuedUntilTheUiIsReady()
    {
        var pending = new PendingInstanceArguments();
        pending.Enqueue(["first.repx"]);
        await pending.DrainAsync(null);
        pending.Enqueue(["second.repx"]);
        var received = new List<string>();
        await pending.DrainAsync(args =>
        {
            received.AddRange(args);
            return Task.CompletedTask;
        });
        Assert.Equal(new[] { "first.repx", "second.repx" }, received);
    }

    [Fact]
    public async Task PendingImports_AreSerializedAndIncludeRequestsArrivingDuringAnImport()
    {
        var pending = new PendingInstanceArguments();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new List<string>();
        pending.Enqueue(["first.repx"]);
        var drain = pending.DrainAsync(async args =>
        {
            received.AddRange(args);
            if (received.Count == 1)
            {
                started.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        pending.Enqueue(["second.repx"]);
        await pending.DrainAsync(_ => throw new InvalidOperationException("A second import must not run concurrently."));
        Assert.Single(received);
        release.TrySetResult();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "first.repx", "second.repx" }, received);
    }

    [Fact]
    public async Task PendingQueue_IsBoundedAndAcceptsNewRequestsAfterDraining()
    {
        var pending = new PendingInstanceArguments();
        for (int index = 0; index < PendingInstanceArguments.Capacity; index++) pending.Enqueue(["sample.repx"]);
        Assert.Throws<InvalidOperationException>(() => pending.Enqueue(["extra.repx"]));
        int received = 0;
        await pending.DrainAsync(_ => { received++; return Task.CompletedTask; });
        Assert.Equal(PendingInstanceArguments.Capacity, received);
        pending.Enqueue(["extra.repx"]);
        await pending.DrainAsync(_ => { received++; return Task.CompletedTask; });
        Assert.Equal(PendingInstanceArguments.Capacity + 1, received);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void HandoffFailure_HasBothLocalizedTexts(string language)
    {
        var map = language == "de" ? Strings_de.Map : Strings_en.Map;
        Assert.False(string.IsNullOrWhiteSpace(map["Instance_HandoffFailed_Title"]));
        Assert.False(string.IsNullOrWhiteSpace(map["Instance_HandoffFailed_Message"]));
    }

    [Theory]
    [InlineData("de", false)]
    [InlineData("en", false)]
    [InlineData("de", true)]
    public void FailureLanguage_IsReadWithoutChangingSettingsOrCreatingStorage(string language, bool backup)
    {
        string root = Directory.CreateTempSubdirectory("HandoffLanguage_").FullName;
        try
        {
            string file = Path.Combine(root, backup ? "settings.json.bak" : "settings.json");
            string original = JsonSerializer.Serialize(new { Language = language });
            File.WriteAllText(file, original);
            var modified = File.GetLastWriteTimeUtc(file);
            Assert.Equal(language, App.ReadInstanceFailureLanguage(root, "fallback"));
            Assert.Equal(original, File.ReadAllText(file));
            Assert.Equal(modified, File.GetLastWriteTimeUtc(file));
            Assert.Single(Directory.GetFiles(root));
            Assert.Empty(Directory.GetDirectories(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailureLanguage_MissingOrUnreadableSettings_UsesFallback(bool invalid)
    {
        string root = Directory.CreateTempSubdirectory("HandoffLanguage_").FullName;
        try
        {
            if (invalid) File.WriteAllText(Path.Combine(root, "settings.json"), "invalid synthetic data");
            Assert.Equal("de", App.ReadInstanceFailureLanguage(root, "de"));
            Assert.Equal(invalid ? 1 : 0, Directory.GetFiles(root).Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exit_OnlyThePrimarySavesState_AndReleasesOwnershipLast(bool primary)
    {
        var calls = new List<string>();
        App.CompleteInstanceExit(primary, () => calls.Add("save"), () => calls.Add("release"));
        Assert.Equal(primary ? new[] { "save", "release" } : new[] { "release" }, calls);
    }

    [Fact]
    public void Exit_ReleasesOwnershipEvenWhenPrimaryCleanupFails()
    {
        bool released = false;
        Assert.Throws<IOException>(() => App.CompleteInstanceExit(true,
            () => throw new IOException("Synthetic shutdown failure."), () => released = true));
        Assert.True(released);
    }
}
