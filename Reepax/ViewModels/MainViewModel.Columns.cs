using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Reepax.Models;
using Reepax.Services.Localization;

namespace Reepax.ViewModels;

public partial class MainViewModel
{
    // =========================================================================
    // TreeListView Column Widths
    // =========================================================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthName))]
    [NotifyPropertyChangedFor(nameof(TotalVisibleColumnsWidth))]
    [NotifyPropertyChangedFor(nameof(TotalContentMinWidth))]
    private double _colWidthName = 220;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthHoster))]
    [NotifyPropertyChangedFor(nameof(TotalVisibleColumnsWidth))]
    [NotifyPropertyChangedFor(nameof(TotalContentMinWidth))]
    private double _colWidthHoster = 75;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthSavePath))]
    [NotifyPropertyChangedFor(nameof(TotalVisibleColumnsWidth))]
    [NotifyPropertyChangedFor(nameof(TotalContentMinWidth))]
    private double _colWidthSavePath = 140;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthSize))]
    [NotifyPropertyChangedFor(nameof(TotalVisibleColumnsWidth))]
    [NotifyPropertyChangedFor(nameof(TotalContentMinWidth))]
    private double _colWidthSize = 75;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthProgress))]
    [NotifyPropertyChangedFor(nameof(TotalVisibleColumnsWidth))]
    [NotifyPropertyChangedFor(nameof(TotalContentMinWidth))]
    private double _colWidthProgress = 110;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthSpeed))]
    [NotifyPropertyChangedFor(nameof(TotalVisibleColumnsWidth))]
    [NotifyPropertyChangedFor(nameof(TotalContentMinWidth))]
    private double _colWidthSpeed = 80;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthEta))]
    [NotifyPropertyChangedFor(nameof(TotalVisibleColumnsWidth))]
    [NotifyPropertyChangedFor(nameof(TotalContentMinWidth))]
    private double _colWidthEta = 65;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthStatus))]
    [NotifyPropertyChangedFor(nameof(TotalVisibleColumnsWidth))]
    [NotifyPropertyChangedFor(nameof(TotalContentMinWidth))]
    private double _colWidthStatus = 95;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthAddedDate))]
    [NotifyPropertyChangedFor(nameof(TotalVisibleColumnsWidth))]
    [NotifyPropertyChangedFor(nameof(TotalContentMinWidth))]
    private double _colWidthAddedDate = 95;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthCompletedDate))]
    [NotifyPropertyChangedFor(nameof(TotalVisibleColumnsWidth))]
    [NotifyPropertyChangedFor(nameof(TotalContentMinWidth))]
    private double _colWidthCompletedDate = 95;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthChecksum))]
    [NotifyPropertyChangedFor(nameof(TotalVisibleColumnsWidth))]
    [NotifyPropertyChangedFor(nameof(TotalContentMinWidth))]
    private double _colWidthChecksum = 90;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthActions))]
    [NotifyPropertyChangedFor(nameof(TotalVisibleColumnsWidth))]
    [NotifyPropertyChangedFor(nameof(TotalContentMinWidth))]
    private double _colWidthActions = 95;

    // =========================================================================
    // TreeListView Column Visibility
    // =========================================================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthName))]
    [NotifyPropertyChangedFor(nameof(TotalVisibleColumnsWidth))]
    [NotifyPropertyChangedFor(nameof(TotalContentMinWidth))]
    private bool _showColName = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthHoster))]
    [NotifyPropertyChangedFor(nameof(TotalVisibleColumnsWidth))]
    [NotifyPropertyChangedFor(nameof(TotalContentMinWidth))]
    private bool _showColHoster = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthSavePath))]
    [NotifyPropertyChangedFor(nameof(TotalVisibleColumnsWidth))]
    [NotifyPropertyChangedFor(nameof(TotalContentMinWidth))]
    private bool _showColSavePath = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthSize))]
    [NotifyPropertyChangedFor(nameof(TotalVisibleColumnsWidth))]
    [NotifyPropertyChangedFor(nameof(TotalContentMinWidth))]
    private bool _showColSize = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthProgress))]
    [NotifyPropertyChangedFor(nameof(TotalVisibleColumnsWidth))]
    [NotifyPropertyChangedFor(nameof(TotalContentMinWidth))]
    private bool _showColProgress = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthSpeed))]
    [NotifyPropertyChangedFor(nameof(TotalVisibleColumnsWidth))]
    [NotifyPropertyChangedFor(nameof(TotalContentMinWidth))]
    private bool _showColSpeed = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthEta))]
    [NotifyPropertyChangedFor(nameof(TotalVisibleColumnsWidth))]
    [NotifyPropertyChangedFor(nameof(TotalContentMinWidth))]
    private bool _showColEta = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthStatus))]
    [NotifyPropertyChangedFor(nameof(TotalVisibleColumnsWidth))]
    [NotifyPropertyChangedFor(nameof(TotalContentMinWidth))]
    private bool _showColStatus = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthAddedDate))]
    [NotifyPropertyChangedFor(nameof(TotalVisibleColumnsWidth))]
    [NotifyPropertyChangedFor(nameof(TotalContentMinWidth))]
    private bool _showColAddedDate = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthCompletedDate))]
    [NotifyPropertyChangedFor(nameof(TotalVisibleColumnsWidth))]
    [NotifyPropertyChangedFor(nameof(TotalContentMinWidth))]
    private bool _showColCompletedDate = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthChecksum))]
    [NotifyPropertyChangedFor(nameof(TotalVisibleColumnsWidth))]
    [NotifyPropertyChangedFor(nameof(TotalContentMinWidth))]
    private bool _showColChecksum = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthActions))]
    [NotifyPropertyChangedFor(nameof(TotalVisibleColumnsWidth))]
    [NotifyPropertyChangedFor(nameof(TotalContentMinWidth))]
    private bool _showColActions = true;

    // =========================================================================
    // Computed Actual Column Widths (0 when column is hidden)
    // =========================================================================

    public double ActualColWidthName => ShowColName ? ColWidthName : 0;
    public double ActualColWidthHoster => ShowColHoster ? ColWidthHoster : 0;
    public double ActualColWidthSavePath => ShowColSavePath ? ColWidthSavePath : 0;
    public double ActualColWidthSize => ShowColSize ? ColWidthSize : 0;
    public double ActualColWidthProgress => ShowColProgress ? ColWidthProgress : 0;
    public double ActualColWidthSpeed => ShowColSpeed ? ColWidthSpeed : 0;
    public double ActualColWidthEta => ShowColEta ? ColWidthEta : 0;
    public double ActualColWidthStatus => ShowColStatus ? ColWidthStatus : 0;
    public double ActualColWidthAddedDate => ShowColAddedDate ? ColWidthAddedDate : 0;
    public double ActualColWidthCompletedDate => ShowColCompletedDate ? ColWidthCompletedDate : 0;
    public double ActualColWidthChecksum => ShowColChecksum ? ColWidthChecksum : 0;
    public double ActualColWidthActions => ShowColActions ? ColWidthActions : 0;

    /// <summary>
    /// Total width of all currently visible columns in pixels.
    /// Used as MinWidth on header and list controls to ensure horizontal scrollbar activates
    /// whenever visible columns exceed the viewport width (even when the list is empty).
    /// </summary>
    public double TotalVisibleColumnsWidth =>
        ActualColWidthName +
        ActualColWidthHoster +
        ActualColWidthSavePath +
        ActualColWidthSize +
        ActualColWidthProgress +
        ActualColWidthSpeed +
        ActualColWidthEta +
        ActualColWidthStatus +
        ActualColWidthAddedDate +
        ActualColWidthCompletedDate +
        ActualColWidthChecksum +
        ActualColWidthActions;

    public const double TrailingBreathingRoom = 0.0;

    /// <summary>
    /// Total width for visible columns.
    /// </summary>
    public double TotalContentMinWidth => TotalVisibleColumnsWidth + TrailingBreathingRoom;

    // =========================================================================
    // Column Right Dividers
    // =========================================================================

    public bool ShowDividerName => ShowColName;
    public bool ShowDividerHoster => ShowColHoster;
    public bool ShowDividerSavePath => ShowColSavePath;
    public bool ShowDividerSize => ShowColSize;
    public bool ShowDividerProgress => ShowColProgress;
    public bool ShowDividerSpeed => ShowColSpeed;
    public bool ShowDividerEta => ShowColEta;
    public bool ShowDividerStatus => ShowColStatus;
    public bool ShowDividerAddedDate => ShowColAddedDate;
    public bool ShowDividerCompletedDate => ShowColCompletedDate;
    public bool ShowDividerChecksum => ShowColChecksum;
    public bool ShowDividerActions => ShowColActions;

    private static System.Windows.GridLength GetColumnGridLength(bool isVisible, double pixelWidth)
    {
        if (!isVisible || pixelWidth <= 0)
            return new System.Windows.GridLength(0);

        return new System.Windows.GridLength(pixelWidth, System.Windows.GridUnitType.Pixel);
    }

    public System.Windows.GridLength GridColWidthName => GetColumnGridLength(ShowColName, ColWidthName);
    public System.Windows.GridLength GridColWidthHoster => GetColumnGridLength(ShowColHoster, ColWidthHoster);
    public System.Windows.GridLength GridColWidthSavePath => GetColumnGridLength(ShowColSavePath, ColWidthSavePath);
    public System.Windows.GridLength GridColWidthSize => GetColumnGridLength(ShowColSize, ColWidthSize);
    public System.Windows.GridLength GridColWidthProgress => GetColumnGridLength(ShowColProgress, ColWidthProgress);
    public System.Windows.GridLength GridColWidthSpeed => GetColumnGridLength(ShowColSpeed, ColWidthSpeed);
    public System.Windows.GridLength GridColWidthEta => GetColumnGridLength(ShowColEta, ColWidthEta);
    public System.Windows.GridLength GridColWidthStatus => GetColumnGridLength(ShowColStatus, ColWidthStatus);
    public System.Windows.GridLength GridColWidthAddedDate => GetColumnGridLength(ShowColAddedDate, ColWidthAddedDate);
    public System.Windows.GridLength GridColWidthCompletedDate => GetColumnGridLength(ShowColCompletedDate, ColWidthCompletedDate);
    public System.Windows.GridLength GridColWidthChecksum => GetColumnGridLength(ShowColChecksum, ColWidthChecksum);
    public System.Windows.GridLength GridColWidthActions => GetColumnGridLength(ShowColActions, ColWidthActions);

    public event EventHandler? ColumnLayoutChanged;

    public void NotifyDividerChanges()
    {
        OnPropertyChanged(nameof(ShowDividerName));
        OnPropertyChanged(nameof(ShowDividerHoster));
        OnPropertyChanged(nameof(ShowDividerSavePath));
        OnPropertyChanged(nameof(ShowDividerSize));
        OnPropertyChanged(nameof(ShowDividerProgress));
        OnPropertyChanged(nameof(ShowDividerSpeed));
        OnPropertyChanged(nameof(ShowDividerEta));
        OnPropertyChanged(nameof(ShowDividerStatus));
        OnPropertyChanged(nameof(ShowDividerAddedDate));
        OnPropertyChanged(nameof(ShowDividerCompletedDate));
        OnPropertyChanged(nameof(ShowDividerChecksum));
        OnPropertyChanged(nameof(ShowDividerActions));

        OnPropertyChanged(nameof(GridColWidthName));
        OnPropertyChanged(nameof(GridColWidthHoster));
        OnPropertyChanged(nameof(GridColWidthSavePath));
        OnPropertyChanged(nameof(GridColWidthSize));
        OnPropertyChanged(nameof(GridColWidthProgress));
        OnPropertyChanged(nameof(GridColWidthSpeed));
        OnPropertyChanged(nameof(GridColWidthEta));
        OnPropertyChanged(nameof(GridColWidthStatus));
        OnPropertyChanged(nameof(GridColWidthAddedDate));
        OnPropertyChanged(nameof(GridColWidthCompletedDate));
        OnPropertyChanged(nameof(GridColWidthChecksum));
        OnPropertyChanged(nameof(GridColWidthActions));

        NotifySlotWidthChanges();

        OnPropertyChanged(nameof(TotalVisibleColumnsWidth));
        OnPropertyChanged(nameof(TotalContentMinWidth));
    }

    // =========================================================================
    // Column Order & Slot Properties
    // =========================================================================

    [ObservableProperty]
    private List<string> _columnOrder = new(AppSettings.DefaultColumnOrder);

    public int ColIndexName => GetColumnIndex("Name");
    public int ColIndexHoster => GetColumnIndex("Hoster");
    public int ColIndexSavePath => GetColumnIndex("SavePath");
    public int ColIndexSize => GetColumnIndex("Size");
    public int ColIndexProgress => GetColumnIndex("Progress");
    public int ColIndexSpeed => GetColumnIndex("Speed");
    public int ColIndexEta => GetColumnIndex("Eta");
    public int ColIndexStatus => GetColumnIndex("Status");
    public int ColIndexAddedDate => GetColumnIndex("AddedDate");
    public int ColIndexCompletedDate => GetColumnIndex("CompletedDate");
    public int ColIndexChecksum => GetColumnIndex("Checksum");
    public int ColIndexActions => GetColumnIndex("Actions");

    public int GetColumnIndex(string column)
    {
        int idx = ColumnOrder.IndexOf(column);
        return idx >= 0 ? idx : Array.IndexOf(AppSettings.DefaultColumnOrder, column);
    }

    public string GetColumnAtSlot(int slot)
    {
        if (slot >= 0 && slot < ColumnOrder.Count)
            return ColumnOrder[slot];
        if (slot >= 0 && slot < AppSettings.DefaultColumnOrder.Length)
            return AppSettings.DefaultColumnOrder[slot];
        return "Name";
    }

    public bool IsColumnVisible(string col) => col switch
    {
        "Name" => ShowColName,
        "Hoster" => ShowColHoster,
        "SavePath" => ShowColSavePath,
        "Size" => ShowColSize,
        "Progress" => ShowColProgress,
        "Speed" => ShowColSpeed,
        "Eta" => ShowColEta,
        "Status" => ShowColStatus,
        "AddedDate" => ShowColAddedDate,
        "CompletedDate" => ShowColCompletedDate,
        "Checksum" => ShowColChecksum,
        "Actions" => ShowColActions,
        _ => true
    };

    public double GetColumnPixelWidth(string col) => col switch
    {
        "Name" => ColWidthName,
        "Hoster" => ColWidthHoster,
        "SavePath" => ColWidthSavePath,
        "Size" => ColWidthSize,
        "Progress" => ColWidthProgress,
        "Speed" => ColWidthSpeed,
        "Eta" => ColWidthEta,
        "Status" => ColWidthStatus,
        "AddedDate" => ColWidthAddedDate,
        "CompletedDate" => ColWidthCompletedDate,
        "Checksum" => ColWidthChecksum,
        "Actions" => ColWidthActions,
        _ => 100
    };

    public System.Windows.GridLength GetSlotGridLength(int slot)
    {
        string col = GetColumnAtSlot(slot);
        return GetColumnGridLength(IsColumnVisible(col), GetColumnPixelWidth(col));
    }

    public double GetSlotActualWidth(int slot)
    {
        string col = GetColumnAtSlot(slot);
        return IsColumnVisible(col) ? GetColumnPixelWidth(col) : 0;
    }

    public System.Windows.GridLength GridColWidthSlot0 => GetSlotGridLength(0);
    public System.Windows.GridLength GridColWidthSlot1 => GetSlotGridLength(1);
    public System.Windows.GridLength GridColWidthSlot2 => GetSlotGridLength(2);
    public System.Windows.GridLength GridColWidthSlot3 => GetSlotGridLength(3);
    public System.Windows.GridLength GridColWidthSlot4 => GetSlotGridLength(4);
    public System.Windows.GridLength GridColWidthSlot5 => GetSlotGridLength(5);
    public System.Windows.GridLength GridColWidthSlot6 => GetSlotGridLength(6);
    public System.Windows.GridLength GridColWidthSlot7 => GetSlotGridLength(7);
    public System.Windows.GridLength GridColWidthSlot8 => GetSlotGridLength(8);
    public System.Windows.GridLength GridColWidthSlot9 => GetSlotGridLength(9);
    public System.Windows.GridLength GridColWidthSlot10 => GetSlotGridLength(10);
    public System.Windows.GridLength GridColWidthSlot11 => GetSlotGridLength(11);

    public double ActualColWidthSlot0 => GetSlotActualWidth(0);
    public double ActualColWidthSlot1 => GetSlotActualWidth(1);
    public double ActualColWidthSlot2 => GetSlotActualWidth(2);
    public double ActualColWidthSlot3 => GetSlotActualWidth(3);
    public double ActualColWidthSlot4 => GetSlotActualWidth(4);
    public double ActualColWidthSlot5 => GetSlotActualWidth(5);
    public double ActualColWidthSlot6 => GetSlotActualWidth(6);
    public double ActualColWidthSlot7 => GetSlotActualWidth(7);
    public double ActualColWidthSlot8 => GetSlotActualWidth(8);
    public double ActualColWidthSlot9 => GetSlotActualWidth(9);
    public double ActualColWidthSlot10 => GetSlotActualWidth(10);
    public double ActualColWidthSlot11 => GetSlotActualWidth(11);

    public void MoveColumn(string column, int targetSlotIndex)
    {
        if (string.IsNullOrEmpty(column)) return;
        int currentIndex = ColumnOrder.IndexOf(column);
        if (currentIndex < 0) return;

        targetSlotIndex = Math.Clamp(targetSlotIndex, 0, ColumnOrder.Count - 1);
        if (currentIndex == targetSlotIndex) return;

        var newOrder = new List<string>(ColumnOrder);
        newOrder.RemoveAt(currentIndex);
        newOrder.Insert(targetSlotIndex, column);
        ApplyNewColumnOrder(newOrder);
    }

    public void MoveColumnBefore(string column, string targetColumn)
    {
        if (string.IsNullOrEmpty(column) || string.IsNullOrEmpty(targetColumn) || column == targetColumn) return;
        int currentIndex = ColumnOrder.IndexOf(column);
        if (currentIndex < 0) return;

        var newOrder = new List<string>(ColumnOrder);
        newOrder.RemoveAt(currentIndex);
        int targetIndex = newOrder.IndexOf(targetColumn);
        if (targetIndex < 0) targetIndex = 0;
        newOrder.Insert(targetIndex, column);
        ApplyNewColumnOrder(newOrder);
    }

    public void MoveColumnAfter(string column, string targetColumn)
    {
        if (string.IsNullOrEmpty(column) || string.IsNullOrEmpty(targetColumn) || column == targetColumn) return;
        int currentIndex = ColumnOrder.IndexOf(column);
        if (currentIndex < 0) return;

        var newOrder = new List<string>(ColumnOrder);
        newOrder.RemoveAt(currentIndex);
        int targetIndex = newOrder.IndexOf(targetColumn);
        if (targetIndex < 0) targetIndex = newOrder.Count - 1;
        newOrder.Insert(targetIndex + 1, column);
        ApplyNewColumnOrder(newOrder);
    }

    private void ApplyNewColumnOrder(List<string> newOrder)
    {
        ColumnOrder = AppSettings.SanitizeColumnOrder(newOrder);
        _settingsService.Settings.ColumnOrder = new List<string>(ColumnOrder);
        _settingsService.SaveSettings();
        NotifyColumnOrderChanges();
    }

    public void NotifyColumnOrderChanges()
    {
        OnPropertyChanged(nameof(ColIndexName));
        OnPropertyChanged(nameof(ColIndexHoster));
        OnPropertyChanged(nameof(ColIndexSavePath));
        OnPropertyChanged(nameof(ColIndexSize));
        OnPropertyChanged(nameof(ColIndexProgress));
        OnPropertyChanged(nameof(ColIndexSpeed));
        OnPropertyChanged(nameof(ColIndexEta));
        OnPropertyChanged(nameof(ColIndexStatus));
        OnPropertyChanged(nameof(ColIndexAddedDate));
        OnPropertyChanged(nameof(ColIndexCompletedDate));
        OnPropertyChanged(nameof(ColIndexChecksum));
        OnPropertyChanged(nameof(ColIndexActions));

        NotifySlotWidthChanges();
    }

    public void NotifySlotWidthChanges()
    {
        OnPropertyChanged(nameof(GridColWidthSlot0));
        OnPropertyChanged(nameof(GridColWidthSlot1));
        OnPropertyChanged(nameof(GridColWidthSlot2));
        OnPropertyChanged(nameof(GridColWidthSlot3));
        OnPropertyChanged(nameof(GridColWidthSlot4));
        OnPropertyChanged(nameof(GridColWidthSlot5));
        OnPropertyChanged(nameof(GridColWidthSlot6));
        OnPropertyChanged(nameof(GridColWidthSlot7));
        OnPropertyChanged(nameof(GridColWidthSlot8));
        OnPropertyChanged(nameof(GridColWidthSlot9));
        OnPropertyChanged(nameof(GridColWidthSlot10));
        OnPropertyChanged(nameof(GridColWidthSlot11));

        OnPropertyChanged(nameof(ActualColWidthSlot0));
        OnPropertyChanged(nameof(ActualColWidthSlot1));
        OnPropertyChanged(nameof(ActualColWidthSlot2));
        OnPropertyChanged(nameof(ActualColWidthSlot3));
        OnPropertyChanged(nameof(ActualColWidthSlot4));
        OnPropertyChanged(nameof(ActualColWidthSlot5));
        OnPropertyChanged(nameof(ActualColWidthSlot6));
        OnPropertyChanged(nameof(ActualColWidthSlot7));
        OnPropertyChanged(nameof(ActualColWidthSlot8));
        OnPropertyChanged(nameof(ActualColWidthSlot9));
        OnPropertyChanged(nameof(ActualColWidthSlot10));
        OnPropertyChanged(nameof(ActualColWidthSlot11));

        OnPropertyChanged(nameof(TotalVisibleColumnsWidth));
        OnPropertyChanged(nameof(TotalContentMinWidth));
    }

    partial void OnShowColNameChanged(bool value) { _settingsService.Settings.ShowColName = value; _settingsService.SaveSettings(); NotifyDividerChanges(); ColumnLayoutChanged?.Invoke(this, EventArgs.Empty); }
    partial void OnShowColHosterChanged(bool value) { _settingsService.Settings.ShowColHoster = value; _settingsService.SaveSettings(); NotifyDividerChanges(); ColumnLayoutChanged?.Invoke(this, EventArgs.Empty); }
    partial void OnShowColSavePathChanged(bool value) { _settingsService.Settings.ShowColSavePath = value; _settingsService.SaveSettings(); NotifyDividerChanges(); ColumnLayoutChanged?.Invoke(this, EventArgs.Empty); }
    partial void OnShowColSizeChanged(bool value) { _settingsService.Settings.ShowColSize = value; _settingsService.SaveSettings(); NotifyDividerChanges(); ColumnLayoutChanged?.Invoke(this, EventArgs.Empty); }
    partial void OnShowColProgressChanged(bool value) { _settingsService.Settings.ShowColProgress = value; _settingsService.SaveSettings(); NotifyDividerChanges(); ColumnLayoutChanged?.Invoke(this, EventArgs.Empty); }
    partial void OnShowColSpeedChanged(bool value) { _settingsService.Settings.ShowColSpeed = value; _settingsService.SaveSettings(); NotifyDividerChanges(); ColumnLayoutChanged?.Invoke(this, EventArgs.Empty); }
    partial void OnShowColEtaChanged(bool value) { _settingsService.Settings.ShowColEta = value; _settingsService.SaveSettings(); NotifyDividerChanges(); ColumnLayoutChanged?.Invoke(this, EventArgs.Empty); }
    partial void OnShowColStatusChanged(bool value) { _settingsService.Settings.ShowColStatus = value; _settingsService.SaveSettings(); NotifyDividerChanges(); ColumnLayoutChanged?.Invoke(this, EventArgs.Empty); }
    partial void OnShowColAddedDateChanged(bool value) { _settingsService.Settings.ShowColAddedDate = value; _settingsService.SaveSettings(); NotifyDividerChanges(); ColumnLayoutChanged?.Invoke(this, EventArgs.Empty); }
    partial void OnShowColCompletedDateChanged(bool value) { _settingsService.Settings.ShowColCompletedDate = value; _settingsService.SaveSettings(); NotifyDividerChanges(); ColumnLayoutChanged?.Invoke(this, EventArgs.Empty); }
    partial void OnShowColChecksumChanged(bool value) { _settingsService.Settings.ShowColChecksum = value; _settingsService.SaveSettings(); NotifyDividerChanges(); ColumnLayoutChanged?.Invoke(this, EventArgs.Empty); }
    partial void OnShowColActionsChanged(bool value) { _settingsService.Settings.ShowColActions = value; _settingsService.SaveSettings(); NotifyDividerChanges(); ColumnLayoutChanged?.Invoke(this, EventArgs.Empty); }

    partial void OnColumnOrderChanged(List<string> value)
    {
        NotifyColumnOrderChanges();
    }

    [ObservableProperty]
    private bool _isDraggingColumnWidth;

    partial void OnColWidthNameChanged(double value) { if (!IsDraggingColumnWidth) { _settingsService.Settings.ColWidthName = value; _settingsService.SaveSettings(); } NotifyDividerChanges(); }
    partial void OnColWidthHosterChanged(double value) { if (!IsDraggingColumnWidth) { _settingsService.Settings.ColWidthHoster = value; _settingsService.SaveSettings(); } NotifyDividerChanges(); }
    partial void OnColWidthSavePathChanged(double value) { if (!IsDraggingColumnWidth) { _settingsService.Settings.ColWidthSavePath = value; _settingsService.SaveSettings(); } NotifyDividerChanges(); }
    partial void OnColWidthSizeChanged(double value) { if (!IsDraggingColumnWidth) { _settingsService.Settings.ColWidthSize = value; _settingsService.SaveSettings(); } NotifyDividerChanges(); }
    partial void OnColWidthProgressChanged(double value) { if (!IsDraggingColumnWidth) { _settingsService.Settings.ColWidthProgress = value; _settingsService.SaveSettings(); } NotifyDividerChanges(); }
    partial void OnColWidthSpeedChanged(double value) { if (!IsDraggingColumnWidth) { _settingsService.Settings.ColWidthSpeed = value; _settingsService.SaveSettings(); } NotifyDividerChanges(); }
    partial void OnColWidthEtaChanged(double value) { if (!IsDraggingColumnWidth) { _settingsService.Settings.ColWidthEta = value; _settingsService.SaveSettings(); } NotifyDividerChanges(); }
    partial void OnColWidthStatusChanged(double value) { if (!IsDraggingColumnWidth) { _settingsService.Settings.ColWidthStatus = value; _settingsService.SaveSettings(); } NotifyDividerChanges(); }
    partial void OnColWidthAddedDateChanged(double value) { if (!IsDraggingColumnWidth) { _settingsService.Settings.ColWidthAddedDate = value; _settingsService.SaveSettings(); } NotifyDividerChanges(); }
    partial void OnColWidthCompletedDateChanged(double value) { if (!IsDraggingColumnWidth) { _settingsService.Settings.ColWidthCompletedDate = value; _settingsService.SaveSettings(); } NotifyDividerChanges(); }
    partial void OnColWidthChecksumChanged(double value) { if (!IsDraggingColumnWidth) { _settingsService.Settings.ColWidthChecksum = value; _settingsService.SaveSettings(); } NotifyDividerChanges(); }
    partial void OnColWidthActionsChanged(double value) { if (!IsDraggingColumnWidth) { _settingsService.Settings.ColWidthActions = value; _settingsService.SaveSettings(); } NotifyDividerChanges(); }

    // =========================================================================
    // Column Sorting
    // =========================================================================

    [ObservableProperty]
    private string? _sortColumn;

    [ObservableProperty]
    private ListSortDirection? _sortDirection;

    public static bool IsDefaultSortDescending(string column) => column switch
    {
        "Size" => true,          // 1st click: larger
        "Speed" => true,         // 1st click: faster
        "Progress" => true,      // 1st click: higher %
        "AddedDate" => true,     // 1st click: newer
        "CompletedDate" => true, // 1st click: newer
        "Status" => true,        // 1st click: active downloads first
        "Actions" => true,       // 1st click: packages with most items first
        _ => false               // Name, Hoster, SavePath, Checksum, Eta: 1st click Ascending
    };

    public void ToggleColumnSort(string column)
    {
        if (string.IsNullOrEmpty(column)) return;

        if (!string.Equals(SortColumn, column, StringComparison.OrdinalIgnoreCase))
        {
            // 1st click on a new column: activate default sort
            SortColumn = column;
            SortDirection = IsDefaultSortDescending(column)
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
        }
        else
        {
            // Clicking on the SAME column: cycle: State 1 -> State 2 -> State 3 (Off)
            bool defaultDesc = IsDefaultSortDescending(column);
            var firstDir = defaultDesc ? ListSortDirection.Descending : ListSortDirection.Ascending;
            var secondDir = defaultDesc ? ListSortDirection.Ascending : ListSortDirection.Descending;

            if (SortDirection == firstDir)
            {
                SortDirection = secondDir;
            }
            else
            {
                SortColumn = null;
                SortDirection = null;
            }
        }

        ApplyCurrentSort();
    }

    public void ClearColumnSort()
    {
        if (SortColumn != null || SortDirection != null)
        {
            SortColumn = null;
            SortDirection = null;
            ApplyCurrentSort();
        }
    }

    public void ApplyCurrentSort()
    {
        // Ensure original item order indices are initialized
        foreach (var pkg in Packages)
        {
            for (int i = 0; i < pkg.Items.Count; i++)
            {
                if (pkg.Items[i].OriginalOrderIndex == 0)
                {
                    pkg.Items[i].OriginalOrderIndex = i + 1;
                }
            }
        }

        if (string.IsNullOrEmpty(SortColumn) || !SortDirection.HasValue)
        {
            // Restore natural queue order
            var naturalRoots = Packages.Where(p => !p.IsClipped && MatchesFilter(p)).ToList();
            SyncOrder(RootPackages, naturalRoots);

            foreach (var pkg in Packages)
            {
                var naturalItems = pkg.Items.OrderBy(i => i.OriginalOrderIndex).ThenBy(i => i.CreatedAt).ToList();
                SyncOrder(pkg.Items, naturalItems);
                if (pkg.ClippedPackages.Count > 1)
                {
                    var naturalClipped = pkg.ClippedPackages.OrderBy(c => c.CreatedAt).ToList();
                    SyncOrder(pkg.ClippedPackages, naturalClipped);
                }
            }
            return;
        }

        string col = SortColumn;
        ListSortDirection dir = SortDirection.Value;

        // 1. Sort RootPackages
        var sortedRoots = SortPackages(RootPackages.ToList(), col, dir);
        SyncOrder(RootPackages, sortedRoots);

        // 2. Sort Items and ClippedPackages inside each package
        foreach (var pkg in Packages)
        {
            var sortedItems = SortItems(pkg.Items.ToList(), col, dir);
            SyncOrder(pkg.Items, sortedItems);

            if (pkg.ClippedPackages.Count > 1)
            {
                var sortedClipped = SortPackages(pkg.ClippedPackages.ToList(), col, dir);
                SyncOrder(pkg.ClippedPackages, sortedClipped);
            }
        }
    }

    public static List<DownloadPackage> SortPackages(List<DownloadPackage> list, string column, ListSortDirection direction)
    {
        bool asc = direction == ListSortDirection.Ascending;
        return column switch
        {
            "Name" => asc 
                ? list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList()
                : list.OrderByDescending(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(),

            "Hoster" => asc
                ? list.OrderBy(p => p.Items.FirstOrDefault()?.HosterName ?? "", StringComparer.OrdinalIgnoreCase).ToList()
                : list.OrderByDescending(p => p.Items.FirstOrDefault()?.HosterName ?? "", StringComparer.OrdinalIgnoreCase).ToList(),

            "SavePath" => asc
                ? list.OrderBy(p => p.SaveDirectory, StringComparer.OrdinalIgnoreCase).ToList()
                : list.OrderByDescending(p => p.SaveDirectory, StringComparer.OrdinalIgnoreCase).ToList(),

            "Size" => asc
                ? list.OrderBy(p => p.TotalBytes).ToList()
                : list.OrderByDescending(p => p.TotalBytes).ToList(),

            "Progress" => asc
                ? list.OrderBy(p => p.ProgressPercentage).ToList()
                : list.OrderByDescending(p => p.ProgressPercentage).ToList(),

            "Speed" => asc
                ? list.OrderBy(p => p.SpeedBytesPerSecond).ToList()
                : list.OrderByDescending(p => p.SpeedBytesPerSecond).ToList(),

            "Eta" => asc
                ? list.OrderBy(p => p.RemainingSeconds <= 0 ? double.MaxValue : p.RemainingSeconds).ToList()
                : list.OrderByDescending(p => p.RemainingSeconds).ToList(),

            "Status" => asc
                ? list.OrderBy(p => GetStatusSortRank(p.Status)).ToList()
                : list.OrderByDescending(p => GetStatusSortRank(p.Status)).ToList(),

            "AddedDate" => asc
                ? list.OrderBy(p => p.CreatedAt).ToList()
                : list.OrderByDescending(p => p.CreatedAt).ToList(),

            "CompletedDate" => asc
                ? list.OrderBy(p => p.CompletedAt ?? DateTime.MinValue).ToList()
                : list.OrderByDescending(p => p.CompletedAt ?? DateTime.MinValue).ToList(),

            "Checksum" => asc
                ? list.OrderBy(p => p.Items.FirstOrDefault()?.DisplayChecksum ?? "", StringComparer.OrdinalIgnoreCase).ToList()
                : list.OrderByDescending(p => p.Items.FirstOrDefault()?.DisplayChecksum ?? "", StringComparer.OrdinalIgnoreCase).ToList(),

            "Actions" => asc
                ? list.OrderBy(p => p.Items.Count).ToList()
                : list.OrderByDescending(p => p.Items.Count).ToList(),

            _ => list
        };
    }

    public static List<DownloadItem> SortItems(List<DownloadItem> list, string column, ListSortDirection direction)
    {
        bool asc = direction == ListSortDirection.Ascending;
        return column switch
        {
            "Name" => asc
                ? list.OrderBy(i => i.FileName, StringComparer.OrdinalIgnoreCase).ToList()
                : list.OrderByDescending(i => i.FileName, StringComparer.OrdinalIgnoreCase).ToList(),

            "Hoster" => asc
                ? list.OrderBy(i => i.HosterName, StringComparer.OrdinalIgnoreCase).ToList()
                : list.OrderByDescending(i => i.HosterName, StringComparer.OrdinalIgnoreCase).ToList(),

            "SavePath" => asc
                ? list.OrderBy(i => i.SaveFilePath ?? "", StringComparer.OrdinalIgnoreCase).ToList()
                : list.OrderByDescending(i => i.SaveFilePath ?? "", StringComparer.OrdinalIgnoreCase).ToList(),

            "Size" => asc
                ? list.OrderBy(i => i.TotalBytes).ToList()
                : list.OrderByDescending(i => i.TotalBytes).ToList(),

            "Progress" => asc
                ? list.OrderBy(i => i.ProgressPercentage).ToList()
                : list.OrderByDescending(i => i.ProgressPercentage).ToList(),

            "Speed" => asc
                ? list.OrderBy(i => i.SpeedBytesPerSecond).ToList()
                : list.OrderByDescending(i => i.SpeedBytesPerSecond).ToList(),

            "Eta" => asc
                ? list.OrderBy(i => i.RemainingSeconds <= 0 ? double.MaxValue : i.RemainingSeconds).ToList()
                : list.OrderByDescending(i => i.RemainingSeconds).ToList(),

            "Status" => asc
                ? list.OrderBy(i => GetStatusSortRank(i.Status)).ToList()
                : list.OrderByDescending(i => GetStatusSortRank(i.Status)).ToList(),

            "AddedDate" => asc
                ? list.OrderBy(i => i.CreatedAt).ToList()
                : list.OrderByDescending(i => i.CreatedAt).ToList(),

            "CompletedDate" => asc
                ? list.OrderBy(i => i.CompletedAt ?? DateTime.MinValue).ToList()
                : list.OrderByDescending(i => i.CompletedAt ?? DateTime.MinValue).ToList(),

            "Checksum" => asc
                ? list.OrderBy(i => i.DisplayChecksum ?? "", StringComparer.OrdinalIgnoreCase).ToList()
                : list.OrderByDescending(i => i.DisplayChecksum ?? "", StringComparer.OrdinalIgnoreCase).ToList(),

            "Actions" => asc
                ? list.OrderBy(i => i.FileName, StringComparer.OrdinalIgnoreCase).ToList()
                : list.OrderByDescending(i => i.FileName, StringComparer.OrdinalIgnoreCase).ToList(),

            _ => list
        };
    }

    private static int GetStatusSortRank(DownloadStatus status) => status switch
    {
        DownloadStatus.Downloading => 10,
        DownloadStatus.SolvingCaptcha => 9,
        DownloadStatus.WaitingForBrowser => 8,
        DownloadStatus.InBrowser => 7,
        DownloadStatus.Queued => 6,
        DownloadStatus.Paused => 5,
        DownloadStatus.Completed => 2,
        DownloadStatus.Failed => 1,
        DownloadStatus.Aborted => 0,
        _ => 0
    };

    public static void SyncOrder<T>(ObservableCollection<T> collection, IList<T> targetOrder)
    {
        for (int targetIndex = 0; targetIndex < targetOrder.Count; targetIndex++)
        {
            int currentIndex = collection.IndexOf(targetOrder[targetIndex]);
            if (currentIndex >= 0 && targetIndex < collection.Count && currentIndex != targetIndex)
            {
                collection.Move(currentIndex, targetIndex);
            }
        }
    }

    // =========================================================================
    // Column AutoFit & Reset
    // =========================================================================

    [RelayCommand]
    public void AutoFitColumns()
    {
        if (ShowColHoster) ColWidthHoster = 75;
        if (ShowColSavePath) ColWidthSavePath = 140;
        if (ShowColSize) ColWidthSize = 75;
        if (ShowColProgress) ColWidthProgress = 110;
        if (ShowColSpeed) ColWidthSpeed = 80;
        if (ShowColEta) ColWidthEta = 65;
        if (ShowColStatus) ColWidthStatus = 95;
        if (ShowColAddedDate) ColWidthAddedDate = 95;
        if (ShowColCompletedDate) ColWidthCompletedDate = 95;
        if (ShowColChecksum) ColWidthChecksum = 90;
        if (ShowColActions) ColWidthActions = 95;

        _settingsService.Settings.ColWidthHoster = ColWidthHoster;
        _settingsService.Settings.ColWidthSavePath = ColWidthSavePath;
        _settingsService.Settings.ColWidthSize = ColWidthSize;
        _settingsService.Settings.ColWidthProgress = ColWidthProgress;
        _settingsService.Settings.ColWidthSpeed = ColWidthSpeed;
        _settingsService.Settings.ColWidthEta = ColWidthEta;
        _settingsService.Settings.ColWidthStatus = ColWidthStatus;
        _settingsService.Settings.ColWidthAddedDate = ColWidthAddedDate;
        _settingsService.Settings.ColWidthCompletedDate = ColWidthCompletedDate;
        _settingsService.Settings.ColWidthChecksum = ColWidthChecksum;
        _settingsService.Settings.ColWidthActions = ColWidthActions;
        _settingsService.SaveSettings();
        NotifyDividerChanges();
    }

    [RelayCommand]
    public void ResetColumns()
    {
        ColWidthName = 220;
        ColWidthHoster = 75;
        ColWidthSavePath = 140;
        ColWidthSize = 75;
        ColWidthProgress = 110;
        ColWidthSpeed = 80;
        ColWidthEta = 65;
        ColWidthStatus = 95;
        ColWidthAddedDate = 95;
        ColWidthCompletedDate = 95;
        ColWidthChecksum = 90;
        ColWidthActions = 95;

        ShowColName = true;
        ShowColHoster = true;
        ShowColSavePath = false;
        ShowColSize = true;
        ShowColProgress = true;
        ShowColSpeed = true;
        ShowColEta = true;
        ShowColStatus = true;
        ShowColAddedDate = false;
        ShowColCompletedDate = false;
        ShowColChecksum = false;
        ShowColActions = true;

        var s = _settingsService.Settings;
        s.ColWidthName = ColWidthName;
        s.ColWidthHoster = ColWidthHoster;
        s.ColWidthSavePath = ColWidthSavePath;
        s.ColWidthSize = ColWidthSize;
        s.ColWidthProgress = ColWidthProgress;
        s.ColWidthSpeed = ColWidthSpeed;
        s.ColWidthEta = ColWidthEta;
        s.ColWidthStatus = ColWidthStatus;
        s.ColWidthAddedDate = ColWidthAddedDate;
        s.ColWidthCompletedDate = ColWidthCompletedDate;
        s.ColWidthChecksum = ColWidthChecksum;
        s.ColWidthActions = ColWidthActions;

        s.ShowColName = ShowColName;
        s.ShowColHoster = ShowColHoster;
        s.ShowColSavePath = ShowColSavePath;
        s.ShowColSize = ShowColSize;
        s.ShowColProgress = ShowColProgress;
        s.ShowColSpeed = ShowColSpeed;
        s.ShowColEta = ShowColEta;
        s.ShowColStatus = ShowColStatus;
        s.ShowColAddedDate = ShowColAddedDate;
        s.ShowColCompletedDate = ShowColCompletedDate;
        s.ShowColChecksum = ShowColChecksum;
        s.ShowColActions = ShowColActions;

        NotifyDividerChanges();
        ClearColumnSort();
        _settingsService.SaveSettings();
    }

    [RelayCommand]
    public void ResetColumnWidths()
    {
        ColWidthName = 220;
        ColWidthHoster = 75;
        ColWidthSavePath = 140;
        ColWidthSize = 75;
        ColWidthProgress = 110;
        ColWidthSpeed = 80;
        ColWidthEta = 65;
        ColWidthStatus = 95;
        ColWidthAddedDate = 95;
        ColWidthCompletedDate = 95;
        ColWidthChecksum = 90;
        ColWidthActions = 95;

        ColumnOrder = new List<string>(AppSettings.DefaultColumnOrder);

        var s = _settingsService.Settings;
        s.ColumnOrder = new List<string>(AppSettings.DefaultColumnOrder);
        s.ColWidthName = ColWidthName;
        s.ColWidthHoster = ColWidthHoster;
        s.ColWidthSavePath = ColWidthSavePath;
        s.ColWidthSize = ColWidthSize;
        s.ColWidthProgress = ColWidthProgress;
        s.ColWidthSpeed = ColWidthSpeed;
        s.ColWidthEta = ColWidthEta;
        s.ColWidthStatus = ColWidthStatus;
        s.ColWidthAddedDate = ColWidthAddedDate;
        s.ColWidthCompletedDate = ColWidthCompletedDate;
        s.ColWidthChecksum = ColWidthChecksum;
        s.ColWidthActions = ColWidthActions;
        _settingsService.SaveSettings();
        StatusSummary = Loc.Get("Status_ColumnWidthsReset");
        NotifyDividerChanges();
        NotifyColumnOrderChanges();
    }
}
