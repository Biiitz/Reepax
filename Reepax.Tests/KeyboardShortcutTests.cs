using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Reepax.Models;
using Reepax.Services.Localization;
using Reepax.Services.Shortcuts;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

public class KeyboardShortcutTests
{
    [Fact]
    public void KeyboardShortcutManager_InitializesAllDefaultShortcuts()
    {
        var manager = new KeyboardShortcutManager();
        Assert.NotEmpty(manager.Shortcuts);
        Assert.True(manager.Shortcuts.Count >= 16);

        var pauseResume = manager.Shortcuts.FirstOrDefault(s => s.Id == "TogglePauseResume");
        Assert.NotNull(pauseResume);
        Assert.Equal(Key.Space, pauseResume.Key);
        Assert.Equal(ModifierKeys.Control, pauseResume.Modifiers);

        var addLinks = manager.Shortcuts.FirstOrDefault(s => s.Id == "AddLinks");
        Assert.NotNull(addLinks);
        Assert.Equal(Key.N, addLinks.Key);
        Assert.Equal(ModifierKeys.Control, addLinks.Modifiers);

        var expandCollapse = manager.Shortcuts.FirstOrDefault(s => s.Id == "ToggleExpandCollapse");
        Assert.NotNull(expandCollapse);
        Assert.Equal(Key.L, expandCollapse.Key);
        Assert.Equal(ModifierKeys.None, expandCollapse.Modifiers);

        var renameSelected = manager.Shortcuts.FirstOrDefault(s => s.Id == "RenameSelected");
        Assert.NotNull(renameSelected);
        Assert.Equal(Key.F2, renameSelected.Key);
        Assert.Equal(ModifierKeys.None, renameSelected.Modifiers);
    }

    [Fact]
    public void KeyboardShortcut_Formatting_WorksInBothLanguages()
    {
        var sc = new KeyboardShortcut
        {
            Id = "TestShortcut",
            DefaultKey = Key.Space,
            DefaultModifiers = ModifierKeys.Control,
            Key = Key.Space,
            Modifiers = ModifierKeys.Control
        };

        var loc = LocalizationService.Instance;

        loc.CurrentLanguage = "de";
        var deDisplay = sc.DisplayGesture;
        Assert.Equal("Strg + Leertaste", deDisplay);

        loc.CurrentLanguage = "en";
        var enDisplay = sc.DisplayGesture;
        Assert.Equal("Ctrl + Space", enDisplay);
    }

    [Fact]
    public void KeyboardShortcut_SerializationAndParsing_RoundTrips()
    {
        var key = Key.K;
        var mods = ModifierKeys.Control | ModifierKeys.Shift;
        var sc = new KeyboardShortcut { Key = key, Modifiers = mods };

        var serialized = sc.Serialize();
        Assert.Contains("Control", serialized);
        Assert.Contains("Shift", serialized);
        Assert.Contains("K", serialized);

        var success = KeyboardShortcut.TryParse(serialized, out var parsedKey, out var parsedMods);
        Assert.True(success);
        Assert.Equal(key, parsedKey);
        Assert.Equal(mods, parsedMods);
    }

    [Fact]
    public void KeyboardShortcutManager_LoadAndExportCustomShortcuts_PreservesCustomizations()
    {
        var manager = new KeyboardShortcutManager();
        var customDict = new Dictionary<string, string>
        {
            ["TogglePauseResume"] = "F5",
            ["AddLinks"] = "Control+Shift+A"
        };

        manager.LoadCustomShortcuts(customDict);

        var pauseResume = manager.Shortcuts.First(s => s.Id == "TogglePauseResume");
        Assert.Equal(Key.F5, pauseResume.Key);
        Assert.Equal(ModifierKeys.None, pauseResume.Modifiers);
        Assert.True(pauseResume.IsCustomized);

        var addLinks = manager.Shortcuts.First(s => s.Id == "AddLinks");
        Assert.Equal(Key.A, addLinks.Key);
        Assert.Equal(ModifierKeys.Control | ModifierKeys.Shift, addLinks.Modifiers);
        Assert.True(addLinks.IsCustomized);

        var exported = manager.ExportCustomShortcuts();
        Assert.True(exported.ContainsKey("TogglePauseResume"));
        Assert.True(exported.ContainsKey("AddLinks"));

        // Reset
        manager.ResetAll();
        Assert.False(pauseResume.IsCustomized);
        Assert.Equal(Key.Space, pauseResume.Key);
        Assert.Equal(ModifierKeys.Control, pauseResume.Modifiers);
    }

    [Fact]
    public void KeyboardShortcutManager_FindMatch_ReturnsCorrectShortcut()
    {
        var manager = new KeyboardShortcutManager();
        manager.ResetAll();

        var match = manager.FindMatch(Key.N, ModifierKeys.Control);
        Assert.NotNull(match);
        Assert.Equal("AddLinks", match.Id);

        var matchL = manager.FindMatch(Key.L, ModifierKeys.None);
        Assert.NotNull(matchL);
        Assert.Equal("ToggleExpandCollapse", matchL.Id);

        var noMatch = manager.FindMatch(Key.F12, ModifierKeys.Alt);
        Assert.Null(noMatch);
    }

    [Fact]
    public void MainViewModel_FinishRecordingShortcut_ReassignsConflictingShortcuts()
    {
        var vm = new MainViewModel();
        vm.ResetAllShortcutsCommand.Execute(null);

        var pauseResume = vm.Shortcuts.First(s => s.Id == "TogglePauseResume"); // Default: Ctrl+Space
        var addLinks = vm.Shortcuts.First(s => s.Id == "AddLinks"); // Default: Ctrl+N

        // Start recording for addLinks and press Ctrl+Space (which belongs to pauseResume)
        vm.StartRecordingShortcut(addLinks);
        Assert.Same(addLinks, vm.RecordingShortcut);
        Assert.True(addLinks.IsRecording);

        vm.FinishRecordingShortcut(Key.Space, ModifierKeys.Control);

        Assert.Null(vm.RecordingShortcut);
        Assert.False(addLinks.IsRecording);
        Assert.Equal(Key.Space, addLinks.Key);
        Assert.Equal(ModifierKeys.Control, addLinks.Modifiers);

        // Previous owner (pauseResume) should have been cleared to avoid duplicate trigger
        Assert.Equal(Key.None, pauseResume.Key);

        // Reset all
        vm.ResetAllShortcutsCommand.Execute(null);
        Assert.Equal(Key.Space, pauseResume.Key);
        Assert.Equal(Key.N, addLinks.Key);
    }

    [Fact]
    public void MainViewModel_SelectAllAndDeselectAll_UpdatesSelection()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();

        var pkg = new DownloadPackage { Name = "Test Pkg" };
        var item1 = new DownloadItem { FileName = "file1.zip" };
        var item2 = new DownloadItem { FileName = "file2.zip" };
        pkg.Items.Add(item1);
        pkg.Items.Add(item2);
        vm.Packages.Add(pkg);

        vm.SelectAllCommand.Execute(null);
        Assert.True(pkg.IsSelected);
        Assert.True(item1.IsSelected);
        Assert.True(item2.IsSelected);

        vm.DeselectAllCommand.Execute(null);
        Assert.False(pkg.IsSelected);
        Assert.False(item1.IsSelected);
        Assert.False(item2.IsSelected);
        Assert.Null(vm.SelectedItem);
        Assert.Null(vm.SelectedPackage);
    }
}
