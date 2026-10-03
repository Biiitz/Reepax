using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using Reepax.Converters;
using Reepax.Models;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

[Collection("SharedQueue")]
public class ColumnSortingTests
{
    [Fact]
    public void ToggleColumnSort_Size_CyclesThroughDescendingAscendingOff()
    {
        var vm = new MainViewModel();
        vm.ClearColumnSort();

        // Initial state: no sort
        Assert.Null(vm.SortColumn);
        Assert.Null(vm.SortDirection);

        // Click 1: Size -> Descending (größer / largest first)
        vm.ToggleColumnSort("Size");
        Assert.Equal("Size", vm.SortColumn);
        Assert.Equal(ListSortDirection.Descending, vm.SortDirection);

        // Click 2: Size -> Ascending (kleiner / smallest first)
        vm.ToggleColumnSort("Size");
        Assert.Equal("Size", vm.SortColumn);
        Assert.Equal(ListSortDirection.Ascending, vm.SortDirection);

        // Click 3: Size -> Off (gehts wieder aus)
        vm.ToggleColumnSort("Size");
        Assert.Null(vm.SortColumn);
        Assert.Null(vm.SortDirection);
    }

    [Fact]
    public void ToggleColumnSort_Name_CyclesThroughAscendingDescendingOff()
    {
        var vm = new MainViewModel();
        vm.ClearColumnSort();

        // Click 1: Name -> Ascending (A to Z)
        vm.ToggleColumnSort("Name");
        Assert.Equal("Name", vm.SortColumn);
        Assert.Equal(ListSortDirection.Ascending, vm.SortDirection);

        // Click 2: Name -> Descending (Z to A)
        vm.ToggleColumnSort("Name");
        Assert.Equal("Name", vm.SortColumn);
        Assert.Equal(ListSortDirection.Descending, vm.SortDirection);

        // Click 3: Name -> Off
        vm.ToggleColumnSort("Name");
        Assert.Null(vm.SortColumn);
        Assert.Null(vm.SortDirection);
    }

    [Fact]
    public void ToggleColumnSort_SwitchingColumns_ClearsPreviousColumnSort()
    {
        var vm = new MainViewModel();
        vm.ClearColumnSort();

        // Click Size -> Size Descending
        vm.ToggleColumnSort("Size");
        Assert.Equal("Size", vm.SortColumn);
        Assert.Equal(ListSortDirection.Descending, vm.SortDirection);

        // Click Name -> Name Ascending (Size cleared)
        vm.ToggleColumnSort("Name");
        Assert.Equal("Name", vm.SortColumn);
        Assert.Equal(ListSortDirection.Ascending, vm.SortDirection);

        // Click Speed -> Speed Descending (Name cleared)
        vm.ToggleColumnSort("Speed");
        Assert.Equal("Speed", vm.SortColumn);
        Assert.Equal(ListSortDirection.Descending, vm.SortDirection);

        vm.ClearColumnSort();
    }

    [Fact]
    public void ApplyCurrentSort_SortsPackagesAndItems_BySize()
    {
        var vm = new MainViewModel();
        vm.ClearColumnSort();
        vm.Packages.Clear();

        var p1 = new DownloadPackage { Name = "Small", TotalBytes = 100_000 };
        p1.Items.Add(new DownloadItem { FileName = "s2.zip", TotalBytes = 70_000 });
        p1.Items.Add(new DownloadItem { FileName = "s1.zip", TotalBytes = 30_000 });

        var p2 = new DownloadPackage { Name = "Huge", TotalBytes = 5_000_000_000 };
        p2.Items.Add(new DownloadItem { FileName = "h1.iso", TotalBytes = 2_000_000_000 });
        p2.Items.Add(new DownloadItem { FileName = "h2.iso", TotalBytes = 3_000_000_000 });

        var p3 = new DownloadPackage { Name = "Medium", TotalBytes = 50_000_000 };
        p3.Items.Add(new DownloadItem { FileName = "m.mp4", TotalBytes = 50_000_000 });

        vm.Packages.Add(p1);
        vm.Packages.Add(p2);
        vm.Packages.Add(p3);
        vm.RefreshRootPackages();

        // 1st click on Size: Descending (Huge -> Medium -> Small)
        vm.ToggleColumnSort("Size");
        Assert.Equal(3, vm.RootPackages.Count);
        Assert.Equal("Huge", vm.RootPackages[0].Name);
        Assert.Equal("Medium", vm.RootPackages[1].Name);
        Assert.Equal("Small", vm.RootPackages[2].Name);

        // Huge's items should also be sorted descending (h2: 3GB -> h1: 2GB)
        Assert.Equal("h2.iso", p2.Items[0].FileName);
        Assert.Equal("h1.iso", p2.Items[1].FileName);

        // 2nd click on Size: Ascending (Small -> Medium -> Huge)
        vm.ToggleColumnSort("Size");
        Assert.Equal("Small", vm.RootPackages[0].Name);
        Assert.Equal("Medium", vm.RootPackages[1].Name);
        Assert.Equal("Huge", vm.RootPackages[2].Name);

        // Huge's items should be sorted ascending (h1: 2GB -> h2: 3GB)
        Assert.Equal("h1.iso", p2.Items[0].FileName);
        Assert.Equal("h2.iso", p2.Items[1].FileName);

        // 3rd click on Size: Off -> Original order restored (p1, p2, p3)
        vm.ToggleColumnSort("Size");
        Assert.Equal("Small", vm.RootPackages[0].Name);
        Assert.Equal("Huge", vm.RootPackages[1].Name);
        Assert.Equal("Medium", vm.RootPackages[2].Name);

        // Items original order restored
        Assert.Equal("s2.zip", p1.Items[0].FileName);
        Assert.Equal("s1.zip", p1.Items[1].FileName);

        vm.ClearColumnSort();
        vm.Packages.Clear();
    }

    [Fact]
    public void ApplyCurrentSort_SortsPackagesAndItems_ByName()
    {
        var vm = new MainViewModel();
        vm.ClearColumnSort();
        vm.Packages.Clear();

        var p1 = new DownloadPackage { Name = "Zulu" };
        p1.Items.Add(new DownloadItem { FileName = "beta.rar" });
        p1.Items.Add(new DownloadItem { FileName = "alpha.rar" });

        var p2 = new DownloadPackage { Name = "Alpha" };
        var p3 = new DownloadPackage { Name = "Mike" };

        vm.Packages.Add(p1);
        vm.Packages.Add(p2);
        vm.Packages.Add(p3);
        vm.RefreshRootPackages();

        // 1st click on Name: Ascending (Alpha, Mike, Zulu)
        vm.ToggleColumnSort("Name");
        Assert.Equal("Alpha", vm.RootPackages[0].Name);
        Assert.Equal("Mike", vm.RootPackages[1].Name);
        Assert.Equal("Zulu", vm.RootPackages[2].Name);

        // Items inside p1 sorted A-Z (alpha, beta)
        Assert.Equal("alpha.rar", p1.Items[0].FileName);
        Assert.Equal("beta.rar", p1.Items[1].FileName);

        // 2nd click on Name: Descending (Zulu, Mike, Alpha)
        vm.ToggleColumnSort("Name");
        Assert.Equal("Zulu", vm.RootPackages[0].Name);
        Assert.Equal("Mike", vm.RootPackages[1].Name);
        Assert.Equal("Alpha", vm.RootPackages[2].Name);

        // Items inside p1 sorted Z-A (beta, alpha)
        Assert.Equal("beta.rar", p1.Items[0].FileName);
        Assert.Equal("alpha.rar", p1.Items[1].FileName);

        // 3rd click: Off -> Original order restored
        vm.ToggleColumnSort("Name");
        Assert.Equal("Zulu", vm.RootPackages[0].Name);
        Assert.Equal("Alpha", vm.RootPackages[1].Name);
        Assert.Equal("Mike", vm.RootPackages[2].Name);

        vm.ClearColumnSort();
        vm.Packages.Clear();
    }

    [Fact]
    public void SortArrowConverters_ReturnCorrectVisibilityAndGeometry()
    {
        var visConv = new ColumnSortArrowVisibilityConverter();
        var geomConv = new ColumnSortArrowGeometryConverter();

        // Case 1: Unsorted -> Collapsed
        var visUnsorted = visConv.Convert(new object[] { null!, null! }, typeof(Visibility), "Size", CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Collapsed, visUnsorted);

        // Case 2: Sorted by Size Descending -> Size is Visible, Name is Collapsed
        var visSize = visConv.Convert(new object[] { "Size", ListSortDirection.Descending }, typeof(Visibility), "Size", CultureInfo.InvariantCulture);
        var visName = visConv.Convert(new object[] { "Size", ListSortDirection.Descending }, typeof(Visibility), "Name", CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Visible, visSize);
        Assert.Equal(Visibility.Collapsed, visName);

        // Case 3: Geometry for Descending vs Ascending
        var geomDesc = geomConv.Convert(new object[] { "Size", ListSortDirection.Descending }, typeof(object), "Size", CultureInfo.InvariantCulture);
        var geomAsc = geomConv.Convert(new object[] { "Size", ListSortDirection.Ascending }, typeof(object), "Size", CultureInfo.InvariantCulture);
        Assert.NotNull(geomDesc);
        Assert.NotNull(geomAsc);
        Assert.NotEqual(geomDesc, geomAsc);
    }

    [Fact]
    public void ResetColumns_ClearsActiveSort()
    {
        var vm = new MainViewModel();
        vm.ToggleColumnSort("Speed");
        Assert.Equal("Speed", vm.SortColumn);

        vm.ResetColumnsCommand.Execute(null);
        Assert.Null(vm.SortColumn);
        Assert.Null(vm.SortDirection);
    }
}
