using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Reepax.Helpers;
using Reepax.Models;
using Reepax.Services.Localization;
using Reepax.ViewModels;

namespace Reepax.Views;

public partial class DownloadOverviewView : UserControl
{
    public static readonly DependencyProperty AvailableHeightProperty = DependencyProperty.Register(
        nameof(AvailableHeight), typeof(double), typeof(DownloadOverviewView), new PropertyMetadata(0.0,
            (owner, _) => ((DownloadOverviewView)owner).UpdateHeight()));
    public double AvailableHeight { get => (double)GetValue(AvailableHeightProperty); set => SetValue(AvailableHeightProperty, value); }
    private DownloadOverviewViewModel? _model;
    private Popup? _metricsPopup;
    private CheckBox? _includeDisabledChoice;
    private double _heightBeforeDrag;
    private const double MinimumDownloadListHeight = 160;
    private double MaximumBodyHeight => AvailableHeight > 0 && double.IsFinite(AvailableHeight)
        ? Math.Max(0, AvailableHeight - HeaderFrame.Height - MinimumDownloadListHeight - 2) : 360;

    public DownloadOverviewView()
    {
        InitializeComponent();
        _metricsPopup = MetricsButton.DropdownPopup;
        _metricsPopup.Opened += (_, _) => _includeDisabledChoice?.Focus();
        ((System.Collections.Specialized.INotifyCollectionChanged)MetricsItems.Items).CollectionChanged += (_, _) => ResetMetricDrag();
        DataContextChanged += (_, _) => AttachModel();
        Loaded += (_, _) => AttachModel();
        IsVisibleChanged += (_, _) => { _model?.SetPresentationActive(IsLoaded && IsVisible); if (!IsVisible) { ResetMetricDrag(); ClosePopup(); } };
        Unloaded += (_, _) => { ResetMetricDrag(); ClosePopup(); _model?.SetPresentationActive(false); if (_model != null) _model.PropertyChanged -= ModelChanged; };
    }
    private void AttachModel()
    {
        ResetMetricDrag();
        if (_model != null) { _model.PropertyChanged -= ModelChanged; _model.SetPresentationActive(false); }
        _model = DataContext as DownloadOverviewViewModel;
        if (_model != null) { _model.PropertyChanged += ModelChanged; _model.SetPresentationActive(IsLoaded && IsVisible); }
        ClosePopup(); BuildMetricsChoices(); UpdateHeight();
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(DownloadOverviewViewModel.IsExpanded) && _model?.IsExpanded == false) ResetMetricDrag();
        if (args.PropertyName == nameof(DownloadOverviewViewModel.PanelHeight)) UpdateHeight();
        if (args.PropertyName == nameof(DownloadOverviewViewModel.IsShown) && _model?.IsShown == false) ClosePopup();
    }
    private void UpdateHeight() { if (Body != null) Body.Height = Math.Min(_model?.PanelHeight ?? DownloadOverviewSettings.DefaultHeight, MaximumBodyHeight); }
    private void ResizeStarted(object sender, DragStartedEventArgs args) => _heightBeforeDrag = _model?.PanelHeight ?? DownloadOverviewSettings.DefaultHeight;
    private void ResizeDelta(object sender, DragDeltaEventArgs args)
    {
        if (_model == null) return;
        _model.PanelHeight = Math.Min(MaximumBodyHeight, Body.ActualHeight - args.VerticalChange);
    }
    private void ResizeCompleted(object sender, DragCompletedEventArgs args)
    { if (args.Canceled && _model != null) _model.PanelHeight = _heightBeforeDrag; }
    private void ResizeKeyDown(object sender, KeyEventArgs args)
    {
        if (_model == null || !_model.IsExpanded || args.Key is not (Key.Up or Key.Down)) return;
        _model.PanelHeight = Math.Min(MaximumBodyHeight, Body.ActualHeight + (args.Key == Key.Up ? 16 : -16));
        args.Handled = true;
    }
    private void ClosePopup() => MetricsButton.CloseDropdown();
    private void MetricValueToolTipOpening(object sender, ToolTipEventArgs args)
    {
        if (sender is not TextBlock value) return;
        var text = new FormattedText(value.Text, CultureInfo.CurrentCulture, value.FlowDirection,
            new Typeface(value.FontFamily, value.FontStyle, value.FontWeight, value.FontStretch), value.FontSize,
            value.Foreground, VisualTreeHelper.GetDpi(value).PixelsPerDip);
        args.Handled = _metricDragThumb != null || value.ActualWidth <= 0 || text.WidthIncludingTrailingWhitespace <= value.ActualWidth + 0.5;
    }

    private void BuildMetricsChoices()
    {
        if (_model == null) return;
        var choices = new StackPanel { Margin = new Thickness(14) };
        var heading = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        Localize(heading, TextBlock.TextProperty, "Overview_Customize");
        choices.Children.Add(heading);
        var include = new CheckBox { Margin = new Thickness(0, 4, 0, 10) };
        _includeDisabledChoice = include;
        Localize(include, ContentControl.ContentProperty, "Overview_IncludeDisabled");
        include.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(DownloadOverviewViewModel.IncludeDisabled)) { Mode = BindingMode.TwoWay });
        choices.Children.Add(include);
        foreach (var metric in _model.Metrics)
        {
            var check = new CheckBox { DataContext = metric, Margin = new Thickness(0, 5, 0, 5), HorizontalAlignment = HorizontalAlignment.Stretch };
            check.SetBinding(ContentControl.ContentProperty, new Binding(nameof(OverviewMetricViewModel.Title)));
            check.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(OverviewMetricViewModel.IsVisible)) { Mode = BindingMode.TwoWay });
            choices.Children.Add(check);
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var option in new[] { ("Overview_SelectAll", _model.SelectAllMetricsCommand), ("Overview_Reset", _model.ResetMetricsCommand) })
        {
            var button = new Button { Command = option.Item2, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 5, 8, 5) };
            Localize(button, ContentControl.ContentProperty, option.Item1);
            button.SetResourceReference(StyleProperty, "PopupButtonStyle"); actions.Children.Add(button);
        }
        choices.Children.Add(actions);
        var surface = (MorphingPopupBorder)MetricsButton.DropdownPopup.Child;
        surface.DataContext = _model;
        surface.Width = 310;
        surface.MaxHeight = 450;
        surface.Child = new ScrollViewer { Content = choices, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        surface.SetResourceReference(Border.BackgroundProperty, "BgCardBrush");
        surface.SetResourceReference(TextElement.ForegroundProperty, "TextPrimaryBrush");
        surface.SetResourceReference(Border.BorderBrushProperty, "BorderDarkBrush");
        surface.BorderThickness = new Thickness(1);
        surface.SetResourceReference(Border.CornerRadiusProperty, "CompactCornerRadius");
        choices.KeyDown += (_, key) =>
        {
            if (key.Key != Key.Escape) return;
            ClosePopup(); MetricsButton.Focus(); key.Handled = true;
        };
    }
    private static void Localize(FrameworkElement target, DependencyProperty property, string key) =>
        target.SetBinding(property, new Binding($"[{key}]") { Source = LocalizationService.Instance, Mode = BindingMode.OneWay });
}
