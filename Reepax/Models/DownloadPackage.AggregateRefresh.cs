using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Reepax.Services.Localization;

namespace Reepax.Models;

public partial class DownloadPackage
{
    private DownloadItem[] _itemsSnapshot = Array.Empty<DownloadItem>();
    private bool _deferItemUpdates;
    private int _hosterNotificationsDirty = 1;
    private string _publishedPrimaryHoster = "Archive";
    private string[] _publishedHosters = Array.Empty<string>();
    private int _publishedEnabledCount;
    private int _publishedDeselectedCount;
    private bool _publishedCanEdit = true;
    private bool _publishedFullyCompleted;
    private string? _publishedSummary;
    private string? _summaryLanguage;
    private int _summaryItemCount = -1;
    private int _summaryEnabledCount = -1;
    private int _summaryCompletedCount = -1;
    private int _summaryTotalCount = -1;
    private bool _hasPublishedTime;
    private long _publishedDurationTicks;
    private long _publishedDurationSeconds;
    private bool _publishedDurationPositive;
    private double _publishedAverageSpeed;
    private string? _publishedTimeCulture;

    internal IReadOnlyList<DownloadItem> ItemsSnapshot => Volatile.Read(ref _itemsSnapshot);

    // Add prepared items without rebuilding the package after every insertion.
    internal void AddItems(IEnumerable<DownloadItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        bool wasDeferring = _deferItemUpdates;
        _deferItemUpdates = true;
        try
        {
            foreach (var item in items)
                Items.Add(item);
        }
        finally
        {
            _deferItemUpdates = wasDeferring;
            if (!wasDeferring)
            {
                Volatile.Write(ref _itemsSnapshot, Items.ToArray());
                RecalculateAggregates(force: true);
            }
        }
    }

    private static int CountEnabledItems(DownloadItem[] items)
    {
        int count = 0;
        foreach (var item in items)
        {
            if (item.IsEnabled)
                count++;
        }
        return count;
    }

    private static List<string> GetUsedHosters(DownloadItem[] items) =>
        items.Select(item => item.HosterName)
            .Where(name => !string.IsNullOrWhiteSpace(name) && name != "Unknown")
            .Distinct()
            .ToList();

    private bool AreAllNextTaskStepsDone()
    {
        for (int i = 0; i < NextTaskSteps.Count; i++)
        {
            if (NextTaskSteps[i].State != NextTaskStepState.Done)
                return false;
        }
        return true;
    }

    private string FormatItemsCountSummary(int itemCount, int enabledCount)
    {
        int deselectedCount = itemCount - enabledCount;
        string filesLabel = Loc.Get("Common_Files");
        if (deselectedCount > 0)
        {
            string deselectedLabel = Loc.Get("Common_Deselected");
            return $"{CompletedItemsCount} / {enabledCount} {filesLabel} ({deselectedCount} {deselectedLabel})";
        }
        return $"{CompletedItemsCount} / {TotalItemsCount} {filesLabel}";
    }

    private void PublishHosterChanges(DownloadItem[] items)
    {
        if (_deferItemUpdates)
            return;

        if (Interlocked.Exchange(ref _hosterNotificationsDirty, 0) == 0)
            return;

        var primaryHoster = items.Length > 0 ? items[0]?.HosterName ?? "Archive" : "Archive";
        if (!string.Equals(_publishedPrimaryHoster, primaryHoster, StringComparison.Ordinal))
        {
            _publishedPrimaryHoster = primaryHoster;
            OnPropertyChanged(nameof(PrimaryHosterName));
        }

        var hosters = GetUsedHosters(items);
        if (!_publishedHosters.SequenceEqual(hosters))
        {
            _publishedHosters = hosters.ToArray();
            OnPropertyChanged(nameof(UsedHosters));
        }
    }

    private void PublishSummaryChanges(int itemCount, int enabledCount)
    {
        string language = LocalizationService.Instance.CurrentLanguage;
        if (_summaryItemCount == itemCount && _summaryEnabledCount == enabledCount &&
            _summaryCompletedCount == CompletedItemsCount && _summaryTotalCount == TotalItemsCount &&
            string.Equals(_summaryLanguage, language, StringComparison.Ordinal))
            return;

        _summaryItemCount = itemCount;
        _summaryEnabledCount = enabledCount;
        _summaryCompletedCount = CompletedItemsCount;
        _summaryTotalCount = TotalItemsCount;
        _summaryLanguage = language;
        string summary = FormatItemsCountSummary(itemCount, enabledCount);
        if (!string.Equals(_publishedSummary, summary, StringComparison.Ordinal))
        {
            _publishedSummary = summary;
            OnPropertyChanged(nameof(ItemsCountSummary));
        }
    }

    private void PublishStructuralChanges(DownloadItem[] items, int enabledCount)
    {
        PublishHosterChanges(items);
        if (_publishedEnabledCount != enabledCount)
        {
            _publishedEnabledCount = enabledCount;
            OnPropertyChanged(nameof(EnabledItemsCount));
        }

        int deselectedCount = items.Length - enabledCount;
        if (_publishedDeselectedCount != deselectedCount)
        {
            _publishedDeselectedCount = deselectedCount;
            OnPropertyChanged(nameof(DeselectedItemsCount));
        }

        PublishSummaryChanges(items.Length, enabledCount);
        bool fullyCompleted = CheckIsFullyCompleted();
        if (_publishedFullyCompleted != fullyCompleted)
        {
            _publishedFullyCompleted = fullyCompleted;
            OnPropertyChanged(nameof(IsFullyCompleted));
        }

        bool canEdit = CanEditPackage;
        if (_publishedCanEdit != canEdit)
        {
            _publishedCanEdit = canEdit;
            OnPropertyChanged(nameof(CanEditPackage));
        }
    }

    private void PublishTimeChanges()
    {
        var duration = Duration;
        long seconds = duration.Ticks / TimeSpan.TicksPerSecond;
        bool positive = duration.Ticks > 0;
        double averageSpeed = AverageSpeedBytesPerSecond;
        string culture = CultureInfo.CurrentCulture.Name;
        bool cultureChanged = !string.Equals(_publishedTimeCulture, culture, StringComparison.Ordinal);

        bool durationChanged = !_hasPublishedTime || _publishedDurationTicks != duration.Ticks;
        bool durationTextChanged = !_hasPublishedTime || _publishedDurationSeconds != seconds ||
            _publishedDurationPositive != positive || cultureChanged;
        bool averageChanged = !_hasPublishedTime || !_publishedAverageSpeed.Equals(averageSpeed) || cultureChanged;
        _publishedDurationTicks = duration.Ticks;
        _publishedDurationSeconds = seconds;
        _publishedDurationPositive = positive;
        _publishedAverageSpeed = averageSpeed;
        _publishedTimeCulture = culture;
        _hasPublishedTime = true;

        if (durationChanged)
            OnPropertyChanged(nameof(Duration));
        if (durationTextChanged)
            OnPropertyChanged(nameof(DurationFormatted));
        if (averageChanged)
            OnPropertyChanged(nameof(AverageSpeedFormatted));
    }

    internal void RefreshLocalizedAggregateText()
    {
        var items = Volatile.Read(ref _itemsSnapshot);
        PublishSummaryChanges(items.Length, CountEnabledItems(items));
        PublishTimeChanges();
    }
}
