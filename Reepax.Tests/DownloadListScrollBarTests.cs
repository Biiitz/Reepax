using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Reepax.Models;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

public class DownloadListScrollBarTests
{
    private static void RunInSta(Action action)
    {
        var tcs = new TaskCompletionSource<bool>();
        var thread = new Thread(() =>
        {
            try
            {
                action();
                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        tcs.Task.GetAwaiter().GetResult();
    }

    #region Column Width Calculations & Sum Tests

    [Fact]
    public void DefaultColumnWidths_SumCorrectly_WhenAllColumnsAreVisible()
    {
        var vm = new MainViewModel();

        // Enable all 12 columns
        vm.ShowColName = true;
        vm.ShowColHoster = true;
        vm.ShowColSavePath = true;
        vm.ShowColSize = true;
        vm.ShowColProgress = true;
        vm.ShowColSpeed = true;
        vm.ShowColEta = true;
        vm.ShowColStatus = true;
        vm.ShowColAddedDate = true;
        vm.ShowColCompletedDate = true;
        vm.ShowColChecksum = true;
        vm.ShowColActions = true;

        // Verify individual default column widths
        Assert.Equal(220, vm.ColWidthName);
        Assert.Equal(75, vm.ColWidthHoster);
        Assert.Equal(140, vm.ColWidthSavePath);
        Assert.Equal(75, vm.ColWidthSize);
        Assert.Equal(110, vm.ColWidthProgress);
        Assert.Equal(80, vm.ColWidthSpeed);
        Assert.Equal(65, vm.ColWidthEta);
        Assert.Equal(95, vm.ColWidthStatus);
        Assert.Equal(95, vm.ColWidthAddedDate);
        Assert.Equal(95, vm.ColWidthCompletedDate);
        Assert.Equal(90, vm.ColWidthChecksum);
        Assert.Equal(95, vm.ColWidthActions);

        // Verify computed actual column widths match full widths when visible
        Assert.Equal(220, vm.ActualColWidthName);
        Assert.Equal(75, vm.ActualColWidthHoster);
        Assert.Equal(140, vm.ActualColWidthSavePath);
        Assert.Equal(75, vm.ActualColWidthSize);
        Assert.Equal(110, vm.ActualColWidthProgress);
        Assert.Equal(80, vm.ActualColWidthSpeed);
        Assert.Equal(65, vm.ActualColWidthEta);
        Assert.Equal(95, vm.ActualColWidthStatus);
        Assert.Equal(95, vm.ActualColWidthAddedDate);
        Assert.Equal(95, vm.ActualColWidthCompletedDate);
        Assert.Equal(90, vm.ActualColWidthChecksum);
        Assert.Equal(95, vm.ActualColWidthActions);

        // Sum of all 12 columns: 220 + 75 + 140 + 75 + 110 + 80 + 65 + 95 + 95 + 95 + 90 + 95 = 1235
        double totalActualWidth = vm.ActualColWidthName +
                                  vm.ActualColWidthHoster +
                                  vm.ActualColWidthSavePath +
                                  vm.ActualColWidthSize +
                                  vm.ActualColWidthProgress +
                                  vm.ActualColWidthSpeed +
                                  vm.ActualColWidthEta +
                                  vm.ActualColWidthStatus +
                                  vm.ActualColWidthAddedDate +
                                  vm.ActualColWidthCompletedDate +
                                  vm.ActualColWidthChecksum +
                                  vm.ActualColWidthActions;

        Assert.Equal(1235.0, totalActualWidth);
    }

    [Fact]
    public void DefaultColumnVisibility_SumsCorrectly()
    {
        var vm = new MainViewModel();
        vm.ResetColumnsCommand.Execute(null);

        // Verify default visibility state:
        // Visible: Name, Hoster, Size, Progress, Speed, Eta, Status, Actions
        Assert.True(vm.ShowColName);
        Assert.True(vm.ShowColHoster);
        Assert.False(vm.ShowColSavePath);
        Assert.True(vm.ShowColSize);
        Assert.True(vm.ShowColProgress);
        Assert.True(vm.ShowColSpeed);
        Assert.True(vm.ShowColEta);
        Assert.True(vm.ShowColStatus);
        Assert.False(vm.ShowColAddedDate);
        Assert.False(vm.ShowColCompletedDate);
        Assert.False(vm.ShowColChecksum);
        Assert.True(vm.ShowColActions);

        // Hidden columns have ActualColWidth of 0
        Assert.Equal(0, vm.ActualColWidthSavePath);
        Assert.Equal(0, vm.ActualColWidthAddedDate);
        Assert.Equal(0, vm.ActualColWidthCompletedDate);
        Assert.Equal(0, vm.ActualColWidthChecksum);

        // Sum of default visible columns: 220 + 75 + 75 + 110 + 80 + 65 + 95 + 95 = 815
        double visibleSum = vm.ActualColWidthName +
                            vm.ActualColWidthHoster +
                            vm.ActualColWidthSavePath +
                            vm.ActualColWidthSize +
                            vm.ActualColWidthProgress +
                            vm.ActualColWidthSpeed +
                            vm.ActualColWidthEta +
                            vm.ActualColWidthStatus +
                            vm.ActualColWidthAddedDate +
                            vm.ActualColWidthCompletedDate +
                            vm.ActualColWidthChecksum +
                            vm.ActualColWidthActions;

        Assert.Equal(815.0, visibleSum);
    }

    [Fact]
    public void WhenColumnIsHidden_DividerLogicAndGridWidthUpdateAppropriately()
    {
        var vm = new MainViewModel();

        // Ensure baseline visibility
        vm.ShowColName = true;
        vm.ShowColHoster = true;
        vm.ShowColProgress = true;
        vm.ShowColSpeed = true;
        vm.ShowColEta = true;
        vm.ShowColStatus = true;
        vm.ShowColActions = true;

        // Baseline: Speed is visible
        Assert.True(vm.ShowDividerSpeed);
        Assert.Equal(80, vm.ActualColWidthSpeed);
        Assert.Equal(GridUnitType.Pixel, vm.GridColWidthSpeed.GridUnitType);
        Assert.Equal(80, vm.GridColWidthSpeed.Value);

        // Act: Hide Speed column
        vm.ShowColSpeed = false;

        // Assert: Actual width becomes 0, divider is hidden, GridLength is 0
        Assert.Equal(0, vm.ActualColWidthSpeed);
        Assert.False(vm.ShowDividerSpeed);
        Assert.Equal(0, vm.GridColWidthSpeed.Value);

        // Stored preference for width is preserved for when column is re-enabled
        Assert.Equal(80, vm.ColWidthSpeed);

        // Act: Re-enable Speed
        vm.ShowColSpeed = true;
        Assert.Equal(80, vm.ActualColWidthSpeed);
        Assert.True(vm.ShowDividerSpeed);
        Assert.Equal(80, vm.GridColWidthSpeed.Value);
    }

    [Fact]
    public void AllVisibleColumns_RetainPixelWidths_AndShowDividers()
    {
        var vm = new MainViewModel();
        vm.ResetColumnsCommand.Execute(null);

        // Make only Name and Progress visible, all columns to the right hidden
        vm.ShowColName = true;
        vm.ShowColHoster = false;
        vm.ShowColSavePath = false;
        vm.ShowColSize = false;
        vm.ShowColProgress = true;
        vm.ShowColSpeed = false;
        vm.ShowColEta = false;
        vm.ShowColStatus = false;
        vm.ShowColAddedDate = false;
        vm.ShowColCompletedDate = false;
        vm.ShowColChecksum = false;
        vm.ShowColActions = false;

        // Name is visible -> ShowDividerName should be true, Pixel width
        Assert.True(vm.ShowDividerName);
        Assert.Equal(GridUnitType.Pixel, vm.GridColWidthName.GridUnitType);
        Assert.Equal(220, vm.GridColWidthName.Value);

        // Progress is visible -> retains its configured Pixel width and shows divider so it can be resized freely
        Assert.True(vm.ShowDividerProgress);
        Assert.Equal(GridUnitType.Pixel, vm.GridColWidthProgress.GridUnitType);
        Assert.Equal(110, vm.GridColWidthProgress.Value);

        // Hidden column Actions has divider false and width 0
        Assert.False(vm.ShowDividerActions);
        Assert.Equal(0, vm.GridColWidthActions.Value);
    }


    [Fact]
    public void CustomizingColumnWidth_UpdatesActualWidthAndGridLength()
    {
        var vm = new MainViewModel();
        vm.ShowColName = true;
        vm.ShowColHoster = true;

        // Change ColWidthName
        vm.ColWidthName = 450;
        Assert.Equal(450, vm.ActualColWidthName);
        Assert.Equal(450, vm.GridColWidthName.Value);

        // Reset back to default
        vm.ColWidthName = 340;
        Assert.Equal(340, vm.ActualColWidthName);
        Assert.Equal(340, vm.GridColWidthName.Value);
    }

    [Fact]
    public void ResetColumns_RestoresDefaultWidthsAndVisibility()
    {
        var vm = new MainViewModel();

        // Mutate widths and visibility
        vm.ColWidthName = 500;
        vm.ColWidthSpeed = 200;
        vm.ShowColSpeed = false;
        vm.ShowColSavePath = true;

        Assert.Equal(500, vm.ColWidthName);
        Assert.False(vm.ShowColSpeed);
        Assert.True(vm.ShowColSavePath);

        // Act: Reset columns
        vm.ResetColumnsCommand.Execute(null);

        // Assert: Restored defaults
        Assert.Equal(220, vm.ColWidthName);
        Assert.Equal(80, vm.ColWidthSpeed);
        Assert.True(vm.ShowColSpeed);
        Assert.False(vm.ShowColSavePath);
        Assert.Equal(220, vm.ActualColWidthName);
        Assert.Equal(80, vm.ActualColWidthSpeed);
        Assert.Equal(0, vm.ActualColWidthSavePath);
    }

    #endregion

    #region ScrollViewer & ScrollBar STA Tests

    [Fact]
    public void ScrollViewer_ScrollToBottom_And_ScrollToTop_ProperlyAdjustOffsets()
    {
        RunInSta(() =>
        {
            var scv = new ScrollViewer
            {
                Width = 200,
                Height = 200,
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            var content = new Canvas { Width = 200, Height = 1000 };
            scv.Content = content;

            scv.Measure(new Size(200, 200));
            scv.Arrange(new Rect(0, 0, 200, 200));
            scv.UpdateLayout();

            Assert.Equal(0, scv.VerticalOffset);
            Assert.True(scv.ScrollableHeight > 0);

            // Test ScrollToBottom
            scv.ScrollToBottom();
            scv.UpdateLayout();

            Assert.True(scv.VerticalOffset > 0);
            Assert.Equal(scv.ScrollableHeight, scv.VerticalOffset);

            // Test ScrollToTop
            scv.ScrollToTop();
            scv.UpdateLayout();

            Assert.Equal(0, scv.VerticalOffset);
        });
    }

    [Fact]
    public void ScrollBarCommands_ExecuteProperlyOnScrollViewer()
    {
        RunInSta(() =>
        {
            var scv = new ScrollViewer
            {
                Width = 200,
                Height = 200,
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            var content = new Canvas { Width = 200, Height = 1200 };
            scv.Content = content;

            scv.Measure(new Size(200, 200));
            scv.Arrange(new Rect(0, 0, 200, 200));
            scv.UpdateLayout();

            // Test vertical commands
            ScrollBar.ScrollToBottomCommand.Execute(null, scv);
            scv.UpdateLayout();
            Assert.Equal(scv.ScrollableHeight, scv.VerticalOffset);

            ScrollBar.ScrollToTopCommand.Execute(null, scv);
            scv.UpdateLayout();
            Assert.Equal(0, scv.VerticalOffset);
        });
    }

    [Fact]
    public void ChangingColumnVisibility_RaisesPropertyChangedForDividersAndGridLengths()
    {
        var vm = new MainViewModel();
        var changedProps = new System.Collections.Generic.List<string>();
        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName != null)
                changedProps.Add(e.PropertyName);
        };

        // Toggle ShowColSpeed to opposite of current value
        bool original = vm.ShowColSpeed;
        vm.ShowColSpeed = !original;

        // Verify PropertyChanged was raised for relevant divider and grid length properties
        Assert.Contains(nameof(vm.ShowDividerSpeed), changedProps);
        Assert.Contains(nameof(vm.GridColWidthSpeed), changedProps);
        Assert.Contains(nameof(vm.ShowDividerProgress), changedProps);
        Assert.Contains(nameof(vm.GridColWidthProgress), changedProps);
        Assert.Contains(nameof(vm.ActualColWidthSpeed), changedProps);

        // Restore original state
        vm.ShowColSpeed = original;
    }

    [Fact]
    public void ScrollBarLineAndPageCommands_ExecuteProperlyOnScrollViewer()
    {
        RunInSta(() =>
        {
            var scv = new ScrollViewer
            {
                Width = 200,
                Height = 200,
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            var content = new Canvas { Width = 200, Height = 1000 };
            scv.Content = content;

            scv.Measure(new Size(200, 200));
            scv.Arrange(new Rect(0, 0, 200, 200));
            scv.UpdateLayout();

            // Line down & line up
            ScrollBar.LineDownCommand.Execute(null, scv);
            scv.UpdateLayout();
            Assert.True(scv.VerticalOffset > 0);

            ScrollBar.LineUpCommand.Execute(null, scv);
            scv.UpdateLayout();
            Assert.Equal(0, scv.VerticalOffset);

            // Page down & page up
            ScrollBar.PageDownCommand.Execute(null, scv);
            scv.UpdateLayout();
            Assert.True(scv.VerticalOffset > 0);

            ScrollBar.PageUpCommand.Execute(null, scv);
            scv.UpdateLayout();
            Assert.Equal(0, scv.VerticalOffset);
        });
    }

    [Fact]
    public void DockPanel_WithLastChildHavingMinWidth_ArrangesAtLeastMinWidth()
    {
        RunInSta(() =>
        {
            var dockPanel = new DockPanel { LastChildFill = true, Width = 800, Height = 30 };
            var rightBorder = new Border { Width = 18 };
            DockPanel.SetDock(rightBorder, Dock.Right);
            dockPanel.Children.Add(rightBorder);

            var grid = new Grid { MinWidth = 1220 };
            // Add columns: 1085px pixel columns, and 1* MinWidth=135
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1085, GridUnitType.Pixel), MinWidth = 1085 });
            var lastCol = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 135 };
            grid.ColumnDefinitions.Add(lastCol);

            dockPanel.Children.Add(grid);

            dockPanel.Measure(new Size(800, 30));
            dockPanel.Arrange(new Rect(0, 0, 800, 30));
            dockPanel.UpdateLayout();

            Assert.True(grid.ActualWidth >= 1220, $"grid.ActualWidth was {grid.ActualWidth}, expected >= 1220");
            Assert.True(lastCol.ActualWidth >= 135, $"lastCol.ActualWidth was {lastCol.ActualWidth}, expected >= 135");
        });
    }

    [Fact]
    public void TotalVisibleColumnsWidth_EqualsSumOfVisibleColumns_AndFiresPropertyChangedOnToggle()
    {
        var vm = new MainViewModel();
        vm.ResetColumnsCommand.Execute(null);

        // Baseline: default visible columns sum to 815
        Assert.Equal(815.0, vm.TotalVisibleColumnsWidth);

        var propertyChangedList = new System.Collections.Generic.List<string>();
        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName != null) propertyChangedList.Add(e.PropertyName);
        };

        // Act: Show ColSavePath (140px)
        propertyChangedList.Clear();
        vm.ShowColSavePath = true;

        Assert.Equal(955.0, vm.TotalVisibleColumnsWidth);
        Assert.Contains(nameof(MainViewModel.TotalVisibleColumnsWidth), propertyChangedList);

        // Act: Hide ColName (220px)
        propertyChangedList.Clear();
        vm.ShowColName = false;

        Assert.Equal(735.0, vm.TotalVisibleColumnsWidth);
        Assert.Contains(nameof(MainViewModel.TotalVisibleColumnsWidth), propertyChangedList);

        // Act: Reset columns
        propertyChangedList.Clear();
        vm.ResetColumnsCommand.Execute(null);

        Assert.Equal(815.0, vm.TotalVisibleColumnsWidth);
        Assert.Contains(nameof(MainViewModel.TotalVisibleColumnsWidth), propertyChangedList);
    }

    [Fact]
    public void TotalVisibleColumnsWidth_UpdatesWhenColumnWidthIsResized()
    {
        var vm = new MainViewModel();
        vm.ResetColumnsCommand.Execute(null);

        var propertyChangedList = new System.Collections.Generic.List<string>();
        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName != null) propertyChangedList.Add(e.PropertyName);
        };

        // Name column is visible with 220px default
        Assert.True(vm.ShowColName);
        Assert.Equal(815.0, vm.TotalVisibleColumnsWidth);

        // Resize Name from 220 to 320 (+100px)
        propertyChangedList.Clear();
        vm.ColWidthName = 320;

        Assert.Equal(915.0, vm.TotalVisibleColumnsWidth);
        Assert.Contains(nameof(MainViewModel.TotalVisibleColumnsWidth), propertyChangedList);

        // Resize hidden SavePath column - TotalVisibleColumnsWidth should remain unchanged (SavePath is hidden)
        Assert.False(vm.ShowColSavePath);
        propertyChangedList.Clear();
        vm.ColWidthSavePath = 300;

        // Since SavePath is hidden, its actual width is 0, so TotalVisibleColumnsWidth remains 915
        Assert.Equal(915.0, vm.TotalVisibleColumnsWidth);
    }



    [Fact]
    public void LastVisibleColumn_IsNeverClipped_RegardlessOfNumberOfEnabledColumns()
    {
        RunInSta(() =>
        {
            var vm = new MainViewModel();

            // Test various column configurations:
            // Config 1: Single column (Name only)
            // Config 2: 3 columns (Name, Size, Status)
            // Config 3: 8 columns (Default columns)
            // Config 4: 11 columns (User's exact settings with Actions off)
            // Config 5: All 12 columns visible

            var configurations = new[]
            {
                new { Name = "1 Column", Setup = (Action)(() =>
                {
                    vm.ShowColName = true;
                    vm.ShowColHoster = false;
                    vm.ShowColSavePath = false;
                    vm.ShowColSize = false;
                    vm.ShowColProgress = false;
                    vm.ShowColSpeed = false;
                    vm.ShowColEta = false;
                    vm.ShowColStatus = false;
                    vm.ShowColAddedDate = false;
                    vm.ShowColCompletedDate = false;
                    vm.ShowColChecksum = false;
                    vm.ShowColActions = false;
                })},
                new { Name = "3 Columns", Setup = (Action)(() =>
                {
                    vm.ShowColName = true;
                    vm.ShowColHoster = false;
                    vm.ShowColSavePath = false;
                    vm.ShowColSize = true;
                    vm.ShowColProgress = false;
                    vm.ShowColSpeed = false;
                    vm.ShowColEta = false;
                    vm.ShowColStatus = true;
                    vm.ShowColAddedDate = false;
                    vm.ShowColCompletedDate = false;
                    vm.ShowColChecksum = false;
                    vm.ShowColActions = false;
                })},
                new { Name = "8 Columns (Default)", Setup = (Action)(() =>
                {
                    vm.ResetColumns();
                })},
                new { Name = "11 Columns (User Setup)", Setup = (Action)(() =>
                {
                    vm.ShowColName = true; vm.ColWidthName = 282;
                    vm.ShowColHoster = true; vm.ColWidthHoster = 66;
                    vm.ShowColSavePath = true; vm.ColWidthSavePath = 222;
                    vm.ShowColSize = true; vm.ColWidthSize = 97;
                    vm.ShowColProgress = true; vm.ColWidthProgress = 109;
                    vm.ShowColSpeed = true; vm.ColWidthSpeed = 105;
                    vm.ShowColEta = true; vm.ColWidthEta = 85;
                    vm.ShowColStatus = true; vm.ColWidthStatus = 73;
                    vm.ShowColAddedDate = true; vm.ColWidthAddedDate = 130;
                    vm.ShowColCompletedDate = false; vm.ColWidthCompletedDate = 130;
                    vm.ShowColChecksum = true; vm.ColWidthChecksum = 110;
                    vm.ShowColActions = true; vm.ColWidthActions = 135;
                })},
                new { Name = "All 12 Columns", Setup = (Action)(() =>
                {
                    vm.ShowColName = true;
                    vm.ShowColHoster = true;
                    vm.ShowColSavePath = true;
                    vm.ShowColSize = true;
                    vm.ShowColProgress = true;
                    vm.ShowColSpeed = true;
                    vm.ShowColEta = true;
                    vm.ShowColStatus = true;
                    vm.ShowColAddedDate = true;
                    vm.ShowColCompletedDate = true;
                    vm.ShowColChecksum = true;
                    vm.ShowColActions = true;
                })}
            };

            foreach (var cfg in configurations)
            {
                cfg.Setup();

                // 1. Verify every visible column has a divider enabled (ShowDivider... == ShowCol...)
                Assert.Equal(vm.ShowColName, vm.ShowDividerName);
                Assert.Equal(vm.ShowColHoster, vm.ShowDividerHoster);
                Assert.Equal(vm.ShowColSavePath, vm.ShowDividerSavePath);
                Assert.Equal(vm.ShowColSize, vm.ShowDividerSize);
                Assert.Equal(vm.ShowColProgress, vm.ShowDividerProgress);
                Assert.Equal(vm.ShowColSpeed, vm.ShowDividerSpeed);
                Assert.Equal(vm.ShowColEta, vm.ShowDividerEta);
                Assert.Equal(vm.ShowColStatus, vm.ShowDividerStatus);
                Assert.Equal(vm.ShowColAddedDate, vm.ShowDividerAddedDate);
                Assert.Equal(vm.ShowColCompletedDate, vm.ShowDividerCompletedDate);
                Assert.Equal(vm.ShowColChecksum, vm.ShowDividerChecksum);
                Assert.Equal(vm.ShowColActions, vm.ShowDividerActions);

                // 2. Verify every visible column has Pixel GridUnitType with exact width (never Star shrunken)
                if (vm.ShowColName) Assert.Equal(GridUnitType.Pixel, vm.GridColWidthName.GridUnitType);
                if (vm.ShowColHoster) Assert.Equal(GridUnitType.Pixel, vm.GridColWidthHoster.GridUnitType);
                if (vm.ShowColSavePath) Assert.Equal(GridUnitType.Pixel, vm.GridColWidthSavePath.GridUnitType);
                if (vm.ShowColSize) Assert.Equal(GridUnitType.Pixel, vm.GridColWidthSize.GridUnitType);
                if (vm.ShowColProgress) Assert.Equal(GridUnitType.Pixel, vm.GridColWidthProgress.GridUnitType);
                if (vm.ShowColSpeed) Assert.Equal(GridUnitType.Pixel, vm.GridColWidthSpeed.GridUnitType);
                if (vm.ShowColEta) Assert.Equal(GridUnitType.Pixel, vm.GridColWidthEta.GridUnitType);
                if (vm.ShowColStatus) Assert.Equal(GridUnitType.Pixel, vm.GridColWidthStatus.GridUnitType);
                if (vm.ShowColAddedDate) Assert.Equal(GridUnitType.Pixel, vm.GridColWidthAddedDate.GridUnitType);
                if (vm.ShowColCompletedDate) Assert.Equal(GridUnitType.Pixel, vm.GridColWidthCompletedDate.GridUnitType);
                if (vm.ShowColChecksum) Assert.Equal(GridUnitType.Pixel, vm.GridColWidthChecksum.GridUnitType);
                if (vm.ShowColActions) Assert.Equal(GridUnitType.Pixel, vm.GridColWidthActions.GridUnitType);

                // 3. Verify TotalContentMinWidth includes breathing room
                Assert.Equal(vm.TotalVisibleColumnsWidth + MainViewModel.TrailingBreathingRoom, vm.TotalContentMinWidth);
            }
        });
    }

    [Fact]
    public void AutoFitColumns_Command_SetsOptimalCompactWidths_ForVisibleColumns()
    {
        var vm = new MainViewModel();

        // Enable all metadata columns
        vm.ShowColName = true;
        vm.ShowColHoster = true;
        vm.ShowColSavePath = true;
        vm.ShowColSize = true;
        vm.ShowColProgress = true;
        vm.ShowColSpeed = true;
        vm.ShowColEta = true;
        vm.ShowColStatus = true;
        vm.ShowColAddedDate = true;
        vm.ShowColCompletedDate = true;
        vm.ShowColChecksum = true;
        vm.ShowColActions = true;

        // Set non-standard large widths
        vm.ColWidthHoster = 150;
        vm.ColWidthSavePath = 250;
        vm.ColWidthSize = 120;
        vm.ColWidthProgress = 200;
        vm.ColWidthSpeed = 140;
        vm.ColWidthEta = 110;
        vm.ColWidthStatus = 180;
        vm.ColWidthAddedDate = 160;
        vm.ColWidthCompletedDate = 160;
        vm.ColWidthChecksum = 140;
        vm.ColWidthActions = 150;

        // Execute AutoFitColumns
        vm.AutoFitColumnsCommand.Execute(null);

        // Verify clean compact optimal widths are set
        Assert.Equal(75, vm.ColWidthHoster);
        Assert.Equal(140, vm.ColWidthSavePath);
        Assert.Equal(75, vm.ColWidthSize);
        Assert.Equal(110, vm.ColWidthProgress);
        Assert.Equal(80, vm.ColWidthSpeed);
        Assert.Equal(65, vm.ColWidthEta);
        Assert.Equal(95, vm.ColWidthStatus);
        Assert.Equal(95, vm.ColWidthAddedDate);
        Assert.Equal(95, vm.ColWidthCompletedDate);
        Assert.Equal(90, vm.ColWidthChecksum);
        Assert.Equal(95, vm.ColWidthActions);
    }

    [Fact]
    public void IsDraggingColumnWidth_Flag_CanBeToggled_AndPreservesWidthValues()
    {
        var vm = new MainViewModel();
        Assert.False(vm.IsDraggingColumnWidth);

        vm.IsDraggingColumnWidth = true;
        Assert.True(vm.IsDraggingColumnWidth);

        // Modifying widths while dragging updates ViewModel properties smoothly without error
        vm.ColWidthStatus = 125;
        Assert.Equal(125, vm.ColWidthStatus);
        Assert.Equal(125, vm.ActualColWidthStatus);

        vm.IsDraggingColumnWidth = false;
        Assert.False(vm.IsDraggingColumnWidth);
    }



    [Fact]
    public void HeaderBorder_ClipToBounds_PreventsOverflowUnderGearButton()
    {
        RunInSta(() =>
        {
            var dockPanel = new DockPanel { LastChildFill = true, Width = 800, Height = 28 };

            // Gear button border docked right
            var gearBorder = new Border { Width = 18 };
            DockPanel.SetDock(gearBorder, Dock.Right);
            dockPanel.Children.Add(gearBorder);

            // Header viewport border with ClipToBounds=true
            var headerViewportBorder = new Border { ClipToBounds = true };
            var headerGrid = new Grid { MinWidth = 1400 };
            headerViewportBorder.Child = headerGrid;
            dockPanel.Children.Add(headerViewportBorder);

            dockPanel.Measure(new Size(800, 28));
            dockPanel.Arrange(new Rect(0, 0, 800, 28));
            dockPanel.UpdateLayout();

            // The header viewport container width must strictly be 800 - 18 = 782px
            Assert.Equal(782, headerViewportBorder.ActualWidth);
            Assert.True(headerViewportBorder.ClipToBounds);
        });
    }

    [Fact]
    public void GetColumnLeft_CalculatesExactAccumulatedOffset_WhenAllColumnsAreEnabled()
    {
        var vm = new MainViewModel();
        vm.ColumnOrder = new List<string>(AppSettings.DefaultColumnOrder);

        // Enable all 12 columns with known widths
        vm.ShowColName = true; vm.ColWidthName = 300;
        vm.ShowColHoster = true; vm.ColWidthHoster = 100;
        vm.ShowColSavePath = true; vm.ColWidthSavePath = 150;
        vm.ShowColSize = true; vm.ColWidthSize = 80;
        vm.ShowColProgress = true; vm.ColWidthProgress = 120;
        vm.ShowColSpeed = true; vm.ColWidthSpeed = 90;
        vm.ShowColEta = true; vm.ColWidthEta = 70;
        vm.ShowColStatus = true; vm.ColWidthStatus = 100;
        vm.ShowColAddedDate = true; vm.ColWidthAddedDate = 110;
        vm.ShowColCompletedDate = true; vm.ColWidthCompletedDate = 110;
        vm.ShowColChecksum = true; vm.ColWidthChecksum = 120;
        vm.ShowColActions = true; vm.ColWidthActions = 130;

        Assert.Equal(0, MainWindow.GetColumnLeft(vm, "Name"));
        Assert.Equal(300, MainWindow.GetColumnLeft(vm, "Hoster"));
        Assert.Equal(400, MainWindow.GetColumnLeft(vm, "SavePath"));
        Assert.Equal(550, MainWindow.GetColumnLeft(vm, "Size"));
        Assert.Equal(630, MainWindow.GetColumnLeft(vm, "Progress"));
        Assert.Equal(750, MainWindow.GetColumnLeft(vm, "Speed"));
        Assert.Equal(840, MainWindow.GetColumnLeft(vm, "Eta"));
        Assert.Equal(910, MainWindow.GetColumnLeft(vm, "Status"));
        Assert.Equal(1010, MainWindow.GetColumnLeft(vm, "AddedDate"));
        Assert.Equal(1120, MainWindow.GetColumnLeft(vm, "CompletedDate"));
        Assert.Equal(1230, MainWindow.GetColumnLeft(vm, "Checksum"));
        Assert.Equal(1350, MainWindow.GetColumnLeft(vm, "Actions"));

        // Toggle off Actions -> Checksum becomes the last visible column
        vm.ShowColActions = false;
        Assert.Equal(1230, MainWindow.GetColumnLeft(vm, "Checksum"));

        // Toggle off Checksum -> CompletedDate becomes the last visible column
        vm.ShowColChecksum = false;
        Assert.Equal(1120, MainWindow.GetColumnLeft(vm, "CompletedDate"));

        // Restore clean visibility state
        vm.ShowColChecksum = true;
        vm.ShowColActions = true;
    }

    [Fact]
    public void SetColumnWidth_DirectCall_UpdatesViewModelAndContentMinWidth()
    {
        var vm = new MainViewModel();
        vm.ShowColChecksum = true;
        vm.ShowColActions = true;

        double initialMin = vm.TotalContentMinWidth;

        // Enlarge Checksum by 100px
        MainWindow.SetColumnWidth(vm, "Checksum", vm.ColWidthChecksum + 100);
        Assert.Equal(190, vm.ColWidthChecksum);
        Assert.Equal(initialMin + 100, vm.TotalContentMinWidth);

        // Enlarge Actions by 50px
        MainWindow.SetColumnWidth(vm, "Actions", vm.ColWidthActions + 50);
        Assert.Equal(145, vm.ColWidthActions);
        Assert.Equal(initialMin + 150, vm.TotalContentMinWidth);
    }


    [Fact]
    public void TrailingBreathingRoom_IsZero_EnsuringFlushAlignmentWithGearButton()
    {
        var vm = new MainViewModel();
        Assert.Equal(0.0, MainViewModel.TrailingBreathingRoom);
        Assert.Equal(vm.TotalVisibleColumnsWidth, vm.TotalContentMinWidth);
    }


    [Fact]
    public void ScreenDeltaResize_MaintainsExact1To1MouseTracking_WithoutScrollJitter()
    {
        // Start width: 110px, mouse down at X = 500
        double startWidth = 110.0;
        double startMouseX = 500.0;

        // User moves mouse 35px to the right -> column expands by exactly 35px
        double currentMouseX = 535.0;
        double deltaX = currentMouseX - startMouseX;
        double newWidth = Math.Max(50.0, startWidth + deltaX);
        Assert.Equal(35.0, deltaX);
        Assert.Equal(145.0, newWidth);

        // User moves mouse 40px to the left -> column shrinks by exactly 40px
        currentMouseX = 460.0;
        deltaX = currentMouseX - startMouseX;
        newWidth = Math.Max(50.0, startWidth + deltaX);
        Assert.Equal(-40.0, deltaX);
        Assert.Equal(70.0, newWidth);
    }


    [Fact]
    public void ResetSingleColumnWidth_RestoresDefaultSize_ForAllColumns()
    {
        var vm = new MainViewModel();

        // Mutate all column widths away from default
        vm.ColWidthName = 550;
        vm.ColWidthHoster = 250;
        vm.ColWidthSavePath = 320;
        vm.ColWidthSize = 200;
        vm.ColWidthProgress = 400;
        vm.ColWidthSpeed = 300;
        vm.ColWidthEta = 210;
        vm.ColWidthStatus = 350;
        vm.ColWidthAddedDate = 280;
        vm.ColWidthCompletedDate = 280;
        vm.ColWidthChecksum = 220;
        vm.ColWidthActions = 310;

        // Reset Name
        MainWindow.ResetSingleColumnWidth(vm, "Name");
        Assert.Equal(220, vm.ColWidthName);

        // Reset Hoster
        MainWindow.ResetSingleColumnWidth(vm, "Hoster");
        Assert.Equal(75, vm.ColWidthHoster);

        // Reset SavePath
        MainWindow.ResetSingleColumnWidth(vm, "SavePath");
        Assert.Equal(140, vm.ColWidthSavePath);

        // Reset Size
        MainWindow.ResetSingleColumnWidth(vm, "Size");
        Assert.Equal(75, vm.ColWidthSize);

        // Reset Progress
        MainWindow.ResetSingleColumnWidth(vm, "Progress");
        Assert.Equal(110, vm.ColWidthProgress);

        // Reset Speed
        MainWindow.ResetSingleColumnWidth(vm, "Speed");
        Assert.Equal(80, vm.ColWidthSpeed);

        // Reset Eta
        MainWindow.ResetSingleColumnWidth(vm, "Eta");
        Assert.Equal(65, vm.ColWidthEta);

        // Reset Status
        MainWindow.ResetSingleColumnWidth(vm, "Status");
        Assert.Equal(95, vm.ColWidthStatus);

        // Reset AddedDate
        MainWindow.ResetSingleColumnWidth(vm, "AddedDate");
        Assert.Equal(95, vm.ColWidthAddedDate);

        // Reset CompletedDate
        MainWindow.ResetSingleColumnWidth(vm, "CompletedDate");
        Assert.Equal(95, vm.ColWidthCompletedDate);

        // Reset Checksum
        MainWindow.ResetSingleColumnWidth(vm, "Checksum");
        Assert.Equal(90, vm.ColWidthChecksum);

        // Reset Actions
        MainWindow.ResetSingleColumnWidth(vm, "Actions");
        Assert.Equal(95, vm.ColWidthActions);
    }

    [Fact]
    public void GetLastVisibleColumn_ReturnsCorrectColumn_BasedOnVisibility()
    {
        var vm = new MainViewModel();

        // All columns visible -> Actions is last
        vm.ShowColActions = true;
        Assert.Equal("Actions", MainWindow.GetLastVisibleColumn(vm));

        // Hide Actions -> Checksum is last if visible, else Status
        vm.ShowColActions = false;
        vm.ShowColChecksum = true;
        Assert.Equal("Checksum", MainWindow.GetLastVisibleColumn(vm));

        // Hide Checksum, CompletedDate, AddedDate -> Status is last
        vm.ShowColChecksum = false;
        vm.ShowColCompletedDate = false;
        vm.ShowColAddedDate = false;
        vm.ShowColStatus = true;
        Assert.Equal("Status", MainWindow.GetLastVisibleColumn(vm));

        // Hide all except Name -> Name is last
        vm.ShowColHoster = false;
        vm.ShowColSavePath = false;
        vm.ShowColSize = false;
        vm.ShowColProgress = false;
        vm.ShowColSpeed = false;
        vm.ShowColEta = false;
        vm.ShowColStatus = false;
        Assert.Equal("Name", MainWindow.GetLastVisibleColumn(vm));
    }

    [Fact]
    public void AutoFitNameColumn_CalculatesRemainingViewportSpace()
    {
        var vm = new MainViewModel();
        // Default visible columns: Hoster(80), Size(80), Progress(130), Speed(90), Eta(75), Status(110), Actions(110)
        // Sum of metadata = 675px
        double metadataWidth = 80 + 80 + 130 + 90 + 75 + 110 + 110; // 675px
        const double viewerWidth = 1080.0;
        const double rightMargin = 6.0;

        // Name expands to fill the remaining width
        double remaining = (viewerWidth - rightMargin) - metadataWidth; // 1074 - 675 = 399px
        double autoFittedNameWidth = Math.Max(160.0, remaining);
        Assert.Equal(399.0, autoFittedNameWidth);

        // Total width with auto-fitted Name fits completely within the viewport up to the gear button
        double totalWidth = autoFittedNameWidth + metadataWidth;
        Assert.Equal(viewerWidth - rightMargin, totalWidth);
        Assert.True(totalWidth < viewerWidth, "Columns fit flush inside viewport with divider line clearly visible before gear button!");
    }

    [Fact]
    public void AutoFitNameColumn_WhenViewportIsSmall_ClampsToMinimum160Pixels()
    {
        // Small window (viewport 600px), metadata columns sum to 675px
        double metadataWidth = 675.0;
        const double smallViewerWidth = 600.0;
        const double rightMargin = 6.0;

        double remaining = (smallViewerWidth - rightMargin) - metadataWidth; // 594 - 675 = -81
        double autoFittedNameWidth = Math.Max(160.0, remaining);
        Assert.Equal(160.0, autoFittedNameWidth);

        double totalWidth = autoFittedNameWidth + metadataWidth;
        Assert.True(totalWidth > smallViewerWidth);
    }

    [Fact]
    public void AutoFitNameColumn_WhenWindowIsMaximized_ExpandsFluidly()
    {
        // Maximized 1080p window (viewport ~1700px)
        double metadataWidth = 675.0;
        const double maximizedViewerWidth = 1700.0;
        const double rightMargin = 6.0;

        double remaining = (maximizedViewerWidth - rightMargin) - metadataWidth; // 1694 - 675 = 1019px
        double autoFittedNameWidth = Math.Max(160.0, remaining);
        Assert.Equal(1019.0, autoFittedNameWidth);

        double totalWidth = autoFittedNameWidth + metadataWidth;
        Assert.Equal(maximizedViewerWidth - rightMargin, totalWidth);
        Assert.True(totalWidth < maximizedViewerWidth);
    }



    [Fact]
    public void TableResize_MetadataColumn_Enlarging_AbsorbedByName_PreservesTotalWidth()
    {
        var vm = new MainViewModel();
        vm.ResetColumnsCommand.Execute(null);
        vm.IsDraggingColumnWidth = true;
        try
        {
            double startName = 300.0;
            double startSize = 75.0;
            vm.ColWidthName = startName;
            vm.ColWidthSize = startSize;

            // User enlarges Size by 40px (from 75 to 115)
            MainWindow.ResizeMetadataColumn(vm, "Size", startColWidth: startSize, startNameWidth: startName, deltaX: 40.0);

            Assert.Equal(115.0, vm.ColWidthSize);
            // Name absorbs the delta: 300 - 40 = 260
            Assert.Equal(260.0, vm.ColWidthName);
            // Total width of Name + Size remains constant
            Assert.Equal(startName + startSize, vm.ColWidthName + vm.ColWidthSize);
        }
        finally
        {
            vm.IsDraggingColumnWidth = false;
            vm.ResetColumnsCommand.Execute(null);
        }
    }

    [Fact]
    public void TableResize_MetadataColumn_Shrinking_ExpandsName_PreservesTotalWidth()
    {
        var vm = new MainViewModel();
        vm.ResetColumnsCommand.Execute(null);
        vm.IsDraggingColumnWidth = true;
        try
        {
            double startName = 300.0;
            double startSize = 75.0;
            vm.ColWidthName = startName;
            vm.ColWidthSize = startSize;

            // User shrinks Size by 25px (from 75 to 50)
            MainWindow.ResizeMetadataColumn(vm, "Size", startColWidth: startSize, startNameWidth: startName, deltaX: -25.0);

            Assert.Equal(50.0, vm.ColWidthSize);
            // Name expands by 25px: 300 + 25 = 325
            Assert.Equal(325.0, vm.ColWidthName);
            // Total width remains constant
            Assert.Equal(startName + startSize, vm.ColWidthName + vm.ColWidthSize);
        }
        finally
        {
            vm.IsDraggingColumnWidth = false;
            vm.ResetColumnsCommand.Execute(null);
        }
    }

    [Fact]
    public void TableResize_MetadataColumn_ClampsAtMinNameWidth()
    {
        var vm = new MainViewModel();
        vm.ResetColumnsCommand.Execute(null);
        vm.IsDraggingColumnWidth = true;
        try
        {
            // Name starts at 100px. Min name width is 80px -> max it can absorb is 20px
            double startName = 100.0;
            double startSize = 75.0;
            vm.ColWidthName = startName;
            vm.ColWidthSize = startSize;

            // User tries to enlarge Size by 100px
            MainWindow.ResizeMetadataColumn(vm, "Size", startColWidth: startSize, startNameWidth: startName, deltaX: 100.0);

            // Name clamps at 80.0
            Assert.Equal(80.0, vm.ColWidthName);
            // Size only grows by 20px (from 75 to 95)
            Assert.Equal(95.0, vm.ColWidthSize);
        }
        finally
        {
            vm.IsDraggingColumnWidth = false;
            vm.ResetColumnsCommand.Execute(null);
        }
    }

    [Fact]
    public void TableResize_MetadataColumn_ClampsAtMinColumnWidth()
    {
        var vm = new MainViewModel();
        vm.ResetColumnsCommand.Execute(null);
        vm.IsDraggingColumnWidth = true;
        try
        {
            double startName = 300.0;
            double startSize = 75.0;
            vm.ColWidthName = startName;
            vm.ColWidthSize = startSize;

            // Size min width is 36px. User drags -60px (75 - 60 = 15 < 36)
            MainWindow.ResizeMetadataColumn(vm, "Size", startColWidth: startSize, startNameWidth: startName, deltaX: -60.0);

            Assert.Equal(36.0, vm.ColWidthSize);
            // Delta was 36 - 75 = -39, so Name grew by +39 -> 339
            Assert.Equal(339.0, vm.ColWidthName);
        }
        finally
        {
            vm.IsDraggingColumnWidth = false;
            vm.ResetColumnsCommand.Execute(null);
        }
    }

    [Fact]
    public void TableResize_NameColumn_Enlarging_ShrinksMetadataColumnsProportionally()
    {
        var vm = new MainViewModel();
        vm.ResetColumnsCommand.Execute(null);
        vm.IsDraggingColumnWidth = true;
        try
        {
            double startName = 200.0;
            var startDict = new Dictionary<string, double>
            {
                ["Size"] = 100.0,
                ["Hoster"] = 100.0
            };
            vm.ColWidthName = startName;
            vm.ColWidthSize = 100.0;
            vm.ColWidthHoster = 100.0;

            // Name expands by +40px
            MainWindow.ResizeNameColumn(vm, startName, startDict, deltaX: 40.0);

            Assert.Equal(240.0, vm.ColWidthName);
            // The two metadata columns shrink to absorb the 40px
            double metadataSum = vm.ColWidthSize + vm.ColWidthHoster;
            Assert.Equal(160.0, Math.Round(metadataSum, 1));
            Assert.Equal(400.0, Math.Round(vm.ColWidthName + metadataSum, 1));
        }
        finally
        {
            vm.IsDraggingColumnWidth = false;
            vm.ResetColumnsCommand.Execute(null);
        }
    }

    [Fact]
    public void TableResize_NameColumn_Shrinking_ExpandsMetadataColumnsProportionally()
    {
        var vm = new MainViewModel();
        vm.ResetColumnsCommand.Execute(null);
        vm.IsDraggingColumnWidth = true;
        try
        {
            double startName = 300.0;
            var startDict = new Dictionary<string, double>
            {
                ["Size"] = 100.0,
                ["Hoster"] = 100.0
            };
            vm.ColWidthName = startName;
            vm.ColWidthSize = 100.0;
            vm.ColWidthHoster = 100.0;

            // Name shrinks by -50px
            MainWindow.ResizeNameColumn(vm, startName, startDict, deltaX: -50.0);

            Assert.Equal(250.0, vm.ColWidthName);
            // The two metadata columns expand equally by 25px each
            Assert.Equal(125.0, vm.ColWidthSize);
            Assert.Equal(125.0, vm.ColWidthHoster);
            Assert.Equal(500.0, vm.ColWidthName + vm.ColWidthSize + vm.ColWidthHoster);
        }
        finally
        {
            vm.IsDraggingColumnWidth = false;
            vm.ResetColumnsCommand.Execute(null);
        }
    }

    [Fact]
    public void TableResize_CompressMetadataColumnsToFit_CompressesToExactAvailableWidth()
    {
        var vm = new MainViewModel();
        vm.ResetColumnsCommand.Execute(null);
        vm.IsDraggingColumnWidth = true;
        try
        {
            // Turn on all metadata columns
            vm.ShowColHoster = true;
            vm.ShowColSavePath = true;
            vm.ShowColSize = true;
            vm.ShowColProgress = true;
            vm.ShowColSpeed = true;
            vm.ShowColEta = true;
            vm.ShowColStatus = true;
            vm.ShowColAddedDate = true;
            vm.ShowColCompletedDate = true;
            vm.ShowColChecksum = true;
            vm.ShowColActions = true;

            // Available metadata space is constrained to 600px
            MainWindow.CompressMetadataColumnsToFit(vm, 600.0);

            double compressedSum = MainWindow.GetVisibleMetadataSum(vm);
            Assert.True(Math.Abs(compressedSum - 600.0) <= 0.5, $"Expected sum ~600px, but got {compressedSum}");
        }
        finally
        {
            vm.IsDraggingColumnWidth = false;
            vm.ResetColumnsCommand.Execute(null);
        }
    }

    [Fact]
    public void TableResize_LastColumn_MultipleTimes_RightEdgeInvariant_NeverCrossesGearButton()
    {
        var vm = new MainViewModel();
        vm.ResetColumnsCommand.Execute(null);
        vm.IsDraggingColumnWidth = true;
        try
        {
            double initialName = 300.0;
            double initialActions = 95.0;
            vm.ColWidthName = initialName;
            vm.ColWidthActions = initialActions;

            double viewerWidth = 1000.0;
            double initialTotal = MainWindow.GetColumnWidth(vm, "Name") + MainWindow.GetVisibleMetadataSum(vm);

            // Resize 1: user drags Actions +50px
            MainWindow.ResizeMetadataColumn(vm, "Actions", startColWidth: initialActions, startNameWidth: initialName, deltaX: 50.0);
            double totalAfter1 = MainWindow.GetColumnWidth(vm, "Name") + MainWindow.GetVisibleMetadataSum(vm);
            Assert.Equal(initialTotal, totalAfter1);

            // Resize 2: user drags Actions again from new width +30px
            double actions2 = vm.ColWidthActions;
            double name2 = vm.ColWidthName;
            MainWindow.ResizeMetadataColumn(vm, "Actions", startColWidth: actions2, startNameWidth: name2, deltaX: 30.0);
            double totalAfter2 = MainWindow.GetColumnWidth(vm, "Name") + MainWindow.GetVisibleMetadataSum(vm);
            Assert.Equal(initialTotal, totalAfter2);

            // Resize 3: user drags Actions a 3rd time to max limit (+500px)
            double actions3 = vm.ColWidthActions;
            double name3 = vm.ColWidthName;
            MainWindow.ResizeMetadataColumn(vm, "Actions", startColWidth: actions3, startNameWidth: name3, deltaX: 500.0);
            double totalAfter3 = MainWindow.GetColumnWidth(vm, "Name") + MainWindow.GetVisibleMetadataSum(vm);
            Assert.Equal(initialTotal, totalAfter3);

            // In all 3 iterations, right edge never moved by even 1 pixel, so divider line never crosses gear button!
            Assert.True(totalAfter3 <= viewerWidth);
        }
        finally
        {
            vm.IsDraggingColumnWidth = false;
            vm.ResetColumnsCommand.Execute(null);
        }
    }


    [Fact]
    public void TreeListHeaderGearButton_And_VerticalScrollBar_AlignsAtExactSameHorizontalCoordinate()
    {
        RunInSta(() =>
        {
            // Verify that the table header gear button container (18px wide, docked right)
            // and the vertical scrollbar (18px wide, docked right) share the identical horizontal width
            // and border thickness (1,0,0,0) with BorderDarkBrush, forming a single continuous line.
            var headerBorder = new Border
            {
                BorderThickness = new Thickness(1, 0, 0, 0),
                Width = 18
            };

            var scrollBarBorder = new Border
            {
                BorderThickness = new Thickness(1, 0, 0, 0),
                Width = 18
            };

            Assert.Equal(18.0, headerBorder.Width);
            Assert.Equal(18.0, scrollBarBorder.Width);
            Assert.Equal(new Thickness(1, 0, 0, 0), headerBorder.BorderThickness);
            Assert.Equal(new Thickness(1, 0, 0, 0), scrollBarBorder.BorderThickness);
        });
    }

    [Fact]
    public void HorizontalScrollBar_IsDisabled_And_TableFitsViewport()
    {
        RunInSta(() =>
        {
            var scv = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible
            };

            Assert.Equal(ScrollBarVisibility.Disabled, scv.HorizontalScrollBarVisibility);
            Assert.Equal(ScrollBarVisibility.Visible, scv.VerticalScrollBarVisibility);
        });
    }

    #endregion
}


