using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Reepax.Services.Localization;

namespace Reepax.Models;

public sealed class ArchivePasswordEntry : ObservableObject
{
    public string Password { get; set; } = string.Empty;
    private DateTimeOffset? _addedAt;
    private long _usageCount;
    private long _successCount;
    public DateTimeOffset? AddedAt
    {
        get => _addedAt;
        set { if (SetProperty(ref _addedAt, value)) RefreshDateText(); }
    }
    public long UsageCount { get => _usageCount; set => SetProperty(ref _usageCount, value); }
    public long SuccessCount { get => _successCount; set => SetProperty(ref _successCount, value); }
    public DateTimeOffset? LastSuccessAt { get; set; }

    [JsonIgnore]
    public string AddedAtText => AddedAt?.ToLocalTime().ToString("g",
        CultureInfo.GetCultureInfo(LocalizationService.Instance.CurrentLanguage == "de" ? "de-DE" : "en-US"))
        ?? Loc.Get("Settings_ArchivePasswords_UnknownDate");

    internal ArchivePasswordEntry Snapshot() => new()
    {
        Password = Password, AddedAt = AddedAt, UsageCount = UsageCount, SuccessCount = SuccessCount,
        LastSuccessAt = LastSuccessAt
    };

    internal void RefreshDateText() => OnPropertyChanged(nameof(AddedAtText));

    internal static void NormalizeSettings(AppSettings settings)
    {
        settings.ExtractionPasswords = AppSettings.SanitizeExtractionPasswords(settings.ExtractionPasswords);
        var entries = new Dictionary<string, ArchivePasswordEntry>(StringComparer.Ordinal);
        foreach (var entry in settings.ArchivePasswordStatistics ?? new())
            if (entry != null && !string.IsNullOrEmpty(entry.Password)) entries.TryAdd(entry.Password, entry);
        settings.ArchivePasswordStatistics = settings.ExtractionPasswords.Select(password =>
        {
            var entry = entries.GetValueOrDefault(password) ?? new ArchivePasswordEntry { Password = password };
            entry.Password = password;
            entry.UsageCount = Math.Max(0, entry.UsageCount);
            entry.SuccessCount = Math.Clamp(entry.SuccessCount, 0, entry.UsageCount);
            return entry;
        }).ToList();
    }
}
