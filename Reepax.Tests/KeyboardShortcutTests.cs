using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Reepax.Models;
using Reepax.Services.Localization;
using Reepax.Services.Shortcuts;
using Reepax.Services.Storage;
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

        var restartApp = manager.Shortcuts.FirstOrDefault(s => s.Id == "RestartApp");
        Assert.NotNull(restartApp);
        Assert.Equal(Key.R, restartApp.Key);
        Assert.Equal(ModifierKeys.Control | ModifierKeys.Shift, restartApp.Modifiers);
    }

    [Fact]
    public void RestartApp_Shortcut_HasCorrectLocalizationAndMatches()
    {
        var manager = new KeyboardShortcutManager();
        var restart = manager.Shortcuts.FirstOrDefault(s => s.Id == "RestartApp");
        Assert.NotNull(restart);

        var loc = LocalizationService.Instance;

        loc.CurrentLanguage = "de";
        restart.RefreshLocalization();
        Assert.Equal("App neu starten", restart.Title);
        Assert.Equal("Pausiert alle aktiven Downloads und startet die Anwendung neu.", restart.Description);
        Assert.Equal("Strg + Umschalt + R", restart.DisplayGesture);

        loc.CurrentLanguage = "en";
        restart.RefreshLocalization();
        Assert.Equal("Restart App", restart.Title);
        Assert.Equal("Pauses all active downloads and restarts the application.", restart.Description);
        Assert.Equal("Ctrl + Shift + R", restart.DisplayGesture);

        var matched = manager.FindMatch(Key.R, ModifierKeys.Control | ModifierKeys.Shift);
        Assert.NotNull(matched);
        Assert.Equal("RestartApp", matched.Id);
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

    [Theory]
    [InlineData("None")]
    [InlineData("none")]
    [InlineData(" None ")]
    public void KeyboardShortcut_ParsingExplicitUnassignedValue_PreservesDisabledState(string serialized)
    {
        Assert.True(KeyboardShortcut.TryParse(serialized, out var key, out var modifiers));
        Assert.Equal(Key.None, key);
        Assert.Equal(ModifierKeys.None, modifiers);
        Assert.Equal("None", new KeyboardShortcut { Key = key, Modifiers = modifiers }.Serialize());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("UnknownKey")]
    [InlineData("Control")]
    [InlineData("Control+None")]
    [InlineData("None+UnknownKey")]
    public void KeyboardShortcutManager_InvalidStoredValuesRestoreDefaultsWhileExplicitNoneStaysDisabled(string? invalid)
    {
        Assert.False(KeyboardShortcut.TryParse(invalid, out _, out _));
        var manager = new KeyboardShortcutManager();
        manager.LoadCustomShortcuts(new Dictionary<string, string>
        {
            ["TogglePauseResume"] = "None",
            ["AddLinks"] = invalid!
        });

        var disabled = manager.Shortcuts.Single(shortcut => shortcut.Id == "TogglePauseResume");
        Assert.Equal(Key.None, disabled.Key);
        Assert.Equal(ModifierKeys.None, disabled.Modifiers);
        Assert.True(disabled.IsCustomized);
        Assert.Same(manager.Shortcuts.Single(shortcut => shortcut.Id == "AddLinks"), manager.FindMatch(Key.N, ModifierKeys.Control));
        Assert.All(manager.Shortcuts.Where(shortcut => shortcut != disabled), shortcut => Assert.False(shortcut.IsCustomized));
        Assert.Equal("None", Assert.Single(manager.ExportCustomShortcuts()).Value);
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

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void MainViewModel_FinishRecordingShortcut_ReassignsConflictingShortcuts(string language)
    {
        string root = Directory.CreateTempSubdirectory("ShortcutReload_").FullName;
        var settings = SettingsService.Instance.Settings;
        var previousCustomShortcuts = settings.CustomShortcuts;
        var previousBindings = KeyboardShortcutManager.Instance.ExportCustomShortcuts();
        string previousLanguage = LocalizationService.Instance.CurrentLanguage;
        try
        {
            LocalizationService.Instance.CurrentLanguage = language;
            var vm = new MainViewModel();
            vm.ResetAllShortcutsCommand.Execute(null);
            var pauseResume = vm.Shortcuts.Single(shortcut => shortcut.Id == "TogglePauseResume");
            var addLinks = vm.Shortcuts.Single(shortcut => shortcut.Id == "AddLinks");
            var rename = vm.Shortcuts.Single(shortcut => shortcut.Id == "RenameSelected");
            vm.StartRecordingShortcut(rename);
            vm.FinishRecordingShortcut(Key.F6, ModifierKeys.Alt);
            vm.StartRecordingShortcut(addLinks);
            Assert.Same(addLinks, vm.RecordingShortcut);
            Assert.True(addLinks.IsRecording);
            vm.FinishRecordingShortcut(Key.Space, ModifierKeys.Control);

            Assert.Null(vm.RecordingShortcut);
            Assert.False(addLinks.IsRecording);
            Assert.Equal(Key.Space, addLinks.Key);
            Assert.Equal(ModifierKeys.Control, addLinks.Modifiers);
            Assert.Equal(Key.None, pauseResume.Key);
            Assert.Equal(ModifierKeys.None, pauseResume.Modifiers);
            Assert.Equal("None", settings.CustomShortcuts[pauseResume.Id]);

            string path = Path.Combine(root, "settings.json");
            var storage = new SettingsService(path);
            storage.Settings.DefaultDownloadDirectory = Path.Combine(root, "downloads");
            storage.Settings.MaxConcurrentDownloads = 7;
            storage.Settings.CustomShortcuts = new(settings.CustomShortcuts);
            storage.SaveSettings();
            var reopened = new SettingsService(path);
            Assert.False(reopened.HasSettingsLoadFailed);
            Assert.Equal(storage.Settings.DefaultDownloadDirectory, reopened.Settings.DefaultDownloadDirectory);
            Assert.Equal(7, reopened.Settings.MaxConcurrentDownloads);
            var loaded = new KeyboardShortcutManager();
            loaded.LoadCustomShortcuts(reopened.Settings.CustomShortcuts);
            var loadedDisabled = loaded.Shortcuts.Single(shortcut => shortcut.Id == pauseResume.Id);
            Assert.Equal(Key.None, loadedDisabled.Key);
            Assert.Equal(ModifierKeys.None, loadedDisabled.Modifiers);
            Assert.Equal(pauseResume.DisplayGesture, loadedDisabled.DisplayGesture);
            Assert.Equal("AddLinks", loaded.FindMatch(Key.Space, ModifierKeys.Control)?.Id);
            Assert.Single(loaded.Shortcuts.Where(shortcut => shortcut.Key == Key.Space && shortcut.Modifiers == ModifierKeys.Control));
            Assert.Equal("RenameSelected", loaded.FindMatch(Key.F6, ModifierKeys.Alt)?.Id);
            Assert.Null(loaded.FindMatch(Key.N, ModifierKeys.Control));
            Assert.Equal(storage.Settings.CustomShortcuts.OrderBy(pair => pair.Key), loaded.ExportCustomShortcuts().OrderBy(pair => pair.Key));
            Assert.All(loaded.Shortcuts.Where(shortcut => shortcut.Id is not ("TogglePauseResume" or "AddLinks" or "RenameSelected")),
                shortcut => Assert.False(shortcut.IsCustomized));

            vm.ShortcutManager.LoadCustomShortcuts(reopened.Settings.CustomShortcuts);
            vm.ResetShortcut(addLinks);
            Assert.Equal("AddLinks", vm.ShortcutManager.FindMatch(Key.N, ModifierKeys.Control)?.Id);
            Assert.Equal(Key.None, pauseResume.Key);
            Assert.Equal("None", settings.CustomShortcuts[pauseResume.Id]);
            Assert.Equal("RenameSelected", vm.ShortcutManager.FindMatch(Key.F6, ModifierKeys.Alt)?.Id);
            vm.ResetAllShortcutsCommand.Execute(null);
            Assert.Equal(Key.Space, pauseResume.Key);
            Assert.Equal(Key.N, addLinks.Key);
            Assert.All(vm.Shortcuts, shortcut => Assert.False(shortcut.IsCustomized));
            Assert.Empty(settings.CustomShortcuts);
        }
        finally
        {
            KeyboardShortcutManager.Instance.LoadCustomShortcuts(previousBindings);
            settings.CustomShortcuts = previousCustomShortcuts;
            LocalizationService.Instance.CurrentLanguage = previousLanguage;
            Directory.Delete(root, recursive: true);
        }
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
