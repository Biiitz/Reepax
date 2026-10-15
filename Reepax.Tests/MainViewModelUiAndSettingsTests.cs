using System;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Reepax.Models;
using Reepax.Services;
using Reepax.Services.Audio;
using Reepax.Services.Download;
using Reepax.Services.Extractor;
using Reepax.Services.Shortcuts;
using Reepax.Services.Storage;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

public class MainViewModelUiAndSettingsTests
{
    // =========================================================================
    // 1. ConnectionsPerDownload Stepper & Slider Tests
    // =========================================================================

    [Fact]
    public void ConnectionsPerDownload_IncrementAndDecrement_RespectsBoundaries()
    {
        var vm = new MainViewModel();

        // Set to middle value
        vm.ConnectionsPerDownload = 5;
        Assert.Equal(5, vm.ConnectionsPerDownload);
        Assert.Equal("5", vm.ConnectionsPerDownloadText);

        // Increment
        vm.IncrementConnections();
        Assert.Equal(6, vm.ConnectionsPerDownload);
        Assert.Equal("6", vm.ConnectionsPerDownloadText);

        // Decrement
        vm.DecrementConnections();
        Assert.Equal(5, vm.ConnectionsPerDownload);
        Assert.Equal("5", vm.ConnectionsPerDownloadText);

        // Test upper boundary (max 20)
        vm.ConnectionsPerDownload = 20;
        vm.IncrementConnections();
        Assert.Equal(20, vm.ConnectionsPerDownload);
        Assert.Equal("20", vm.ConnectionsPerDownloadText);

        // Test lower boundary (min 1)
        vm.ConnectionsPerDownload = 1;
        vm.DecrementConnections();
        Assert.Equal(1, vm.ConnectionsPerDownload);
        Assert.Equal("1", vm.ConnectionsPerDownloadText);
    }

    [Theory]
    [InlineData("10", 10)]
    [InlineData("1", 1)]
    [InlineData("20", 20)]
    [InlineData("0", 1)]     // snaps up to min 1
    [InlineData("-5", 1)]    // snaps up to min 1
    [InlineData("25", 20)]   // snaps down to max 20
    [InlineData("999", 20)]  // snaps down to max 20
    public void ConnectionsPerDownloadText_TwoWayInput_ClampsAndSnapsCorrectly(string input, int expectedValue)
    {
        var vm = new MainViewModel();
        vm.ConnectionsPerDownloadText = input;

        Assert.Equal(expectedValue, vm.ConnectionsPerDownload);
        Assert.Equal(expectedValue.ToString(), vm.ConnectionsPerDownloadText);
    }

    // =========================================================================
    // 2. MaxConcurrentDownloads Stepper & Slider Tests
    // =========================================================================

    [Fact]
    public void MaxConcurrentDownloads_IncrementAndDecrement_RespectsBoundaries()
    {
        var vm = new MainViewModel();

        vm.MaxConcurrentDownloads = 3;
        Assert.Equal(3, vm.MaxConcurrentDownloads);
        Assert.Equal("3", vm.MaxConcurrentDownloadsText);

        vm.IncrementMaxDownloads();
        Assert.Equal(4, vm.MaxConcurrentDownloads);
        Assert.Equal("4", vm.MaxConcurrentDownloadsText);

        vm.DecrementMaxDownloads();
        Assert.Equal(3, vm.MaxConcurrentDownloads);
        Assert.Equal("3", vm.MaxConcurrentDownloadsText);

        // Upper limit is 10
        vm.MaxConcurrentDownloads = 10;
        vm.IncrementMaxDownloads();
        Assert.Equal(10, vm.MaxConcurrentDownloads);
        Assert.Equal("10", vm.MaxConcurrentDownloadsText);

        // Lower limit is 1
        vm.MaxConcurrentDownloads = 1;
        vm.DecrementMaxDownloads();
        Assert.Equal(1, vm.MaxConcurrentDownloads);
        Assert.Equal("1", vm.MaxConcurrentDownloadsText);
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData("1", 1)]
    [InlineData("10", 10)]
    [InlineData("0", 1)]     // snaps up to min 1
    [InlineData("-2", 1)]    // snaps up to min 1
    [InlineData("15", 10)]   // snaps down to max 10
    [InlineData("100", 10)]  // snaps down to max 10
    public void MaxConcurrentDownloadsText_TwoWayInput_ClampsAndSnapsCorrectly(string input, int expectedValue)
    {
        var vm = new MainViewModel();
        vm.MaxConcurrentDownloadsText = input;

        Assert.Equal(expectedValue, vm.MaxConcurrentDownloads);
        Assert.Equal(expectedValue.ToString(), vm.MaxConcurrentDownloadsText);
    }

    // =========================================================================
    // 3. SpeedLimitMBps Stepper & Number Parsing Tests
    // =========================================================================

    [Theory]
    [InlineData("0", 0.0)]
    [InlineData("15", 15.0)]
    [InlineData("12.5", 12.5)]
    [InlineData("12,5", 12.5)]   // comma locale support
    [InlineData("  50  ", 50.0)]
    [InlineData("-5", 0.0)]      // negative clamped to 0
    [InlineData("", 0.0)]
    public void TryParseSpeedLimit_SupportsCommasDotsAndWhitespace(string input, double expected)
    {
        bool success = MainViewModel.TryParseSpeedLimit(input, out double parsed);
        Assert.True(success);
        Assert.Equal(expected, parsed);
    }

    [Fact]
    public void SpeedLimitStepper_IncrementAndDecrement_StepsAccurately()
    {
        var vm = new MainViewModel();
        vm.SpeedLimitMBps = 0;
        Assert.Equal("0", vm.SpeedLimitText);

        vm.IncrementSpeedLimit();
        Assert.Equal(1.0, vm.SpeedLimitMBps);

        vm.IncrementSpeedLimit();
        Assert.Equal(2.0, vm.SpeedLimitMBps);

        vm.DecrementSpeedLimit();
        Assert.Equal(1.0, vm.SpeedLimitMBps);

        vm.DecrementSpeedLimit();
        Assert.Equal(0.0, vm.SpeedLimitMBps);

        // Cannot go below 0
        vm.DecrementSpeedLimit();
        Assert.Equal(0.0, vm.SpeedLimitMBps);
    }

    [Fact]
    public void SpeedLimitMBps_FractionalIncrement_SnapsToCeilFloor()
    {
        var vm = new MainViewModel();
        vm.SpeedLimitMBps = 2.4;

        vm.IncrementSpeedLimit();
        Assert.Equal(3.0, vm.SpeedLimitMBps);

        vm.SpeedLimitMBps = 2.4;
        vm.DecrementSpeedLimit();
        Assert.Equal(2.0, vm.SpeedLimitMBps);
    }

    // =========================================================================
    // 4. Sound & Audio Settings Tests (Sliders, Volume, Cycling, Previews)
    // =========================================================================

    [Fact]
    public void SoundVolumeSliders_ClampBetween0And100()
    {
        var vm = new MainViewModel();

        // Completion volume
        vm.CompletionSoundVolume = 50;
        Assert.Equal(50, vm.CompletionSoundVolume);

        vm.CompletionSoundVolume = 150;
        Assert.Equal(100, vm.CompletionSoundVolume);

        vm.CompletionSoundVolume = -20;
        Assert.Equal(0, vm.CompletionSoundVolume);

        // Error volume
        vm.ErrorSoundVolume = 70;
        Assert.Equal(70, vm.ErrorSoundVolume);

        vm.ErrorSoundVolume = 200;
        Assert.Equal(100, vm.ErrorSoundVolume);

        vm.ErrorSoundVolume = -5;
        Assert.Equal(0, vm.ErrorSoundVolume);
    }

    [Fact]
    public void CompletionSoundTrigger_SwitchBetweenPackageAndIndividualFiles()
    {
        var vm = new MainViewModel();

        vm.IsCompletionSoundEntirePackage = true;
        Assert.True(vm.IsCompletionSoundEntirePackage);
        Assert.False(vm.IsCompletionSoundIndividualFiles);
        Assert.Equal(CompletionSoundTrigger.EntirePackage, vm.CompletionSoundTrigger);

        vm.IsCompletionSoundIndividualFiles = true;
        Assert.True(vm.IsCompletionSoundIndividualFiles);
        Assert.False(vm.IsCompletionSoundEntirePackage);
        Assert.Equal(CompletionSoundTrigger.IndividualFiles, vm.CompletionSoundTrigger);
    }

    [Fact]
    public void CycleCompletionSound_ForwardAndBackward_WrapsCorrectly()
    {
        var vm = new MainViewModel();
        vm.SelectedCompletionSound = "1.mp3";
        Assert.Equal("Sound 1", vm.SelectedCompletionSoundDisplayName);

        // Cycle forward
        vm.CycleCompletionSound(forward: true);
        Assert.Equal("2.mp3", vm.SelectedCompletionSound);

        // Cycle backwards
        vm.CycleCompletionSound(forward: false);
        Assert.Equal("1.mp3", vm.SelectedCompletionSound);

        // Cycle backward from 1.mp3 wraps to 10.mp3
        vm.CycleCompletionSound(forward: false);
        Assert.Equal("10.mp3", vm.SelectedCompletionSound);

        // Cycle forward from 10.mp3 wraps to 1.mp3
        vm.CycleCompletionSound(forward: true);
        Assert.Equal("1.mp3", vm.SelectedCompletionSound);
    }

    [Fact]
    public void CycleErrorSound_ForwardAndBackward_WrapsCorrectly()
    {
        var vm = new MainViewModel();
        vm.SelectedErrorSound = "1.mp3";
        Assert.Equal("Sound 1", vm.SelectedErrorSoundDisplayName);

        // Cycle forward to 2.mp3
        vm.CycleErrorSound(forward: true);
        Assert.Equal("2.mp3", vm.SelectedErrorSound);

        // Cycle forward from 2.mp3 wraps to 1.mp3
        vm.CycleErrorSound(forward: true);
        Assert.Equal("1.mp3", vm.SelectedErrorSound);

        // Cycle backward wraps to 2.mp3
        vm.CycleErrorSound(forward: false);
        Assert.Equal("2.mp3", vm.SelectedErrorSound);
    }

    [Fact]
    public void SoundPreviewCommands_ExecuteWithoutCrashing()
    {
        var vm = new MainViewModel();

        // Preview completion sound command
        vm.SelectAndPreviewCompletionSound("3.mp3");
        Assert.Equal("3.mp3", vm.SelectedCompletionSound);

        // Preview error sound command
        vm.SelectAndPreviewErrorSound("2.mp3");
        Assert.Equal("2.mp3", vm.SelectedErrorSound);

        // Volume preview commands
        vm.PreviewCompletionVolumeSound();
        vm.PreviewErrorVolumeSound();
        vm.PreviewVolumeSound();

        // Empty/null input guards
        vm.SelectAndPreviewCompletionSound(null);
        Assert.Equal("3.mp3", vm.SelectedCompletionSound);

        vm.SelectAndPreviewErrorSound("");
        Assert.Equal("2.mp3", vm.SelectedErrorSound);
    }

    // =========================================================================
    // 5. General Toggles & Settings Commands
    // =========================================================================

    [Fact]
    public void ExtractionToggles_ToggleStateCorrectly()
    {
        var vm = new MainViewModel();

        // AutoExtractArchives toggle
        bool initialAutoExtract = vm.AutoExtractArchives;
        vm.ToggleAutoExtractArchives();
        Assert.Equal(!initialAutoExtract, vm.AutoExtractArchives);
        vm.ToggleAutoExtractArchives();
        Assert.Equal(initialAutoExtract, vm.AutoExtractArchives);

        // DeleteArchiveAfterExtraction toggle
        bool initialDelete = vm.DeleteArchiveAfterExtraction;
        vm.ToggleDeleteArchiveAfterExtraction();
        Assert.Equal(!initialDelete, vm.DeleteArchiveAfterExtraction);
        vm.ToggleDeleteArchiveAfterExtraction();
        Assert.Equal(initialDelete, vm.DeleteArchiveAfterExtraction);

        // MoveArchiveToRecycleBin toggle
        bool initialRecycle = vm.MoveArchiveToRecycleBin;
        vm.ToggleMoveArchiveToRecycleBin();
        Assert.Equal(!initialRecycle, vm.MoveArchiveToRecycleBin);
        vm.ToggleMoveArchiveToRecycleBin();
        Assert.Equal(initialRecycle, vm.MoveArchiveToRecycleBin);

        // LowResourceExtraction toggle
        bool initialLowRes = vm.LowResourceExtraction;
        vm.ToggleLowResourceExtraction();
        Assert.Equal(!initialLowRes, vm.LowResourceExtraction);
        vm.ToggleLowResourceExtraction();
        Assert.Equal(initialLowRes, vm.LowResourceExtraction);
    }

    [Fact]
    public void SystemToggles_ToggleStateCorrectly()
    {
        var vm = new MainViewModel();

        // MinimizeToTrayOnClose
        bool initialTray = vm.MinimizeToTrayOnClose;
        vm.ToggleMinimizeToTrayOnClose();
        Assert.Equal(!initialTray, vm.MinimizeToTrayOnClose);
        vm.ToggleMinimizeToTrayOnClose();
        Assert.Equal(initialTray, vm.MinimizeToTrayOnClose);

        // CompletionNotifications
        bool initialNotif = vm.EnableCompletionNotifications;
        vm.ToggleCompletionNotifications();
        Assert.Equal(!initialNotif, vm.EnableCompletionNotifications);
        vm.ToggleCompletionNotifications();
        Assert.Equal(initialNotif, vm.EnableCompletionNotifications);

        // CompletionSound
        bool initialCompSound = vm.EnableCompletionSound;
        vm.ToggleCompletionSound();
        Assert.Equal(!initialCompSound, vm.EnableCompletionSound);
        vm.ToggleCompletionSound();
        Assert.Equal(initialCompSound, vm.EnableCompletionSound);

        // ErrorSound
        bool initialErrSound = vm.EnableErrorSound;
        vm.ToggleErrorSound();
        Assert.Equal(!initialErrSound, vm.EnableErrorSound);
        vm.ToggleErrorSound();
        Assert.Equal(initialErrSound, vm.EnableErrorSound);

        // AutoCollapseCompletedPackages
        bool initialCollapse = vm.AutoCollapseCompletedPackages;
        vm.ToggleAutoCollapseCompletedPackages();
        Assert.Equal(!initialCollapse, vm.AutoCollapseCompletedPackages);
        vm.ToggleAutoCollapseCompletedPackages();
        Assert.Equal(initialCollapse, vm.AutoCollapseCompletedPackages);

        // ClipboardMonitor
        bool initialClipboard = vm.EnableClipboardMonitor;
        vm.ToggleClipboardMonitor();
        Assert.Equal(!initialClipboard, vm.EnableClipboardMonitor);
        Assert.False(string.IsNullOrWhiteSpace(vm.ClipboardMonitorTooltip));
        vm.ToggleClipboardMonitor();
        Assert.Equal(initialClipboard, vm.EnableClipboardMonitor);
    }

    [Fact]
    public void ArchivePasswords_AddRemoveExpandAndCopy_WorkReliably()
    {
        var vm = new MainViewModel();
        vm.ArchivePasswords.Clear();

        // Toggle expanded
        bool initExpanded = vm.IsArchivePasswordsExpanded;
        vm.ToggleArchivePasswordsExpanded();
        Assert.Equal(!initExpanded, vm.IsArchivePasswordsExpanded);

        // Add password
        vm.NewArchivePasswordInput = " Secret123 ";
        Assert.True(vm.HasNewArchivePasswordInput);

        vm.AddArchivePassword();
        Assert.Equal(1, vm.ArchivePasswordsCount);
        Assert.True(vm.HasArchivePasswords);
        Assert.Contains("Secret123", vm.ArchivePasswords);
        Assert.Empty(vm.NewArchivePasswordInput);

        // Adding duplicate is ignored
        vm.NewArchivePasswordInput = "Secret123";
        vm.AddArchivePassword();
        Assert.Equal(1, vm.ArchivePasswordsCount);

        // Copy command does not crash
        vm.CopyArchivePassword("Secret123");
        vm.CopyArchivePassword(null);

        // Remove password
        vm.RemoveArchivePassword("Secret123");
        Assert.Equal(0, vm.ArchivePasswordsCount);
        Assert.False(vm.HasArchivePasswords);
    }

    // =========================================================================
    // 6. Post-Download Action Commands & State
    // =========================================================================

    [Fact]
    public void PostDownloadAction_SetAndCycleCommands_UpdateFlagsAndTooltips()
    {
        var vm = new MainViewModel();

        // Default / none
        vm.SetPostDownloadAction(PostDownloadAction.None);
        Assert.False(vm.IsPostDownloadActionActive);
        Assert.True(vm.IsNoneActionSelected);

        // Set via enum
        vm.SetPostDownloadAction(PostDownloadAction.Shutdown);
        Assert.True(vm.IsPostDownloadActionActive);
        Assert.True(vm.IsShutdownActionSelected);
        Assert.False(vm.IsSleepActionSelected);

        // Set via string (e.g. from XAML CommandParameter)
        vm.SetPostDownloadAction("Sleep");
        Assert.True(vm.IsSleepActionSelected);

        vm.SetPostDownloadAction("ExitApp");
        Assert.True(vm.IsExitAppActionSelected);

        // Cycle through all actions
        vm.SetPostDownloadAction(PostDownloadAction.None);
        vm.CyclePostDownloadAction();
        Assert.Equal(PostDownloadAction.Shutdown, vm.CurrentPostDownloadAction);

        vm.CyclePostDownloadAction();
        Assert.Equal(PostDownloadAction.Sleep, vm.CurrentPostDownloadAction);

        vm.CyclePostDownloadAction();
        Assert.Equal(PostDownloadAction.ExitApp, vm.CurrentPostDownloadAction);

        vm.CyclePostDownloadAction();
        Assert.Equal(PostDownloadAction.None, vm.CurrentPostDownloadAction);

        // Cancel countdown command
        vm.CancelPostDownloadCountdown();
    }

    // =========================================================================
    // 7. Keyboard Shortcut Recording, Conflict Handling & Resets
    // =========================================================================

    [Fact]
    public void KeyboardShortcut_RecordAndFinish_WithConflictResolution()
    {
        var vm = new MainViewModel();
        var shortcuts = vm.Shortcuts;
        Assert.NotEmpty(shortcuts);

        var first = shortcuts[0]; // e.g. TogglePauseResume
        var second = shortcuts[1]; // e.g. AddLinks

        // Start recording
        vm.StartRecordingShortcut(first);
        Assert.True(first.IsRecording);
        Assert.Equal(first, vm.RecordingShortcut);

        // Cancel recording
        vm.CancelRecordingShortcut();
        Assert.False(first.IsRecording);
        Assert.Null(vm.RecordingShortcut);

        // Record again and finish with a custom combination (Ctrl + Alt + K)
        vm.StartRecordingShortcut(first);
        vm.FinishRecordingShortcut(Key.K, ModifierKeys.Control | ModifierKeys.Alt);

        Assert.Equal(Key.K, first.Key);
        Assert.Equal(ModifierKeys.Control | ModifierKeys.Alt, first.Modifiers);
        Assert.False(first.IsRecording);
        Assert.Null(vm.RecordingShortcut);
        Assert.True(first.IsCustomized);

        // Conflict handling: Assign the same combination to the second shortcut
        vm.StartRecordingShortcut(second);
        vm.FinishRecordingShortcut(Key.K, ModifierKeys.Control | ModifierKeys.Alt);

        Assert.Equal(Key.K, second.Key);
        Assert.Equal(ModifierKeys.Control | ModifierKeys.Alt, second.Modifiers);

        // The first shortcut's duplicate combination must have been cleared
        Assert.Equal(Key.None, first.Key);
        Assert.Equal(ModifierKeys.None, first.Modifiers);

        // Reset individual shortcut
        vm.ResetShortcut(first);
        Assert.Equal(first.DefaultKey, first.Key);
        Assert.Equal(first.DefaultModifiers, first.Modifiers);

        // Reset all shortcuts
        vm.ResetAllShortcuts();
        Assert.False(second.IsCustomized);
        Assert.Equal(second.DefaultKey, second.Key);
    }

    // =========================================================================
    // 8. Queue & Toolbar Buttons: Expand, Collapse, Pause, Start, Clear
    // =========================================================================

    [Fact]
    public void ExpandAll_CollapseAll_And_ToggleExpandCollapse_WorkCorrectly()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();

        Assert.False(vm.CanExpandCollapseAll);

        var pkg1 = new DownloadPackage { Name = "Pkg 1", IsExpanded = false };
        var pkg2 = new DownloadPackage { Name = "Pkg 2", IsExpanded = false };
        vm.Packages.Add(pkg1);
        vm.Packages.Add(pkg2);

        Assert.True(vm.CanExpandCollapseAll);

        // ExpandAll
        vm.ExpandAll();
        Assert.True(pkg1.IsExpanded);
        Assert.True(pkg2.IsExpanded);

        // CollapseAll
        vm.CollapseAll();
        Assert.False(pkg1.IsExpanded);
        Assert.False(pkg2.IsExpanded);

        // ToggleExpandCollapseAll (since all are collapsed, it should expand all)
        vm.ToggleExpandCollapseAll();
        Assert.True(pkg1.IsExpanded);
        Assert.True(pkg2.IsExpanded);

        // If at least one is collapsed, it should expand all
        pkg1.IsExpanded = false;
        vm.ToggleExpandCollapseAll();
        Assert.True(pkg1.IsExpanded);
        Assert.True(pkg2.IsExpanded);

        // If all are expanded, it should collapse all
        vm.ToggleExpandCollapseAll();
        Assert.False(pkg1.IsExpanded);
        Assert.False(pkg2.IsExpanded);
    }

    [Fact]
    public void TogglePauseResumeCommand_SwitchesBetweenPauseAllAndStartAll()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();

        var pkg = new DownloadPackage { Name = "Package", Status = DownloadStatus.Paused };
        var item = new DownloadItem { FileName = "file.zip", Status = DownloadStatus.Paused, IsEnabled = true };
        pkg.Items.Add(item);
        vm.Packages.Add(pkg);

        Assert.True(vm.CanTogglePauseResume);

        // Initially paused -> TogglePauseResume should call StartAll
        vm.TogglePauseResume();
        // Item/package should now be queued or running
        Assert.NotEqual(DownloadStatus.Paused, item.Status);

        // Now queue is running -> TogglePauseResume should call PauseAll
        vm.TogglePauseResume();
        Assert.Equal(DownloadStatus.Paused, item.Status);
    }

    [Fact]
    public void ItemPauseResumeRetryCommands_ExecuteCleanly()
    {
        var vm = new MainViewModel();
        var item = new DownloadItem { FileName = "sample.bin", Status = DownloadStatus.Downloading, IsEnabled = true };

        // Toggle pause
        vm.ToggleItemPause(item);
        Assert.Equal(DownloadStatus.Paused, item.Status);

        // Toggle resume
        vm.ToggleItemPause(item);
        Assert.Equal(DownloadStatus.Queued, item.Status);

        // Explicit Pause
        vm.PauseItem(item);
        Assert.Equal(DownloadStatus.Paused, item.Status);

        // Explicit Resume
        vm.ResumeItem(item);
        Assert.Equal(DownloadStatus.Queued, item.Status);

        // Retry (e.g. from Failed state)
        item.Status = DownloadStatus.Failed;
        vm.RetryItem(item);
        Assert.Equal(DownloadStatus.Queued, item.Status);

        // Null guard does not throw
        vm.ToggleItemPause(null);
        vm.PauseItem(null);
        vm.ResumeItem(null);
        vm.RetryItem(null);
    }

    [Fact]
    public void PackagePauseResumeCommands_ReflectExtractionState()
    {
        var vm = new MainViewModel();
        var pkg = new DownloadPackage { Id = Guid.NewGuid(), Name = "ArchivePkg" };
        var item = new DownloadItem { FileName = "archive.part1.rar", Status = DownloadStatus.Downloading, IsEnabled = true };
        pkg.Items.Add(item);
        pkg.RecalculateAggregates();
        vm.Packages.Add(pkg);

        // Normal pause & resume
        vm.PausePackage(pkg);
        Assert.Equal(DownloadStatus.Paused, item.Status);
        Assert.Equal(DownloadStatus.Paused, pkg.Status);

        vm.ResumePackage(pkg);
        Assert.Equal(DownloadStatus.Queued, item.Status);

        // Toggle package pause
        item.Status = DownloadStatus.Downloading;
        vm.TogglePackagePause(pkg);
        Assert.Equal(DownloadStatus.Paused, item.Status);

        vm.TogglePackagePause(pkg);
        Assert.Equal(DownloadStatus.Queued, item.Status);

        // Null guard
        vm.PausePackage(null);
        vm.ResumePackage(null);
        vm.TogglePackagePause(null);
    }

    [Fact]
    public void SearchAndStatusFilter_FilterPackagesAndItemsAccurately()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();

        var pkg1 = new DownloadPackage { Name = "Alpha Project", Status = DownloadStatus.Completed };
        pkg1.Items.Add(new DownloadItem { FileName = "alpha_part1.rar", Status = DownloadStatus.Completed, IsEnabled = true });

        var pkg2 = new DownloadPackage { Name = "Beta Project", Status = DownloadStatus.Downloading };
        pkg2.Items.Add(new DownloadItem { FileName = "beta_setup.iso", Status = DownloadStatus.Downloading, IsEnabled = true });

        vm.Packages.Add(pkg1);
        vm.Packages.Add(pkg2);

        // Search text filtering
        vm.SearchFilterText = "Alpha";
        Assert.True(vm.HasSearchFilterText);

        vm.ClearSearchFilter();
        Assert.Empty(vm.SearchFilterText);
        Assert.False(vm.HasSearchFilterText);

        // Status filter command via enum
        vm.SetStatusFilter(DownloadStatusFilter.Completed);
        Assert.Equal(DownloadStatusFilter.Completed, vm.SelectedStatusFilter);
        Assert.True(vm.IsStatusFilterActive);

        // Status filter command via string
        vm.SetStatusFilter("Running");
        Assert.Equal(DownloadStatusFilter.Running, vm.SelectedStatusFilter);

        // Reset to All
        vm.SetStatusFilter(DownloadStatusFilter.All);
        Assert.False(vm.IsStatusFilterActive);
    }

    [Fact]
    public void RemovePackage_UnclipsChildPackagesAndPromotesToRoot()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();
        vm.RootPackages.Clear();

        var parent = new DownloadPackage { Id = Guid.NewGuid(), Name = "Parent Package" };
        var child = new DownloadPackage { Id = Guid.NewGuid(), Name = "Child Package", ParentPackageId = parent.Id, ParentPackageName = parent.Name };
        parent.ClippedPackages.Add(child);

        vm.Packages.Add(parent);
        vm.Packages.Add(child);
        vm.RootPackages.Add(parent);

        // Removing the parent package with deleteFilesFromDisk: false
        vm.RemovePackage(parent, deleteFilesFromDisk: false);

        // Parent should be removed, child should be promoted to RootPackages and unlinked
        Assert.DoesNotContain(parent, vm.Packages);
        Assert.DoesNotContain(parent, vm.RootPackages);
        Assert.Contains(child, vm.Packages);
        Assert.Contains(child, vm.RootPackages);
        Assert.Null(child.ParentPackageId);
        Assert.Null(child.ParentPackageName);
    }
}
