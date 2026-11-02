using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Reepax.Helpers;

public sealed partial class DownloadListView
{
    private const int IncrementalRowLimit = 64;

    private void SynchronizeRows(List<DownloadListRow> next)
    {
        var remaining = next.ToHashSet();
        foreach (var row in _rows)
            if (!remaining.Contains(row)) row.Index = -1;
        for (int index = 0; index < next.Count; index++)
        {
            next[index].Index = index;
            next[index].RefreshPresentation();
        }

        int first = 0;
        while (first < _rows.Count && first < next.Count && ReferenceEquals(_rows[first], next[first])) first++;
        int oldEnd = _rows.Count - 1;
        int newEnd = next.Count - 1;
        while (oldEnd >= first && newEnd >= first && ReferenceEquals(_rows[oldEnd], next[newEnd]))
        {
            oldEnd--;
            newEnd--;
        }

        if (Math.Max(oldEnd - first + 1, newEnd - first + 1) > IncrementalRowLimit)
        {
            if (TryMoveSingleRow(next, first, oldEnd, newEnd)) return;
            var viewer = ScrollViewer;
            double vertical = viewer?.VerticalOffset ?? 0;
            double horizontal = viewer?.HorizontalOffset ?? 0;
            _rows.ResetTo(next);
            if (viewer != null && next.Count > 0 && (vertical > 0 || horizontal > 0))
            {
                // Rebuild the virtualized extent before restoring pixel offsets.
                viewer.UpdateLayout();
                viewer.ScrollToVerticalOffset(vertical);
                viewer.ScrollToHorizontalOffset(horizontal);
            }
            return;
        }

        // Only the bounded changed span needs individual searches and notifications.
        for (int index = oldEnd; index >= first; index--)
        {
            if (remaining.Contains(_rows[index])) continue;
            _rows.RemoveAt(index);
            oldEnd--;
        }
        for (int index = first; index <= newEnd; index++)
        {
            var row = next[index];
            if (index < _rows.Count && ReferenceEquals(_rows[index], row)) continue;
            int source = index + 1;
            while (source <= oldEnd && !ReferenceEquals(_rows[source], row)) source++;
            if (source <= oldEnd)
                _rows.Move(source, index);
            else
            {
                _rows.Insert(index, row);
                oldEnd++;
            }
        }
    }

    private bool TryMoveSingleRow(List<DownloadListRow> next, int first, int oldEnd, int newEnd)
    {
        if (oldEnd != newEnd) return false;
        bool Matches(int currentStart, int nextStart)
        {
            for (int offset = 0; offset < oldEnd - first; offset++)
                if (!ReferenceEquals(_rows[currentStart + offset], next[nextStart + offset])) return false;
            return true;
        }
        if (ReferenceEquals(_rows[oldEnd], next[first]) && Matches(first, first + 1))
        {
            _rows.Move(oldEnd, first);
            return true;
        }
        if (ReferenceEquals(_rows[first], next[newEnd]) && Matches(first + 1, first))
        {
            _rows.Move(first, oldEnd);
            return true;
        }
        return false;
    }
}

internal sealed class DownloadRowCollection : ObservableCollection<DownloadListRow>
{
    private static readonly PropertyChangedEventArgs CountChanged = new(nameof(Count));
    private static readonly PropertyChangedEventArgs IndexerChanged = new("Item[]");
    private static readonly NotifyCollectionChangedEventArgs ResetChanged = new(NotifyCollectionChangedAction.Reset);

    internal void ResetTo(IReadOnlyList<DownloadListRow> rows)
    {
        CheckReentrancy();
        bool countChanged = Count != rows.Count;
        Items.Clear();
        foreach (var row in rows) Items.Add(row);
        if (countChanged) OnPropertyChanged(CountChanged);
        OnPropertyChanged(IndexerChanged);
        // Refresh the WPF view once without range notifications.
        OnCollectionChanged(ResetChanged);
    }
}
