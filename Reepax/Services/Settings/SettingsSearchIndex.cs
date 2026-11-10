using System.Globalization;
using System.Text;
using Reepax.Services.Localization;
using Reepax.ViewModels;

namespace Reepax.Services.Settings;

internal sealed record SettingsSearchEntry(string Key, SettingsCategory Category, string DescriptionKey, string[] SearchKeys)
{
    internal IReadOnlyList<string> OptionTexts { get; init; } = Array.Empty<string>();
    internal string CategoryKey => Category switch
    {
        SettingsCategory.DownloadConnections => "Settings_Category_Downloads",
        SettingsCategory.Shortcuts => "Settings_Category_Shortcuts",
        SettingsCategory.About => "Settings_Category_About",
        _ => "Settings_Category_General"
    };
    public string Title => Loc.Get(Key);
    public string CategoryName => Loc.Get(CategoryKey);
    public string Description => string.IsNullOrEmpty(DescriptionKey) ? string.Empty : Loc.Get(DescriptionKey);
}

internal sealed class SettingsSearchIndex
{
    private sealed record Document(SettingsSearchEntry Entry, string[] Titles, string[] Details);
    private readonly Document[] _documents;

    internal SettingsSearchIndex(IEnumerable<SettingsSearchEntry> entries)
    {
        var maps = new[] { Strings_en.Map, Strings_de.Map };
        string[] Translations(IEnumerable<string> keys) => keys.SelectMany(key => maps
            .Where(map => map.ContainsKey(key)).Select(map => map[key])).ToArray();

        _documents = entries.DistinctBy(entry => entry.Key).Select(entry => new Document(entry,
            Translations(new[] { entry.Key }).Select(Normalize).Distinct(StringComparer.Ordinal).ToArray(),
            Translations(entry.SearchKeys.Append(entry.DescriptionKey)).Concat(entry.OptionTexts)
                .Select(Normalize).Distinct(StringComparer.Ordinal).ToArray())).ToArray();
    }

    internal IReadOnlyList<SettingsSearchEntry> Search(string? query)
    {
        string input = query ?? string.Empty;
        string normalized = Normalize(input[..Math.Min(input.Length, 256)]);
        if (normalized.Length == 0) return Array.Empty<SettingsSearchEntry>();

        return _documents.Select(document => (document.Entry, Score: Score(document, normalized)))
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Entry.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(match => match.Entry).ToArray();
    }

    private static int Score(Document document, string query)
    {
        int score = 0;
        foreach (string title in document.Titles)
        {
            if (title.Equals(query, StringComparison.Ordinal)) return 300;
            if (title.StartsWith(query, StringComparison.Ordinal)) score = Math.Max(score, 200);
            else if (title.Contains(query, StringComparison.Ordinal)) score = Math.Max(score, 100);
        }
        if (score > 0) return score;
        return document.Details.Any(text => text.Contains(query, StringComparison.Ordinal)) ? 10 : 0;
    }

    private static string Normalize(string value)
    {
        // Pasted text and the input limit may contain an incomplete UTF-16 pair.
        var safe = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes()) safe.Append(rune.ToString());

        var normalized = new StringBuilder();
        foreach (char character in safe.ToString().ToLowerInvariant().Replace("ß", "ss").Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(character)) normalized.Append(character);
            else if (normalized.Length > 0 && normalized[^1] != ' ') normalized.Append(' ');
        }
        return normalized.ToString().Trim().Replace("ae", "a").Replace("oe", "o").Replace("ue", "u");
    }
}
