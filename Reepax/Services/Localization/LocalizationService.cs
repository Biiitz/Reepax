using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;

namespace Reepax.Services.Localization;

public class LocalizationService : INotifyPropertyChanged
{
    private static readonly Lazy<LocalizationService> _instance = new(() => new LocalizationService());
    public static LocalizationService Instance => _instance.Value;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<string>? LanguageChanged;

    private string _currentLanguage = "en"; // Default is English
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _languages = new(StringComparer.OrdinalIgnoreCase);

    private LocalizationService()
    {
        _languages["en"] = Strings_en.Map;
        _languages["de"] = Strings_de.Map;
    }

    public string CurrentLanguage
    {
        get => _currentLanguage;
        set
        {
            var normalized = NormalizeLanguage(value);
            if (!string.Equals(_currentLanguage, normalized, StringComparison.OrdinalIgnoreCase))
            {
                _currentLanguage = normalized;
                ApplyCulture(normalized);
                RaiseLanguageChanged();
            }
        }
    }

    public string this[string key] => Get(key);

    public string Get(string key)
    {
        if (string.IsNullOrEmpty(key))
            return string.Empty;

        if (_languages.TryGetValue(_currentLanguage, out var currentMap) && currentMap.TryGetValue(key, out var translation))
        {
            return translation;
        }

        // Fallback to English if current language missing key
        if (!_currentLanguage.Equals("en", StringComparison.OrdinalIgnoreCase) &&
            _languages.TryGetValue("en", out var enMap) && enMap.TryGetValue(key, out var enTranslation))
        {
            return enTranslation;
        }

        // Fallback to German if English missing key
        if (_languages.TryGetValue("de", out var deMap) && deMap.TryGetValue(key, out var deTranslation))
        {
            return deTranslation;
        }

        return key;
    }

    public string Format(string key, params object[] args)
    {
        var template = Get(key);
        try
        {
            return string.Format(template, args);
        }
        catch
        {
            return template;
        }
    }

    private void ApplyCulture(string lang)
    {
        try
        {
            var culture = new CultureInfo(lang.Equals("de", StringComparison.OrdinalIgnoreCase) ? "de-DE" : "en-US");
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }
        catch { }
    }

    private void RaiseLanguageChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLanguage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        LanguageChanged?.Invoke(_currentLanguage);
    }

    private static string NormalizeLanguage(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang))
            return "en";

        var trimmed = lang.Trim().ToLowerInvariant();
        if (trimmed.StartsWith("de"))
            return "de";

        return "en";
    }
}

/// <summary>
/// Short static helper for convenient translation calls in code.
/// </summary>
public static class Loc
{
    public static string Get(string key) => LocalizationService.Instance.Get(key);
    public static string T(string key) => LocalizationService.Instance.Get(key);
    public static string Format(string key, params object[] args) => LocalizationService.Instance.Format(key, args);
}
