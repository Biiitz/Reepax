using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Reepax.Models;

namespace Reepax.Helpers;

public sealed partial class DownloadListView : ItemsControl
{
    public static readonly DependencyProperty PackagesProperty = DependencyProperty.Register(
        nameof(Packages), typeof(IEnumerable<DownloadPackage>), typeof(DownloadListView),
        new PropertyMetadata(null, OnPackagesChanged));

    private readonly ObservableCollection<DownloadListRow> _rows = new();
    private readonly Dictionary<object, DownloadListRow> _rowCache = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<DownloadPackage> _watchedPackages = new();
    private readonly HashSet<INotifyCollectionChanged> _watchedCollections = new();
    private readonly Dictionary<DownloadPackage, DownloadListRow> _packageEnds = new();
    private INotifyCollectionChanged? _rootCollection;
    private DispatcherOperation? _refreshOperation;
    private bool _isDetached;

    public IEnumerable<DownloadPackage>? Packages
    {
        get => (IEnumerable<DownloadPackage>?)GetValue(PackagesProperty);
        set => SetValue(PackagesProperty, value);
    }

    internal ScrollViewer? ScrollViewer => Template?.FindName("PART_ScrollViewer", this) as ScrollViewer;

    public DownloadListView()
    {
        ItemsSource = _rows;
        InitializePointerInteraction();
        Loaded += (_, _) =>
        {
            if (!_isDetached) return;
            _isDetached = false;
            AttachRoot();
            RefreshRows();
        };
        Unloaded += (_, _) =>
        {
            _isDetached = true;
            DetachSubscriptions();
        };
    }

    private static void OnPackagesChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (DownloadListView)sender;
        view.DetachSubscriptions();
        if (view._isDetached) return;
        view.AttachRoot();
        view.RefreshRows();
    }

    private void AttachRoot()
    {
        _rootCollection = Packages as INotifyCollectionChanged;
        if (_rootCollection != null)
            _rootCollection.CollectionChanged += OnCollectionChanged;
    }

    private void DetachSubscriptions()
    {
        CancelPointerInteraction();
        _refreshOperation?.Abort();
        _refreshOperation = null;
        if (_rootCollection != null)
            _rootCollection.CollectionChanged -= OnCollectionChanged;
        _rootCollection = null;
        foreach (var package in _watchedPackages)
            package.PropertyChanged -= OnPackageChanged;
        foreach (var collection in _watchedCollections)
            collection.CollectionChanged -= OnCollectionChanged;
        _watchedPackages.Clear();
        _watchedCollections.Clear();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => QueueRefresh();

    private void OnPackageChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (string.IsNullOrEmpty(args.PropertyName) ||
            args.PropertyName is nameof(DownloadPackage.IsExpanded) or nameof(DownloadPackage.IsClipped))
            QueueRefresh();

        if ((string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == nameof(DownloadPackage.IsDropAfter)) &&
            sender is DownloadPackage package && _packageEnds.TryGetValue(package, out var row))
            row.RefreshPresentation();
    }

    private void QueueRefresh()
    {
        if (_isDetached || _refreshOperation != null) return;
        // Combine structural changes before the next layout pass.
        _refreshOperation = Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            _refreshOperation = null;
            RefreshRows();
        }));
    }

    private void RefreshRows()
    {
        var next = new List<DownloadListRow>();
        var packages = new HashSet<DownloadPackage>();
        var collections = new HashSet<INotifyCollectionChanged>();
        var models = new HashSet<object>(ReferenceEqualityComparer.Instance);
        _packageEnds.Clear();
        foreach (var row in _rowCache.Values)
            row.EndingPackages.Clear();

        DownloadListRow GetRow(object model, DownloadPackage owner)
        {
            if (!_rowCache.TryGetValue(model, out var row))
                _rowCache.Add(model, row = new DownloadListRow(model, owner));
            row.Package = owner;
            return row;
        }

        void AppendPackage(DownloadPackage package)
        {
            if (!packages.Add(package)) return;
            models.Add(package);
            collections.Add(package.Items);
            collections.Add(package.ClippedPackages);
            next.Add(GetRow(package, package));
            foreach (var item in package.Items)
            {
                models.Add(item);
                if (package.IsExpanded)
                    next.Add(GetRow(item, package));
            }

            // Attached packages remain visible when the parent's files are collapsed.
            if (!package.IsClipped)
            {
                foreach (var child in package.ClippedPackages)
                    AppendPackage(child);
            }
            var last = next[^1];
            last.EndingPackages.Add(package);
            _packageEnds.Add(package, last);
        }

        foreach (var package in Packages ?? Enumerable.Empty<DownloadPackage>())
            AppendPackage(package);

        foreach (var package in _watchedPackages.Except(packages))
            package.PropertyChanged -= OnPackageChanged;
        foreach (var package in packages.Except(_watchedPackages))
            package.PropertyChanged += OnPackageChanged;
        _watchedPackages.Clear();
        _watchedPackages.UnionWith(packages);
        foreach (var collection in _watchedCollections.Except(collections))
            collection.CollectionChanged -= OnCollectionChanged;
        foreach (var collection in collections.Except(_watchedCollections))
            collection.CollectionChanged += OnCollectionChanged;
        _watchedCollections.Clear();
        _watchedCollections.UnionWith(collections);
        foreach (var model in _rowCache.Keys.Where(model => !models.Contains(model)).ToArray())
            _rowCache.Remove(model);

        var remaining = next.ToHashSet();
        for (int i = _rows.Count - 1; i >= 0; i--)
        {
            if (!remaining.Contains(_rows[i]))
            {
                _rows[i].Index = -1;
                _rows.RemoveAt(i);
            }
        }
        var existing = _rows.ToHashSet();
        for (int i = 0; i < next.Count; i++)
        {
            var row = next[i];
            row.Index = i;
            row.RefreshPresentation();
            if (i < _rows.Count && ReferenceEquals(_rows[i], row)) continue;
            if (existing.Contains(row))
                _rows.Move(_rows.IndexOf(row), i);
            else
                _rows.Insert(i, row);
        }
    }
}

public sealed class DownloadRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? PackageTemplate { get; set; }
    public DataTemplate? ItemTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item switch
    {
        DownloadPackage => PackageTemplate,
        DownloadItem => ItemTemplate,
        _ => null
    };
}

public sealed class DownloadListRow : ObservableObject
{
    internal int Index { get; set; } = -1;
    public object Model { get; }
    public DownloadPackage Package { get; internal set; }
    public bool IsFile => Model is DownloadItem;
    public bool IsClippedFile => IsFile && Package.IsClipped;
    public bool HasPackageDropAfter => EndingPackages.Any(package => package.IsDropAfter);
    internal List<DownloadPackage> EndingPackages { get; } = new();

    internal DownloadListRow(object model, DownloadPackage package)
    {
        Model = model;
        Package = package;
    }

    internal void RefreshPresentation()
    {
        OnPropertyChanged(nameof(IsClippedFile));
        OnPropertyChanged(nameof(HasPackageDropAfter));
    }
}
