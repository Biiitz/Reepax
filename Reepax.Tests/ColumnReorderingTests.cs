using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Reepax.Models;
using Reepax.ViewModels;
using Reepax.Services.Storage;
using Xunit;

namespace Reepax.Tests;

public class ColumnReorderingTests : System.IDisposable
{
    public ColumnReorderingTests()
    {
        SettingsService.Instance.Settings.ColumnOrder = new List<string>(AppSettings.DefaultColumnOrder);
        SettingsService.Instance.Settings.ShowColSize = true;
        SettingsService.Instance.Settings.ShowColActions = true;
        SettingsService.Instance.SaveSettings();
    }

    public void Dispose()
    {
        SettingsService.Instance.Settings.ColumnOrder = new List<string>(AppSettings.DefaultColumnOrder);
        SettingsService.Instance.Settings.ShowColSize = true;
        SettingsService.Instance.Settings.ShowColActions = true;
        SettingsService.Instance.SaveSettings();
    }

    [Fact]
    public void DefaultColumnOrder_ContainsAllTwelveColumnsInStandardOrder()
    {
        var defaultOrder = AppSettings.DefaultColumnOrder;
        Assert.Equal(12, defaultOrder.Length);
        Assert.Equal("Name", defaultOrder[0]);
        Assert.Equal("Hoster", defaultOrder[1]);
        Assert.Equal("SavePath", defaultOrder[2]);
        Assert.Equal("Size", defaultOrder[3]);
        Assert.Equal("Progress", defaultOrder[4]);
        Assert.Equal("Speed", defaultOrder[5]);
        Assert.Equal("Eta", defaultOrder[6]);
        Assert.Equal("Status", defaultOrder[7]);
        Assert.Equal("AddedDate", defaultOrder[8]);
        Assert.Equal("CompletedDate", defaultOrder[9]);
        Assert.Equal("Checksum", defaultOrder[10]);
        Assert.Equal("Actions", defaultOrder[11]);
    }

    [Fact]
    public void SanitizeColumnOrder_WhenNullOrEmpty_ReturnsDefaultOrder()
    {
        var sanitizedNull = AppSettings.SanitizeColumnOrder(null);
        Assert.Equal(AppSettings.DefaultColumnOrder, sanitizedNull);

        var sanitizedEmpty = AppSettings.SanitizeColumnOrder(new List<string>());
        Assert.Equal(AppSettings.DefaultColumnOrder, sanitizedEmpty);
    }

    [Fact]
    public void SanitizeColumnOrder_WithDuplicatesAndInvalidColumns_CleansAndAppendsMissing()
    {
        var input = new List<string> { "Size", "size", "UNKNOWN_COL", "Progress" };
        var sanitized = AppSettings.SanitizeColumnOrder(input);

        Assert.Equal(12, sanitized.Count);
        Assert.Equal("Size", sanitized[0]);
        Assert.Equal("Progress", sanitized[1]);
        // All other 10 standard columns appended in default relative order
        Assert.DoesNotContain("UNKNOWN_COL", sanitized);
        Assert.Equal(12, sanitized.Distinct().Count());
    }

    [Fact]
    public void MoveColumn_ValidIndices_MovesColumnCorrectly()
    {
        var vm = new MainViewModel();
        vm.ColumnOrder = new List<string>(AppSettings.DefaultColumnOrder);

        // Move "Size" (index 3) to index 0 (front)
        vm.MoveColumn("Size", 0);
        Assert.Equal("Size", vm.ColumnOrder[0]);
        Assert.Equal("Name", vm.ColumnOrder[1]);
        Assert.Equal(0, vm.ColIndexSize);
        Assert.Equal(1, vm.ColIndexName);

        // Move "Size" from index 0 to index 5
        vm.MoveColumn("Size", 5);
        Assert.Equal("Size", vm.ColumnOrder[5]);
        Assert.Equal(5, vm.ColIndexSize);
        Assert.Equal(12, vm.ColumnOrder.Distinct().Count());
    }

    [Fact]
    public void MoveColumn_OutOfBounds_ClampsCleanly()
    {
        var vm = new MainViewModel();
        vm.ColumnOrder = new List<string>(AppSettings.DefaultColumnOrder);

        // Move "Name" to negative index
        vm.MoveColumn("Name", -5);
        Assert.Equal("Name", vm.ColumnOrder[0]);

        // Move "Name" beyond maximum index
        vm.MoveColumn("Name", 999);
        Assert.Equal("Name", vm.ColumnOrder[11]);
        Assert.Equal(11, vm.ColIndexName);
    }

    [Fact]
    public void MoveColumnBefore_PutsSourceDirectlyBeforeTarget()
    {
        var vm = new MainViewModel();
        vm.ColumnOrder = new List<string>(AppSettings.DefaultColumnOrder);

        // Move "Eta" right before "Name"
        vm.MoveColumnBefore("Eta", "Name");
        Assert.Equal(0, vm.ColIndexEta);
        Assert.Equal(1, vm.ColIndexName);

        // Move "Checksum" right before "Speed"
        int speedIndexBefore = vm.ColIndexSpeed;
        vm.MoveColumnBefore("Checksum", "Speed");
        Assert.Equal(vm.ColIndexSpeed - 1, vm.ColIndexChecksum);
        Assert.Equal(12, vm.ColumnOrder.Distinct().Count());
    }

    [Fact]
    public void MoveColumnAfter_PutsSourceDirectlyAfterTarget()
    {
        var vm = new MainViewModel();
        vm.ColumnOrder = new List<string>(AppSettings.DefaultColumnOrder);

        // Move "Name" right after "Actions" (to the end)
        vm.MoveColumnAfter("Name", "Actions");
        Assert.Equal(11, vm.ColIndexName);
        Assert.Equal("Name", vm.ColumnOrder[11]);
        Assert.Equal(12, vm.ColumnOrder.Distinct().Count());
    }

    [Fact]
    public void ResetColumnWidths_RestoresDefaultColumnOrder()
    {
        var vm = new MainViewModel();
        vm.MoveColumn("Actions", 0);
        vm.MoveColumn("Checksum", 1);
        Assert.NotEqual(AppSettings.DefaultColumnOrder, vm.ColumnOrder);

        vm.ResetColumnWidths();
        Assert.Equal(AppSettings.DefaultColumnOrder, vm.ColumnOrder);
        Assert.Equal(0, vm.ColIndexName);
        Assert.Equal(11, vm.ColIndexActions);
    }

    [Fact]
    public void SlotWidths_ReflectWidthOfColumnPlacedInThatSlot()
    {
        var vm = new MainViewModel();
        vm.ShowColSize = true;
        vm.ColumnOrder = new List<string>(AppSettings.DefaultColumnOrder);
        vm.ColWidthSize = 123.0;

        // In default order, Size is slot 3
        Assert.Equal(3, vm.ColIndexSize);
        Assert.Equal(123.0, vm.ActualColWidthSlot3);

        // Move Size to slot 0
        vm.MoveColumn("Size", 0);
        Assert.Equal(0, vm.ColIndexSize);
        Assert.Equal(123.0, vm.ActualColWidthSlot0);
    }

    [Fact]
    public void GetLastVisibleColumn_RespectsDynamicColumnOrder()
    {
        var vm = new MainViewModel();
        vm.ShowColActions = true;
        vm.ShowColSize = true;
        vm.ColumnOrder = new List<string>(AppSettings.DefaultColumnOrder);

        // By default with all columns visible, last visible is Actions
        Assert.Equal("Actions", MainWindow.GetLastVisibleColumn(vm));

        // Move "Size" to the very end
        vm.MoveColumn("Size", 11);
        Assert.Equal("Size", MainWindow.GetLastVisibleColumn(vm));

        // When "Size" is hidden, last visible should be the one before it in ColumnOrder
        vm.ShowColSize = false;
        Assert.Equal("Actions", MainWindow.GetLastVisibleColumn(vm));

        // Restore visibility
        vm.ShowColSize = true;
    }

    [Fact]
    public void ReorderingColumns_DoesNotAffectActiveSortState()
    {
        var vm = new MainViewModel();
        vm.ToggleColumnSort("Size"); // Size descending
        Assert.Equal("Size", vm.SortColumn);
        Assert.Equal(ListSortDirection.Descending, vm.SortDirection);

        // Move Size from slot 3 to slot 0
        vm.MoveColumn("Size", 0);

        // Sort remains active and intact
        Assert.Equal("Size", vm.SortColumn);
        Assert.Equal(ListSortDirection.Descending, vm.SortDirection);

        vm.ClearColumnSort();
    }

    [Fact]
    public void CalculateDraggedHeaderBarLeft_TracksMouseOffsetAndClampsToContainer()
    {
        // 100px bar in 800px container, grabbed 30px from left edge
        double containerWidth = 800;
        double barWidth = 100;
        double grabOffsetX = 30;

        // Normal dragging
        double left = MainWindow.CalculateDraggedHeaderBarLeft(130, grabOffsetX, containerWidth, barWidth);
        Assert.Equal(100, left);

        // Dragged to the left beyond zero -> clamped to 0
        double clampLeft = MainWindow.CalculateDraggedHeaderBarLeft(10, grabOffsetX, containerWidth, barWidth);
        Assert.Equal(0, clampLeft);

        // Dragged to the right beyond container bounds -> clamped to 800 - 100 = 700
        double clampRight = MainWindow.CalculateDraggedHeaderBarLeft(900, grabOffsetX, containerWidth, barWidth);
        Assert.Equal(700, clampRight);
    }

    [Fact]
    public void CalculateDropInsertion_ReturnsCorrectTargetAndIndicatorX()
    {
        var vm = new MainViewModel();
        vm.ColumnOrder = new List<string>(AppSettings.DefaultColumnOrder);

        // Name (220px: 0..220), Hoster (75px: 220..295)
        // Midpoint of Name is 110. Mouse at 50 should target before Name at indicatorX = 0
        var (target1, atEnd1, indicatorX1) = MainWindow.CalculateDropInsertion(vm, 50);
        Assert.Equal("Name", target1);
        Assert.False(atEnd1);
        Assert.Equal(0, indicatorX1);

        // Midpoint of Hoster is 220 + 37.5 = 257.5. Mouse at 230 should target before Hoster at indicatorX = 220
        var (target2, atEnd2, indicatorX2) = MainWindow.CalculateDropInsertion(vm, 230);
        Assert.Equal("Hoster", target2);
        Assert.False(atEnd2);
        Assert.Equal(220, indicatorX2);

        // Mouse at 9999 (far right) should drop at end
        var (targetEnd, atEnd3, indicatorXEnd) = MainWindow.CalculateDropInsertion(vm, 9999);
        Assert.Null(targetEnd);
        Assert.True(atEnd3);
        Assert.True(indicatorXEnd > 0);
    }
}

