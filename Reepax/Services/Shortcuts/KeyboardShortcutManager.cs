using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Reepax.Models;

namespace Reepax.Services.Shortcuts;

public partial class KeyboardShortcutManager : ObservableObject
{
    private static readonly Lazy<KeyboardShortcutManager> _instance = new(() => new KeyboardShortcutManager());
    public static KeyboardShortcutManager Instance => _instance.Value;

    public ObservableCollection<KeyboardShortcut> Shortcuts { get; } = new();

    public KeyboardShortcutManager()
    {
        InitializeDefaultShortcuts();
    }

    public void InitializeDefaultShortcuts()
    {
        Shortcuts.Clear();
        AddShortcut("TogglePauseResume", "Shortcut_TogglePauseResume_Title", "Shortcut_TogglePauseResume_Desc", Key.Space, ModifierKeys.Control);
        AddShortcut("AddLinks", "Shortcut_AddLinks_Title", "Shortcut_AddLinks_Desc", Key.N, ModifierKeys.Control);
        AddShortcut("ToggleExpandCollapse", "Shortcut_ToggleExpandCollapse_Title", "Shortcut_ToggleExpandCollapse_Desc", Key.L, ModifierKeys.None);
        AddShortcut("DeleteSelected", "Shortcut_DeleteSelected_Title", "Shortcut_DeleteSelected_Desc", Key.Delete, ModifierKeys.None);
        AddShortcut("RenameSelected", "Shortcut_RenameSelected_Title", "Shortcut_RenameSelected_Desc", Key.F2, ModifierKeys.None);
        AddShortcut("SelectAll", "Shortcut_SelectAll_Title", "Shortcut_SelectAll_Desc", Key.A, ModifierKeys.Control);
        AddShortcut("DeselectAll", "Shortcut_DeselectAll_Title", "Shortcut_DeselectAll_Desc", Key.Escape, ModifierKeys.None);
        AddShortcut("StartAll", "Shortcut_StartAll_Title", "Shortcut_StartAll_Desc", Key.R, ModifierKeys.Control);
        AddShortcut("PauseAll", "Shortcut_PauseAll_Title", "Shortcut_PauseAll_Desc", Key.P, ModifierKeys.Control);
        AddShortcut("ClearCompleted", "Shortcut_ClearCompleted_Title", "Shortcut_ClearCompleted_Desc", Key.C, ModifierKeys.Control | ModifierKeys.Shift);
        AddShortcut("OpenDownloadFolder", "Shortcut_OpenDownloadFolder_Title", "Shortcut_OpenDownloadFolder_Desc", Key.D, ModifierKeys.Control);
        AddShortcut("OpenExtensionsFolder", "Shortcut_OpenExtensionsFolder_Title", "Shortcut_OpenExtensionsFolder_Desc", Key.E, ModifierKeys.Control);
        AddShortcut("OpenSettings", "Shortcut_OpenSettings_Title", "Shortcut_OpenSettings_Desc", Key.OemComma, ModifierKeys.Control);
        AddShortcut("ShowDownloads", "Shortcut_ShowDownloads_Title", "Shortcut_ShowDownloads_Desc", Key.D1, ModifierKeys.Control);
        AddShortcut("ShowSettings", "Shortcut_ShowSettings_Title", "Shortcut_ShowSettings_Desc", Key.D2, ModifierKeys.Control);
        AddShortcut("ImportPackage", "Shortcut_ImportPackage_Title", "Shortcut_ImportPackage_Desc", Key.I, ModifierKeys.Control);
    }

    private void AddShortcut(string id, string titleKey, string descKey, Key defaultKey, ModifierKeys defaultModifiers)
    {
        var sc = new KeyboardShortcut
        {
            Id = id,
            TitleKey = titleKey,
            DescriptionKey = descKey,
            DefaultKey = defaultKey,
            DefaultModifiers = defaultModifiers,
            Key = defaultKey,
            Modifiers = defaultModifiers
        };
        Shortcuts.Add(sc);
    }

    public void LoadCustomShortcuts(Dictionary<string, string>? customShortcuts)
    {
        if (customShortcuts == null) return;

        foreach (var sc in Shortcuts)
        {
            if (customShortcuts.TryGetValue(sc.Id, out var serialized) &&
                KeyboardShortcut.TryParse(serialized, out var key, out var mods))
            {
                sc.Key = key;
                sc.Modifiers = mods;
            }
            else
            {
                sc.ResetToDefault();
            }
        }
    }

    public Dictionary<string, string> ExportCustomShortcuts()
    {
        var dict = new Dictionary<string, string>();
        foreach (var sc in Shortcuts)
        {
            if (sc.IsCustomized)
            {
                dict[sc.Id] = sc.Serialize();
            }
        }
        return dict;
    }

    public void ResetAll()
    {
        foreach (var sc in Shortcuts)
        {
            sc.ResetToDefault();
        }
    }

    public void RefreshLocalization()
    {
        foreach (var sc in Shortcuts)
        {
            sc.RefreshLocalization();
        }
    }

    public KeyboardShortcut? FindMatch(Key key, ModifierKeys modifiers)
    {
        var pureModifiers = modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows);
        return Shortcuts.FirstOrDefault(s => s.Key == key && s.Modifiers == pureModifiers);
    }
}
