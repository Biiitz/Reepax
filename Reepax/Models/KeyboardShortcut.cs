using System;
using System.Collections.Generic;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Reepax.Services.Localization;

namespace Reepax.Models;

public partial class KeyboardShortcut : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string TitleKey { get; set; } = string.Empty;
    public string DescriptionKey { get; set; } = string.Empty;

    public Key DefaultKey { get; set; } = Key.None;
    public ModifierKeys DefaultModifiers { get; set; } = ModifierKeys.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayGesture))]
    [NotifyPropertyChangedFor(nameof(IsCustomized))]
    private Key _key = Key.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayGesture))]
    [NotifyPropertyChangedFor(nameof(IsCustomized))]
    private ModifierKeys _modifiers = ModifierKeys.None;

    [ObservableProperty]
    private bool _isRecording;

    public bool IsCustomized => Key != DefaultKey || Modifiers != DefaultModifiers;

    public string Title => Loc.Get(TitleKey);
    public string Description => Loc.Get(DescriptionKey);

    public string DisplayGesture => FormatGesture(Key, Modifiers);

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(DisplayGesture));
    }

    public void ResetToDefault()
    {
        Key = DefaultKey;
        Modifiers = DefaultModifiers;
        IsRecording = false;
    }

    public static string FormatGesture(Key key, ModifierKeys modifiers)
    {
        if (key == Key.None)
            return Loc.Get("Shortcut_NotSet");

        var parts = new List<string>();
        bool isGerman = LocalizationService.Instance.CurrentLanguage == "de";

        if ((modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            parts.Add(isGerman ? "Strg" : "Ctrl");
        if ((modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
            parts.Add("Alt");
        if ((modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            parts.Add(isGerman ? "Umschalt" : "Shift");
        if ((modifiers & ModifierKeys.Windows) == ModifierKeys.Windows)
            parts.Add("Win");

        parts.Add(FormatKey(key, isGerman));
        return string.Join(" + ", parts);
    }

    public static string FormatKey(Key key, bool isGerman)
    {
        return key switch
        {
            Key.Space => isGerman ? "Leertaste" : "Space",
            Key.Delete => isGerman ? "Entf" : "Delete",
            Key.Back => isGerman ? "Rücktaste" : "Backspace",
            Key.Escape => "Esc",
            Key.Return => isGerman ? "Eingabe" : "Enter",
            Key.Tab => "Tab",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemMinus => "-",
            Key.OemPlus => "+",
            Key.D0 => "0",
            Key.D1 => "1",
            Key.D2 => "2",
            Key.D3 => "3",
            Key.D4 => "4",
            Key.D5 => "5",
            Key.D6 => "6",
            Key.D7 => "7",
            Key.D8 => "8",
            Key.D9 => "9",
            _ => key.ToString()
        };
    }

    public string Serialize()
    {
        var parts = new List<string>();
        if ((Modifiers & ModifierKeys.Control) == ModifierKeys.Control) parts.Add("Control");
        if ((Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt) parts.Add("Alt");
        if ((Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift) parts.Add("Shift");
        if ((Modifiers & ModifierKeys.Windows) == ModifierKeys.Windows) parts.Add("Windows");
        parts.Add(Key.ToString());
        return string.Join("+", parts);
    }

    public static bool TryParse(string? text, out Key key, out ModifierKeys modifiers)
    {
        key = Key.None;
        modifiers = ModifierKeys.None;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (Enum.TryParse<ModifierKeys>(part, true, out var mod))
            {
                modifiers |= mod;
            }
            else if (Enum.TryParse<Key>(part, true, out var k))
            {
                key = k;
            }
        }
        return key != Key.None;
    }
}
