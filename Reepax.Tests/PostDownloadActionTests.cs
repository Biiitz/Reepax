using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;
using Xunit;

namespace Reepax.Tests;

[Collection("PostDownloadActionTests")]
public class PostDownloadActionTests : IDisposable
{
    public PostDownloadActionTests()
    {
        // Ensure test environment is flagged
        DownloadPersistenceService.IsTestEnvironment = true;
        PostDownloadActionService.Instance.CancelCountdown(manual: true);
        PostDownloadActionService.Instance.CurrentAction = PostDownloadAction.None;
    }

    public void Dispose()
    {
        PostDownloadActionService.Instance.CancelCountdown(manual: true);
        PostDownloadActionService.Instance.CurrentAction = PostDownloadAction.None;
    }

    [Fact]
    public void AppSettings_PostDownloadAction_DefaultValue_IsNone()
    {
        var settings = new AppSettings();
        Assert.Equal(PostDownloadAction.None, settings.PostDownloadAction);
    }

    [Fact]
    public void AppSettings_PostDownloadAction_JsonRoundTrip()
    {
        var original = new AppSettings
        {
            PostDownloadAction = PostDownloadAction.Shutdown
        };

        string json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(PostDownloadAction.Shutdown, deserialized.PostDownloadAction);
    }

    [Fact]
    public void PostDownloadAction_CheckHasActiveWork_RunningOrQueuedItems_ReturnsTrue()
    {
        var pkg = new DownloadPackage
        {
            Id = Guid.NewGuid(),
            Name = "Test Package",
            Status = DownloadStatus.Downloading,
            IsEnabled = true
        };
        var item = new DownloadItem
        {
            Id = Guid.NewGuid(),
            FileName = "file.part1.rar",
            Status = DownloadStatus.Downloading,
            IsEnabled = true
        };
        pkg.Items.Add(item);

        var packages = new List<DownloadPackage> { pkg };
        bool hasActive = PostDownloadActionService.CheckHasActiveWork(packages);

        Assert.True(hasActive);
    }

    [Fact]
    public void PostDownloadAction_CheckHasActiveWork_PausedItems_ReturnsFalse()
    {
        // User scenario: e.g. 2 downloads, 1 running and 1 paused.
        // Once the running one completes, only the paused one remains.
        // Paused items must NOT be counted as active work!
        var pkgCompleted = new DownloadPackage
        {
            Id = Guid.NewGuid(),
            Name = "Finished Package",
            Status = DownloadStatus.Completed,
            IsEnabled = true
        };
        var itemCompleted = new DownloadItem
        {
            Id = Guid.NewGuid(),
            FileName = "game.iso",
            Status = DownloadStatus.Completed,
            IsEnabled = true,
            TotalBytes = 1000,
            DownloadedBytes = 1000
        };
        pkgCompleted.Items.Add(itemCompleted);

        var pkgPaused = new DownloadPackage
        {
            Id = Guid.NewGuid(),
            Name = "Paused Package",
            Status = DownloadStatus.Paused,
            IsEnabled = true
        };
        var itemPaused = new DownloadItem
        {
            Id = Guid.NewGuid(),
            FileName = "other.iso",
            Status = DownloadStatus.Paused,
            IsEnabled = true
        };
        pkgPaused.Items.Add(itemPaused);

        var packages = new List<DownloadPackage> { pkgCompleted, pkgPaused };
        bool hasActive = PostDownloadActionService.CheckHasActiveWork(packages);

        Assert.False(hasActive);
    }

    [Fact]
    public void PostDownloadAction_CheckHasActiveWork_ExtractingPackage_ReturnsTrue()
    {
        var pkg = new DownloadPackage
        {
            Id = Guid.NewGuid(),
            Name = "Extracting Package",
            Status = DownloadStatus.Completed,
            IsExtracting = true,
            IsEnabled = true
        };
        var item = new DownloadItem
        {
            Id = Guid.NewGuid(),
            FileName = "game.part1.rar",
            Status = DownloadStatus.Completed,
            IsEnabled = true
        };
        pkg.Items.Add(item);

        var packages = new List<DownloadPackage> { pkg };
        bool hasActive = PostDownloadActionService.CheckHasActiveWork(packages);

        Assert.True(hasActive);
    }

    [Fact]
    public void PostDownloadAction_CheckHasActiveWork_CompletedPackage_ReturnsFalse()
    {
        var pkg = new DownloadPackage
        {
            Id = Guid.NewGuid(),
            Name = "Completed Package",
            Status = DownloadStatus.Completed,
            IsEnabled = true
        };
        var item = new DownloadItem
        {
            Id = Guid.NewGuid(),
            FileName = "movie.mkv",
            Status = DownloadStatus.Completed,
            IsEnabled = true,
            TotalBytes = 2000,
            DownloadedBytes = 2000
        };
        pkg.Items.Add(item);

        var packages = new List<DownloadPackage> { pkg };
        bool hasActive = PostDownloadActionService.CheckHasActiveWork(packages);

        Assert.False(hasActive);
    }

    [Fact]
    public void PostDownloadAction_StartCountdown_SetsIsCountdownActiveAnd10Seconds()
    {
        var service = PostDownloadActionService.Instance;
        service.CurrentAction = PostDownloadAction.Shutdown;

        service.StartCountdown();

        Assert.True(service.IsCountdownActive);
        Assert.Equal(10, service.RemainingSeconds);

        service.CancelCountdown(manual: true);
        Assert.False(service.IsCountdownActive);
    }

    [Fact]
    public void PostDownloadAction_CancelCountdown_Manual_ResetsActionToNone()
    {
        var service = PostDownloadActionService.Instance;
        service.CurrentAction = PostDownloadAction.Sleep;
        service.StartCountdown();

        Assert.True(service.IsCountdownActive);

        // User clicks "Abbrechen"
        service.CancelCountdown(manual: true);

        Assert.False(service.IsCountdownActive);
        Assert.Equal(PostDownloadAction.None, service.CurrentAction);
    }

    [Fact]
    public void PostDownloadAction_CancelCountdown_Automatic_PreservesAction()
    {
        var service = PostDownloadActionService.Instance;
        service.CurrentAction = PostDownloadAction.ExitApp;
        service.StartCountdown();

        Assert.True(service.IsCountdownActive);

        // A new download starts or is resumed while countdown is running
        service.CancelCountdown(manual: false);

        Assert.False(service.IsCountdownActive);
        // Action remains armed for when new work finishes!
        Assert.Equal(PostDownloadAction.ExitApp, service.CurrentAction);
    }

    [Fact]
    public void PostDownloadAction_Evaluate_TriggersCountdownWhenAllActiveWorkFinished()
    {
        var service = PostDownloadActionService.Instance;
        service.CurrentAction = PostDownloadAction.Shutdown;

        var pkg = new DownloadPackage
        {
            Id = Guid.NewGuid(),
            Name = "Eval Package",
            Status = DownloadStatus.Downloading,
            IsEnabled = true
        };
        var item = new DownloadItem
        {
            Id = Guid.NewGuid(),
            FileName = "file.zip",
            Status = DownloadStatus.Downloading,
            IsEnabled = true
        };
        pkg.Items.Add(item);

        var packages = new List<DownloadPackage> { pkg };

        // 1. While running, evaluate records active work
        service.Evaluate(packages);
        Assert.False(service.IsCountdownActive);

        // 2. Package finishes 100%
        item.Status = DownloadStatus.Completed;
        item.TotalBytes = 5000;
        item.DownloadedBytes = 5000;
        pkg.Status = DownloadStatus.Completed;

        // 3. Evaluate after completion starts the 10-second countdown!
        service.Evaluate(packages);

        Assert.True(service.IsCountdownActive);
        Assert.Equal(10, service.RemainingSeconds);

        service.CancelCountdown(manual: true);
    }

    [Fact]
    public void PostDownloadAction_Evaluate_DoesNotTriggerWhenActionIsNone()
    {
        var service = PostDownloadActionService.Instance;
        service.CurrentAction = PostDownloadAction.None;
        service.NotifyWorkStarted();

        var pkg = new DownloadPackage
        {
            Id = Guid.NewGuid(),
            Name = "None Package",
            Status = DownloadStatus.Completed,
            IsEnabled = true
        };
        var item = new DownloadItem
        {
            Id = Guid.NewGuid(),
            FileName = "file.zip",
            Status = DownloadStatus.Completed,
            IsEnabled = true,
            TotalBytes = 100,
            DownloadedBytes = 100
        };
        pkg.Items.Add(item);

        service.Evaluate(new List<DownloadPackage> { pkg });

        Assert.False(service.IsCountdownActive);
    }

    [Theory]
    [InlineData(PostDownloadAction.None, "PostDownload_Action_None")]
    [InlineData(PostDownloadAction.Shutdown, "PostDownload_Action_Shutdown")]
    [InlineData(PostDownloadAction.Sleep, "PostDownload_Action_Sleep")]
    [InlineData(PostDownloadAction.ExitApp, "PostDownload_Action_ExitApp")]
    public void PostDownloadAction_GetActionDisplayName_ReturnsExpectedTranslations(PostDownloadAction action, string locKey)
    {
        var service = PostDownloadActionService.Instance;
        var displayName = service.GetActionDisplayName(action);
        var expected = Loc.Get(locKey);

        Assert.Equal(expected, displayName);
    }
}
