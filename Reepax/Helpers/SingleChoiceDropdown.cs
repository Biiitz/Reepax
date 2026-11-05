using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Path = System.Windows.Shapes.Path;

namespace Reepax.Helpers;

public sealed class ChoiceOption : DependencyObject
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(ChoiceOption));
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(object), typeof(ChoiceOption));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public object Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
}

[ContentProperty(nameof(Options))]
public sealed class SingleChoiceDropdown : Button
{
    private static readonly DependencyPropertyKey IsDropdownOpenPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsDropdownOpen), typeof(bool), typeof(SingleChoiceDropdown), new PropertyMetadata(false));
    public static readonly DependencyProperty IsDropdownOpenProperty = IsDropdownOpenPropertyKey.DependencyProperty;
    public bool IsDropdownOpen => (bool)GetValue(IsDropdownOpenProperty);
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(SingleChoiceDropdown), new PropertyMetadata(null, Rebuild));
    public static readonly DependencyProperty SelectedValueProperty = DependencyProperty.Register(nameof(SelectedValue), typeof(object), typeof(SingleChoiceDropdown), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, Refresh));
    public static readonly DependencyProperty DisplayMemberPathProperty = DependencyProperty.Register(nameof(DisplayMemberPath), typeof(string), typeof(SingleChoiceDropdown), new PropertyMetadata(nameof(ChoiceOption.Text), Rebuild));
    public static readonly DependencyProperty SelectedValuePathProperty = DependencyProperty.Register(nameof(SelectedValuePath), typeof(string), typeof(SingleChoiceDropdown), new PropertyMetadata(nameof(ChoiceOption.Value), Rebuild));
    public IEnumerable? ItemsSource { get => (IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public object? SelectedValue { get => GetValue(SelectedValueProperty); set => SetValue(SelectedValueProperty, value); }
    public string DisplayMemberPath { get => (string)GetValue(DisplayMemberPathProperty); set => SetValue(DisplayMemberPathProperty, value); }
    public string SelectedValuePath { get => (string)GetValue(SelectedValuePathProperty); set => SetValue(SelectedValuePathProperty, value); }
    public ObservableCollection<ChoiceOption> Options { get; } = new();
    public object? SelectedItem => _entries.FirstOrDefault(entry => Equals(entry.Value, SelectedValue)).Item;
    public Popup DropdownPopup { get; }
    public double PopupVerticalOffset
    {
        get => DropdownPopup.VerticalOffset;
        set => DropdownPopup.VerticalOffset = value;
    }
    public PlacementMode PopupPlacement
    {
        get => DropdownPopup.Placement;
        set
        {
            DropdownPopup.Placement = value;
            DropdownPopup.VerticalOffset = value == PlacementMode.Top ? -4 : 4;
        }
    }
    public event EventHandler? SelectionCommitted;
    private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 440 };
    private readonly Path _arrow = new() { Data = Geometry.Parse("M 0,0 L 3.5,3.5 L 7,0"), StrokeThickness = 1.5, Width = 7, Height = 4, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _optionsPanel = new();
    private readonly List<(object Item, object? Value, Path Check)> _entries = new();
    private long _closedAt = long.MinValue / 2;
    private bool _closeRequested;

    public SingleChoiceDropdown()
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Reepax;component/Themes/ChoiceControls.xaml", UriKind.Relative) });
        SetResourceReference(StyleProperty, "SoundDropdownButtonStyle");
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Center;
        FontSize = 11.5;
        FontWeight = FontWeights.SemiBold;
        _arrow.SetResourceReference(ShapeStrokeProperty, "TextSecondaryBrush");
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.Children.Add(_label);
        Grid.SetColumn(_arrow, 1);
        content.Children.Add(_arrow);
        Content = content;
        var border = new MorphingPopupBorder { Child = new ScrollViewer { Content = _optionsPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 360 } };
        border.Style = (Style)FindResource("SettingsDropdownPopupBorderStyle");
        border.SetBinding(System.Windows.Documents.TextElement.FontFamilyProperty, new Binding(nameof(FontFamily)) { Source = this });
        DropdownPopup = new Popup { PlacementTarget = this, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade, VerticalOffset = 4, Child = border };
        PopupLayout.SetAlignRight(DropdownPopup, true);
        PopupMorph.SetIsEnabled(DropdownPopup, true);
        AddLogicalChild(DropdownPopup);
        DropdownPopup.Opened += (_, _) => { _closeRequested = false; SetValue(IsDropdownOpenPropertyKey, true); ControlMotion.SetIsExpanded(_arrow, true); };
        DropdownPopup.Closed += (_, _) => { if (!_closeRequested) _closedAt = Environment.TickCount64; SetValue(IsDropdownOpenPropertyKey, false); ControlMotion.SetIsExpanded(_arrow, false); };
        Options.CollectionChanged += (_, _) => RebuildOptions();
        Click += (_, args) => { if (ReferenceEquals(args.OriginalSource, this)) ToggleDropdown(); };
        Loaded += (_, _) => RebuildOptions();
        Unloaded += (_, _) => CloseDropdown();
        DataContextChanged += (_, _) => CloseDropdown();
        IsVisibleChanged += (_, _) => { if (!IsVisible) CloseDropdown(); };
        IsEnabledChanged += (_, _) => { if (!IsEnabled) CloseDropdown(); };
        DropdownPopup.PreviewKeyDown += HandleKey;
    }

    private static readonly DependencyProperty ShapeStrokeProperty = System.Windows.Shapes.Shape.StrokeProperty;
    protected override IEnumerator LogicalChildren
    {
        get
        {
            if (DropdownPopup == null) return base.LogicalChildren;
            return new object[] { Content, DropdownPopup }.GetEnumerator();
        }
    }
    private static void Rebuild(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var selector = (SingleChoiceDropdown)sender;
        if (args.Property == ItemsSourceProperty)
        {
            if (args.OldValue is INotifyCollectionChanged previous)
                CollectionChangedEventManager.RemoveHandler(previous, selector.SourceChanged);
            if (args.NewValue is INotifyCollectionChanged current)
                CollectionChangedEventManager.AddHandler(current, selector.SourceChanged);
        }
        selector.RebuildOptions();
    }
    private void SourceChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (Dispatcher.CheckAccess()) RebuildOptions();
        else _ = Dispatcher.BeginInvoke(new Action(RebuildOptions));
    }
    private static void Refresh(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((SingleChoiceDropdown)sender).RefreshSelection();

    private void RebuildOptions()
    {
        if (_optionsPanel == null) return;
        _entries.Clear();
        _optionsPanel.Children.Clear();
        foreach (object item in ItemsSource ?? Options)
        {
            object? value = Read(item, SelectedValuePath);
            var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 440, FontSize = 11.5, FontWeight = FontWeights.Normal };
            BindLabel(label, item);
            var check = new Path { Width = 11, Height = 11, Stretch = Stretch.Uniform, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            check.SetResourceReference(Path.DataProperty, "IconCheck");
            check.SetResourceReference(Path.FillProperty, "TextPrimaryBrush");
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(label);
            Grid.SetColumn(check, 1);
            row.Children.Add(check);
            var button = new Button { Content = row, CommandParameter = value };
            button.Style = (Style)FindResource("SoundDropdownItemStyle");
            button.Click += (_, _) => Commit(value);
            _optionsPanel.Children.Add(button);
            _entries.Add((item, value, check));
        }
        RefreshSelection();
    }

    private void BindLabel(TextBlock label, object item)
    {
        if (string.IsNullOrEmpty(DisplayMemberPath)) label.Text = item.ToString();
        else label.SetBinding(TextBlock.TextProperty, new Binding(DisplayMemberPath) { Source = item });
    }

    private static object? Read(object item, string path) => string.IsNullOrEmpty(path) ? item : TypeDescriptor.GetProperties(item)[path]?.GetValue(item);

    private void RefreshSelection()
    {
        if (_entries == null) return;
        var selected = _entries.FirstOrDefault(entry => Equals(entry.Value, SelectedValue));
        BindingOperations.ClearBinding(_label, TextBlock.TextProperty);
        _label.Text = string.Empty;
        if (selected.Item != null) BindLabel(_label, selected.Item);
        foreach (var entry in _entries) entry.Check.Visibility = Equals(entry.Value, SelectedValue) ? Visibility.Visible : Visibility.Hidden;
    }

    public void CloseDropdown()
    {
        if (DropdownPopup.IsOpen)
        {
            // Measure the click guard from the request, not the end of WPF's fade-out.
            _closeRequested = true;
            _closedAt = Environment.TickCount64;
        }
        DropdownPopup.IsOpen = false;
        SetValue(IsDropdownOpenPropertyKey, false);
        ControlMotion.SetIsExpanded(_arrow, false);
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs args)
    {
        if (args.OriginalSource is DependencyObject source && DropdownPopup.Child is FrameworkElement popupChild &&
            (ReferenceEquals(source, popupChild) || popupChild.IsAncestorOf(source)))
        {
            base.OnPreviewMouseDown(args);
            return;
        }
        if (args.ChangedButton == MouseButton.Left && (DropdownPopup.IsOpen || Environment.TickCount64 - _closedAt < 350))
        {
            CloseDropdown();
            _closedAt = Environment.TickCount64;
            args.Handled = true;
        }
        base.OnPreviewMouseDown(args);
    }

    private void ToggleDropdown()
    {
        if (DropdownPopup.IsOpen) { CloseDropdown(); return; }
        if (Environment.TickCount64 - _closedAt < 350) return;
        var border = (Border)DropdownPopup.Child;
        border.MinWidth = ActualWidth;
        border.MaxWidth = Math.Min(500, SystemParameters.WorkArea.Width);
        DropdownPopup.IsOpen = !DropdownPopup.IsOpen;
    }

    private void Commit(object? value)
    {
        SetCurrentValue(SelectedValueProperty, value);
        GetBindingExpression(SelectedValueProperty)?.UpdateSource();
        SelectionCommitted?.Invoke(this, EventArgs.Empty);
        CloseDropdown();
        Focus();
    }

    private void FocusOption(bool forward)
    {
        if (_optionsPanel.Children.Count == 0) return;
        if (!DropdownPopup.IsOpen) ToggleDropdown();
        if (!DropdownPopup.IsOpen) return;
        var options = _optionsPanel.Children.Cast<Button>().ToArray();
        int current = Array.FindIndex(options, option => option.IsKeyboardFocusWithin);
        int next = current < 0 ? Math.Max(0, _entries.FindIndex(entry => Equals(entry.Value, SelectedValue)))
            : (current + (forward ? 1 : -1) + options.Length) % options.Length;
        options[next].Focus();
        options[next].BringIntoView();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs args) { HandleKey(this, args); if (!args.Handled) base.OnPreviewKeyDown(args); }
    private void HandleKey(object sender, KeyEventArgs args)
    {
        if (args.Key is Key.Down or Key.Up) FocusOption(args.Key == Key.Down);
        else if (args.Key == Key.Escape && DropdownPopup.IsOpen) { CloseDropdown(); Focus(); }
        else return;
        args.Handled = true;
    }

}
