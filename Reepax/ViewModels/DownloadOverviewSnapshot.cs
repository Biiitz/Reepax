using System.Globalization;
using Reepax.Converters;
using Reepax.Models;
using Reepax.Services.Localization;
using Reepax.Services.Verification;

namespace Reepax.ViewModels;

internal sealed class DownloadOverviewSnapshot
{
    internal int Files, Active;
    private int _packages, _queued, _paused, _waiting, _completed, _failed, _disabled, _unknown, _verifying, _verified;
    private int _extracting, _repairing, _passwords, _cleanup;
    private long _size, _downloaded, _remaining;
    private double _speed, _seconds;
    private DateTime? _added, _finished;
    private readonly HashSet<string> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _names = new();

    private static IEnumerable<DownloadPackage> Flatten(IEnumerable<DownloadPackage> packages)
    {
        var visited = new HashSet<Guid>();
        var pending = new Stack<DownloadPackage>(packages.Reverse());
        while (pending.TryPop(out var package))
        {
            if (!visited.Add(package.Id)) continue;
            yield return package;
            foreach (var child in package.ClippedPackages.Reverse()) pending.Push(child);
        }
    }

    internal static DownloadOverviewSnapshot Capture(IEnumerable<DownloadPackage> packages, IEnumerable<DownloadPackage> filtered,
        DownloadOverviewScope scope, bool includeDisabled)
    {
        var all = Flatten(packages).ToArray();
        var scopedIds = scope switch
        {
            DownloadOverviewScope.Filtered => Flatten(filtered).Select(package => package.Id).ToHashSet(),
            DownloadOverviewScope.Selected => Flatten(all.Where(package => package.IsSelected)).Select(package => package.Id).ToHashSet(),
            _ => null
        };
        var result = new DownloadOverviewSnapshot();
        var seen = new HashSet<Guid>();
        foreach (var package in all)
        {
            bool packageInScope = scopedIds?.Contains(package.Id) ?? true;
            if (scope == DownloadOverviewScope.Filtered && !packageInScope) continue;
            if (!includeDisabled && !package.IsEnabled) continue;
            int before = result.Files;
            foreach (var item in package.ItemsSnapshot)
            {
                if (scope == DownloadOverviewScope.Selected && !packageInScope && !item.IsSelected) continue;
                if (!includeDisabled && !item.IsEnabled) continue;
                if (!seen.Add(item.Id)) continue;
                result.Add(item, package.IsEnabled);
            }
            if (result.Files == before && (package.ItemsSnapshot.Count != 0 || !packageInScope)) continue;
            result._packages++;
            if (result._names.Count < 4) result._names.Add(package.Name);
            if (!string.IsNullOrWhiteSpace(package.SaveDirectory)) result._paths.Add(package.SaveDirectory);
            if (package.RequiresArchivePassword) result._passwords++;
            if (package.HasPendingArchiveCleanup) result._cleanup++;
            if (package.IsExtracting) result._extracting++;
            if (package.NextTaskSteps.Any(step => step.Key == "Par2" && step.State is NextTaskStepState.Running or NextTaskStepState.Transitioning)) result._repairing++;
        }
        return result;
    }

    private void Add(DownloadItem item, bool packageEnabled)
    {
        Files++;
        if (!item.IsEnabled || !packageEnabled) _disabled++;
        long downloaded = Math.Max(0, item.DownloadedBytes);
        long size = item.TotalBytes > 0 ? item.TotalBytes : item.Status == DownloadStatus.Completed && downloaded > 0 ? downloaded : 0;
        _downloaded += downloaded;
        if (size > 0 || item.Status == DownloadStatus.Completed) { _size += size; _remaining += Math.Max(0, size - downloaded); }
        else _unknown++;
        if (item.Status == DownloadStatus.Downloading)
        {
            Active++;
            if (double.IsFinite(item.SpeedBytesPerSecond)) _speed += Math.Max(0, item.SpeedBytesPerSecond);
        }
        if (item.Status == DownloadStatus.Queued) _queued++;
        if (item.Status == DownloadStatus.Paused) _paused++;
        if (item.Status is DownloadStatus.WaitingForBrowser or DownloadStatus.InBrowser or DownloadStatus.InBrowserSlot1 or DownloadStatus.InBrowserSlot2 or DownloadStatus.SolvingCaptcha) _waiting++;
        if (item.IsReadyForPostProcessing) _completed++;
        if (item.Status is DownloadStatus.Failed or DownloadStatus.Aborted) _failed++;
        if (item.IsVerifyingIntegrity) _verifying++;
        if (item.IsReadyForPostProcessing && !string.IsNullOrWhiteSpace(item.ExpectedChecksum) && !string.IsNullOrWhiteSpace(item.CalculatedChecksum) &&
            string.Equals(ChecksumVerificationService.NormalizeChecksum(item.ExpectedChecksum),
                ChecksumVerificationService.NormalizeChecksum(item.CalculatedChecksum), StringComparison.OrdinalIgnoreCase)) _verified++;
        _seconds += Math.Max(0, item.Duration.TotalSeconds);
        if (!_added.HasValue || item.CreatedAt < _added) _added = item.CreatedAt;
        if (item.IsReadyForPostProcessing && item.CompletedAt.HasValue && (!_finished.HasValue || item.CompletedAt > _finished)) _finished = item.CompletedAt;
        if (!string.IsNullOrWhiteSpace(item.HosterName)) _sources.Add(item.HosterName);
    }

    internal string Format(string id, int configuredConnections)
    {
        string Number(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
        string Bytes(long value) => BytesToHumanReadableConverter.FormatBytes(value);
        string Time(double seconds) => TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 315360000)).ToString(@"d\.hh\:mm\:ss");
        string KnownBytes(long value) => _unknown > 0 ? Loc.Format("Overview_AtLeast", Bytes(value)) : Bytes(value);
        return id switch
        {
            "Packages" => Number(_packages), "Files" => Number(Files),
            "Names" => _names.Count == 0 ? "—" : string.Join(", ", _names.Take(3)) + (_packages > 3 ? " …" : ""),
            "Sources" => _sources.Count == 0 ? "—" : string.Join(", ", _sources.OrderBy(value => value).Take(3)) + (_sources.Count > 3 ? " …" : ""),
            "Paths" => _paths.Count == 0 ? "—" : _paths.Count == 1 ? _paths.First() : Loc.Format("Overview_FolderCount", _paths.Count),
            "Size" => KnownBytes(_size), "Downloaded" => Bytes(_downloaded), "Remaining" => KnownBytes(_remaining),
            "Progress" => Files == 0 ? "0 %" : _completed == Files ? "100 %" : _unknown > 0 || _size == 0 ? "—" : $"{Math.Clamp((double)_downloaded / _size * 100, 0, 100):0.00} %",
            "Speed" => (string)new SpeedToHumanReadableConverter().Convert(_speed, typeof(string), null, CultureInfo.CurrentCulture),
            "Eta" => Files > 0 && _completed == Files ? "00:00" : _unknown > 0 || _speed <= 0 || _paused > 0 || _waiting > 0 || _failed > 0 ? "—" : "≈ " + Time(_remaining / _speed),
            "Active" => Number(Active), "Queued" => Number(_queued), "Paused" => Number(_paused), "Waiting" => Number(_waiting),
            "Completed" => Number(_completed), "Failed" => Number(_failed), "Disabled" => Number(_disabled), "UnknownSizes" => Number(_unknown),
            "Verifying" => Number(_verifying), "Verified" => Number(_verified), "Duration" => Time(_seconds),
            "AverageSpeed" => (string)new SpeedToHumanReadableConverter().Convert(_seconds > 0 ? _downloaded / _seconds : 0, typeof(string), null, CultureInfo.CurrentCulture),
            "Added" => _added?.ToString("g", CultureInfo.CurrentCulture) ?? "—", "Finished" => _finished?.ToString("g", CultureInfo.CurrentCulture) ?? "—",
            "Extracting" => Number(_extracting), "Repairing" => Number(_repairing), "Passwords" => Number(_passwords), "Cleanup" => Number(_cleanup),
            "Connections" => configuredConnections.ToString(CultureInfo.CurrentCulture), _ => "—"
        };
    }
}
