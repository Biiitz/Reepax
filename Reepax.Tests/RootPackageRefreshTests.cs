using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using Reepax.Models;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

[Collection("SharedQueue")]
public sealed class RootPackageRefreshTests : IDisposable
{
    private readonly MainViewModel _viewModel = new();

    public RootPackageRefreshTests()
    {
        _viewModel.Packages.Clear();
        _viewModel.ClearColumnSort();
        _viewModel.SearchFilterText = string.Empty;
        _viewModel.SelectedStatusFilter = DownloadStatusFilter.All;
    }

    public void Dispose()
    {
        _viewModel.ClearColumnSort();
        _viewModel.SearchFilterText = string.Empty;
        _viewModel.Packages.Clear();
    }

    [Theory]
    [InlineData(128, false)]
    [InlineData(256, false)]
    [InlineData(128, true)]
    [InlineData(256, true)]
    public void Refresh_UnchangedRootsAvoidRepeatedLinearSearches(int count, bool sorted)
    {
        for (int i = 0; i < count; i++)
            _viewModel.Packages.Add(new ComparisonCountingPackage { Name = $"Package {i:D4}" });
        if (sorted) _viewModel.ToggleColumnSort("Name");
        var expected = _viewModel.Packages.ToArray();
        var changes = new List<NotifyCollectionChangedAction>();
        _viewModel.RootPackages.CollectionChanged += (_, args) => changes.Add(args.Action);
        ComparisonCountingPackage.Comparisons = 0;

        _viewModel.RefreshRootPackages();

        int comparisons = ComparisonCountingPackage.Comparisons;
        Assert.Empty(changes);
        AssertReferences(expected, _viewModel.RootPackages);
        Assert.True(comparisons <= count * 4, $"Unchanged refresh performed {comparisons} equality comparisons for {count} packages.");
    }

    [Fact]
    public void Refresh_AppendingPackageOnlyInsertsNewInstance()
    {
        for (int i = 0; i < 128; i++)
            _viewModel.Packages.Add(new ComparisonCountingPackage { Name = $"Package {i:D4}" });
        var first = _viewModel.Packages[0];
        first.IsSelected = true;
        first.IsExpanded = false;
        var expected = _viewModel.Packages.ToList();
        var actions = new List<NotifyCollectionChangedAction>();
        _viewModel.RootPackages.CollectionChanged += (_, args) => actions.Add(args.Action);
        ComparisonCountingPackage.Comparisons = 0;
        var added = new DownloadPackage { Name = "Added package" };

        _viewModel.Packages.Add(added);

        int comparisons = ComparisonCountingPackage.Comparisons;
        expected.Add(added);
        AssertReferences(expected, _viewModel.RootPackages);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Add }, actions);
        Assert.True(first.IsSelected);
        Assert.False(first.IsExpanded);
        Assert.True(comparisons <= expected.Count * 4, $"Appending performed {comparisons} equality comparisons.");
    }

    [Fact]
    public void Refresh_FilteringClippingAndRestoringPreservesInstancesAndSelection()
    {
        var first = new DownloadPackage { Name = "Match one", IsSelected = true, IsExpanded = false };
        var second = new DownloadPackage { Name = "Other" };
        var third = new DownloadPackage { Name = "Match two" };
        var clipped = new DownloadPackage { Name = "Match child", ParentPackageId = first.Id };
        foreach (var package in new[] { first, second, third, clipped }) _viewModel.Packages.Add(package);
        var actions = new List<NotifyCollectionChangedAction>();
        _viewModel.RootPackages.CollectionChanged += (_, args) => actions.Add(args.Action);

        _viewModel.SearchFilterText = "match";
        AssertReferences(new[] { first, third }, _viewModel.RootPackages);
        _viewModel.SearchFilterText = "MATCH";
        AssertReferences(new[] { first, third }, _viewModel.RootPackages);
        _viewModel.SearchFilterText = string.Empty;
        AssertReferences(new[] { first, second, third }, _viewModel.RootPackages);

        Assert.True(first.IsSelected);
        Assert.False(first.IsExpanded);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Add }, actions);
    }

    [Fact]
    public void Refresh_UsesMovesForSortingAndRestoresNaturalOrder()
    {
        var first = new DownloadPackage { Name = "Bravo", IsSelected = true };
        var second = new DownloadPackage { Name = "Charlie" };
        var third = new DownloadPackage { Name = "Alpha" };
        foreach (var package in new[] { first, second, third }) _viewModel.Packages.Add(package);
        var actions = new List<NotifyCollectionChangedAction>();
        _viewModel.RootPackages.CollectionChanged += (_, args) => actions.Add(args.Action);

        _viewModel.ToggleColumnSort("Name");
        _viewModel.RefreshRootPackages();
        AssertReferences(new[] { third, first, second }, _viewModel.RootPackages);
        var inserted = new DownloadPackage { Name = "Beta" };
        _viewModel.Packages.Add(inserted);
        AssertReferences(new[] { third, inserted, first, second }, _viewModel.RootPackages);
        _viewModel.ToggleColumnSort("Name");
        _viewModel.RefreshRootPackages();
        AssertReferences(new[] { second, first, inserted, third }, _viewModel.RootPackages);
        _viewModel.ClearColumnSort();
        _viewModel.RefreshRootPackages();
        AssertReferences(new[] { first, second, third, inserted }, _viewModel.RootPackages);

        Assert.True(first.IsSelected);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Remove, actions);
        Assert.Equal(1, actions.Count(action => action == NotifyCollectionChangedAction.Add));
        Assert.Contains(NotifyCollectionChangedAction.Move, actions);
    }

    [Fact]
    public void Refresh_ReconcilesStaleMissingAndReorderedRootsWithoutReset()
    {
        var packages = Enumerable.Range(0, 4).Select(index => new DownloadPackage { Name = $"Package {index}" }).ToArray();
        foreach (var package in packages) _viewModel.Packages.Add(package);
        _viewModel.RootPackages.Clear();
        _viewModel.RootPackages.Add(packages[2]);
        _viewModel.RootPackages.Add(new DownloadPackage { Name = "Removed package" });
        _viewModel.RootPackages.Add(packages[0]);
        var actions = new List<NotifyCollectionChangedAction>();
        _viewModel.RootPackages.CollectionChanged += (_, args) => actions.Add(args.Action);

        _viewModel.RefreshRootPackages();

        AssertReferences(packages, _viewModel.RootPackages);
        Assert.Equal(new[]
        {
            NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Move,
            NotifyCollectionChangedAction.Add, NotifyCollectionChangedAction.Add
        }, actions);
    }

    [Fact]
    public void Refresh_DistinguishesPackageInstancesWithMatchingNamesAndIds()
    {
        var first = new DownloadPackage { Name = "Same name" };
        var second = new DownloadPackage { Name = "Same name", Id = first.Id, IsSelected = true };
        _viewModel.Packages.Add(first);
        _viewModel.Packages.Add(second);
        AssertReferences(new[] { first, second }, _viewModel.RootPackages);

        _viewModel.Packages.Remove(first);
        _viewModel.RefreshRootPackages();

        AssertReferences(new[] { second }, _viewModel.RootPackages);
        Assert.True(second.IsSelected);
    }

    private static void AssertReferences(IReadOnlyList<DownloadPackage> expected, IList<DownloadPackage> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++) Assert.Same(expected[i], actual[i]);
    }

    private sealed class ComparisonCountingPackage : DownloadPackage
    {
        internal static int Comparisons;
        public override bool Equals(object? other)
        {
            Comparisons++;
            return ReferenceEquals(this, other);
        }
        public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
    }
}
