using System;
using System.Collections.Generic;

namespace Reepax.Models;

internal readonly record struct PackageProgressTotals(long Total, int Enabled, long Downloaded, double Speed,
    int Completed, int Active, int KnownCount, long KnownTotal, long Remaining, double ProgressSum, double MaxEta);

internal readonly record struct PackageItemCounts(int Total, int Enabled, int Active, int Ready,
    bool HasActiveWork, bool HasCompleted, bool PausableDownload, bool PausableQueued,
    bool Startable, bool StartableQueued, bool StartableBrowser)
{
    internal bool CanStart(bool running, bool queueRunning) => Startable || (!queueRunning && StartableQueued) || (!running && StartableBrowser);
}

internal sealed class PackageProgressCache
{
    private readonly object _gate = new();
    private readonly Dictionary<DownloadItem, (Entry Value, int Count)> _entries = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<double, int> _etaCounts = new();
    private readonly SortedSet<double> _etas = new();
    private readonly HashSet<DownloadItem> _timedItems = new(ReferenceEqualityComparer.Instance);
    private PackageProgressTotals _totals;
    private PackageItemCounts _counts;
    private long _durationTicks;
    private int _speedContributors;
    private int _progressContributors;
    private bool _valid;
    private long _revision;

    internal long Revision { get { lock (_gate) return _revision; } }
    internal void Invalidate() { lock (_gate) { _valid = false; _revision++; } }

    internal bool TryGet(out PackageProgressTotals totals, out PackageItemCounts counts)
        => TryGet(out totals, out counts, out _);

    internal bool TryGet(out PackageProgressTotals totals, out PackageItemCounts counts, out long revision)
    {
        lock (_gate)
        {
            revision = _revision;
            totals = _totals with { MaxEta = _etas.Count == 0 ? 0 : _etas.Max };
            counts = _counts;
            return _valid;
        }
    }

    internal void Rebuild(DownloadItem[] items)
    {
        lock (_gate)
        {
            _valid = false;
            _entries.Clear(); _etaCounts.Clear(); _etas.Clear(); _timedItems.Clear();
            _totals = default; _counts = default; _durationTicks = 0;
            _speedContributors = 0; _progressContributors = 0;
            foreach (var item in items)
            {
                var value = Capture(item);
                int count = _entries.TryGetValue(item, out var previous) ? previous.Count + 1 : 1;
                _entries[item] = (value, count);
                Add(value, 1);
                if (value.Status == DownloadStatus.Downloading) _timedItems.Add(item);
                bool paused = value.Status is DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Aborted;
                bool browser = value.Status is DownloadStatus.InBrowser or DownloadStatus.InBrowserSlot1 or DownloadStatus.InBrowserSlot2 or DownloadStatus.SolvingCaptcha;
                bool otherStart = value.Status is not (DownloadStatus.Completed or DownloadStatus.Downloading or DownloadStatus.InBrowser or DownloadStatus.SolvingCaptcha or DownloadStatus.Queued);
                bool queuedPause = value.Status is DownloadStatus.Queued or DownloadStatus.InBrowser or DownloadStatus.InBrowserSlot1 or DownloadStatus.InBrowserSlot2 or DownloadStatus.SolvingCaptcha or DownloadStatus.WaitingForBrowser;
                _counts = new(_counts.Total + 1, _counts.Enabled + (value.Enabled ? 1 : 0),
                    _counts.Active + (value.Enabled && value.Status == DownloadStatus.Downloading ? 1 : 0),
                    _counts.Ready + (value.Enabled && value.Ready ? 1 : 0),
                    _counts.HasActiveWork || (value.Enabled && (value.Status is DownloadStatus.Downloading or DownloadStatus.Queued || value.Verifying)),
                    _counts.HasCompleted || value.Status == DownloadStatus.Completed,
                    _counts.PausableDownload || (!value.Unavailable && value.Status == DownloadStatus.Downloading),
                    _counts.PausableQueued || (!value.Unavailable && value.Enabled && queuedPause),
                    _counts.Startable || (value.Enabled && (paused || otherStart)),
                    _counts.StartableQueued || (value.Enabled && value.Status == DownloadStatus.Queued),
                    _counts.StartableBrowser || (value.Enabled && browser));
            }
            _revision++;
            _valid = double.IsFinite(_totals.Speed) && double.IsFinite(_totals.ProgressSum);
        }
    }

    internal void Update(DownloadItem item)
    {
        lock (_gate)
        {
            if (!_valid || !_entries.TryGetValue(item, out var old)) return;
            var value = Capture(item);
            if (old.Value.Enabled != value.Enabled || old.Value.Status != value.Status || old.Value.Ready != value.Ready ||
                old.Value.Unavailable != value.Unavailable || old.Value.Verifying != value.Verifying ||
                (value.Status == DownloadStatus.Completed && value.Total <= 0 && value.Downloaded > 0) ||
                !double.IsFinite(value.Speed) || !double.IsFinite(value.Progress))
            {
                _valid = false;
                return;
            }
            if (old.Value == value) return;
            Add(old.Value, -old.Count);
            Add(value, old.Count);
            _entries[item] = (value, old.Count);
        }
    }

    internal bool TryGetDuration(out long ticks)
    {
        lock (_gate)
        {
            ticks = _durationTicks;
            if (!_valid) return false;
            // Only active item clocks advance without property notifications.
            foreach (var item in _timedItems)
            {
                var entry = _entries[item];
                ticks = checked(ticks + (item.Duration.Ticks - entry.Value.DurationTicks) * entry.Count);
            }
            return true;
        }
    }

    private Entry Capture(DownloadItem item)
    {
        _revision++;
        return new(item.IsEnabled, item.TotalBytes, item.DownloadedBytes, item.SpeedBytesPerSecond,
            item.ProgressPercentage, item.RemainingSeconds, item.Status, item.Duration.Ticks,
            item.IsReadyForPostProcessing, item.IsUnavailableForProcessing, item.IsVerifyingIntegrity);
    }

    private void Add(Entry value, int count)
    {
        _durationTicks = checked(_durationTicks + value.DurationTicks * count);
        _totals = _totals with { Total = _totals.Total + value.Total * count };
        if (!value.Enabled) return;
        bool completed = value.Status == DownloadStatus.Completed;
        bool known = value.Total > 0;
        double progress = completed ? 100 : known
            ? Math.Clamp((double)value.Downloaded / value.Total * 100, 0, 100)
            : value.Progress > 0 ? Math.Clamp(value.Progress, 0, 100) : 0;
        if (value.Speed != 0) _speedContributors += count;
        if (progress != 0) _progressContributors += count;
        // Removing the last contribution must produce an exact zero.
        _totals = new(_totals.Total, _totals.Enabled + count, _totals.Downloaded + value.Downloaded * count,
            _speedContributors == 0 ? 0 : _totals.Speed + value.Speed * count, _totals.Completed + (completed ? count : 0),
            _totals.Active + (value.Status == DownloadStatus.Downloading ? count : 0),
            _totals.KnownCount + (known ? count : 0), _totals.KnownTotal + (known ? value.Total * count : 0),
            _totals.Remaining + (!completed && known && value.Total > value.Downloaded ? (value.Total - value.Downloaded) * count : 0),
            _progressContributors == 0 ? 0 : _totals.ProgressSum + progress * count, 0);
        if (value.Status != DownloadStatus.Downloading || !(value.Eta > 0)) return;
        int occurrences = _etaCounts.GetValueOrDefault(value.Eta) + count;
        if (occurrences == 0) { _etaCounts.Remove(value.Eta); _etas.Remove(value.Eta); }
        else { _etaCounts[value.Eta] = occurrences; _etas.Add(value.Eta); }
    }

    private readonly record struct Entry(bool Enabled, long Total, long Downloaded, double Speed, double Progress,
        double Eta, DownloadStatus Status, long DurationTicks, bool Ready, bool Unavailable, bool Verifying);
}
