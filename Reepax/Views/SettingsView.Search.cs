using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Reepax.Helpers;
using Reepax.Models;
using Reepax.Services.Localization;
using Reepax.Services.Settings;
using Reepax.ViewModels;

namespace Reepax.Views;

public partial class SettingsView
{
    private SettingsSearchIndex? _settingsSearchIndex;
    private readonly Dictionary<string, FrameworkElement> _settingsSearchTargets = new();
    private bool _isSearchingSettings, _openingSettingsSearchResult;
    private double _settingsSearchScrollOffset;
    private DispatcherOperation? _settingsSearchNavigation;
    private SearchHighlight? _settingsSearchHighlight;
    private AdornerLayer? _settingsSearchHighlightLayer;
    private int _settingsSearchNavigationVersion;
    internal bool IsSettingsSearchNavigationActive { get; private set; }
    private static readonly DependencyProperty SearchNavigationOffsetProperty = DependencyProperty.Register(
        "SearchNavigationOffset", typeof(double), typeof(SettingsView), new PropertyMetadata(0.0, (owner, args) =>
        {
            var view = (SettingsView)owner;
            if (view.IsSettingsSearchNavigationActive)
                view.SettingsContentScrollViewer.ScrollToVerticalOffset((double)args.NewValue);
        }));
    internal IReadOnlyList<SettingsSearchEntry> SettingsSearchEntries { get; private set; } = Array.Empty<SettingsSearchEntry>();

    private static IEnumerable<DependencyObject> SearchElements(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in SearchElements(child)) yield return descendant;
    }

    private static string? SearchResourceKey(DependencyObject element, DependencyProperty property)
    {
        var binding = BindingOperations.GetBinding(element, property);
        string? path = binding?.Path?.Path;
        return binding?.Source == LocalizationService.Instance && path is { Length: > 2 } && path[0] == '[' && path[^1] == ']'
            ? path[1..^1] : null;
    }

    private static IEnumerable<string> SearchResourceKeys(DependencyObject root)
    {
        foreach (var element in SearchElements(root))
        {
            foreach (var property in new[] { TextBlock.TextProperty, ContentControl.ContentProperty, FrameworkElement.ToolTipProperty })
                if (SearchResourceKey(element, property) is { } key) yield return key;
            if (element is FrameworkElement { ToolTip: DependencyObject tip })
                foreach (string key in SearchResourceKeys(tip)) yield return key;
            if (element is Helpers.SingleChoiceDropdown dropdown)
            {
                foreach (var option in dropdown.Options)
                    if (SearchResourceKey(option, Helpers.ChoiceOption.TextProperty) is { } key) yield return key;
                foreach (var theme in dropdown.ItemsSource?.OfType<Services.ThemeDefinition>() ?? Enumerable.Empty<Services.ThemeDefinition>())
                    yield return theme.LabelKey;
            }
        }
    }

    private static bool IsSearchHeading(string key) =>
        (key.StartsWith("Settings_", StringComparison.Ordinal) && key.EndsWith("_Title", StringComparison.Ordinal)) ||
        key == "Overview_Show" || (key.StartsWith("About_", StringComparison.Ordinal) &&
        (key.EndsWith("_Title", StringComparison.Ordinal) || key.EndsWith("_Label", StringComparison.Ordinal) ||
         key.StartsWith("About_Links_", StringComparison.Ordinal) || key == "About_Community_ContributeButton"));

    private static FrameworkElement SearchTarget(TextBlock heading, FrameworkElement category)
    {
        Grid? closestGrid = null;
        for (DependencyObject? current = heading.Parent; current != null && current != category; current = LogicalTreeHelper.GetParent(current))
        {
            if (current is Button or CheckBox) return (FrameworkElement)current;
            if (current is Grid { ColumnDefinitions.Count: > 0 } grid)
            {
                closestGrid ??= grid;
                if (SearchElements(grid).Any(element => element is System.Windows.Controls.Primitives.ButtonBase or TextBox or Slider))
                    return grid;
            }
        }
        return closestGrid ?? (FrameworkElement)heading;
    }

    private void BuildSettingsSearchIndex()
    {
        var entries = new List<SettingsSearchEntry>();
        _settingsSearchTargets.Clear();
        foreach (var (panel, category) in new[] { (CategoryGeneral, SettingsCategory.General),
            (CategoryDownloadConnections, SettingsCategory.DownloadConnections), (CategoryAbout, SettingsCategory.About) })
        {
            foreach (var heading in SearchElements(panel).OfType<TextBlock>())
            {
                string? key = SearchResourceKey(heading, TextBlock.TextProperty);
                if (key == null || !IsSearchHeading(key)) continue;
                var target = SearchTarget(heading, panel);
                var keys = SearchResourceKeys(target).Distinct().ToList();
                string prefix = key.EndsWith("_Title", StringComparison.Ordinal) ? key[..^6] : key;
                string description = new[] { prefix + "_Subtitle", prefix + "_Description", key == "Overview_Show" ? "Overview_Help" : "" }
                    .FirstOrDefault(candidate => Strings_en.Map.ContainsKey(candidate)) ?? string.Empty;
                var optionTexts = SearchElements(target).OfType<Helpers.SingleChoiceDropdown>()
                    .SelectMany(selector => selector.ItemsSource?.OfType<Services.Audio.SoundOption>() ?? Enumerable.Empty<Services.Audio.SoundOption>())
                    .Select(option => option.Text).ToArray();
                entries.Add(new(key, category, description, keys.ToArray()) { OptionTexts = optionTexts });
                _settingsSearchTargets.TryAdd(key, target);
            }
        }
        if (ViewModel is { } model)
            entries.AddRange(model.Shortcuts.Select(shortcut => new SettingsSearchEntry(shortcut.TitleKey,
                SettingsCategory.Shortcuts, shortcut.DescriptionKey, Array.Empty<string>())));
        SettingsSearchEntries = entries.DistinctBy(entry => entry.Key).ToArray();
        _settingsSearchIndex = new(SettingsSearchEntries);
        RefreshSettingsSearch();
    }

    private void SettingsSearch_TextChanged(object sender, TextChangedEventArgs args) => RefreshSettingsSearch();

    private void SettingsSearchLanguageChanged(string language) => RefreshSettingsSearch();

    private void RefreshSettingsSearch()
    {
        if (SettingsSearchBox == null || SettingsSearchResults == null || _settingsSearchIndex == null) return;
        if (!_openingSettingsSearchResult) CancelSettingsSearchNavigation();
        bool searching = !string.IsNullOrWhiteSpace(SettingsSearchBox.Text);
        if (searching && !_isSearchingSettings)
        {
            _settingsSearchScrollOffset = SettingsContentScrollViewer.VerticalOffset;
            CloseSettingsDropdowns();
            CancelSettingsSearchNavigation();
        }
        bool wasSearching = _isSearchingSettings;
        _isSearchingSettings = searching;
        SettingsSearchPlaceholder.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
        SettingsSearchClearButton.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        SettingsSearchResults.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        // Retain the measured card width while showing results over the content.
        SettingsContentScrollViewer.Visibility = searching ? Visibility.Hidden : Visibility.Visible;
        var results = _settingsSearchIndex.Search(SettingsSearchBox.Text);
        SettingsSearchResultList.ItemsSource = results;
        SettingsSearchResultList.SelectedIndex = results.Count > 0 ? 0 : -1;
        SettingsSearchStatus.Text = results.Count == 0 ? Loc.Get("Settings_Search_NoResults") : Loc.Format("Settings_Search_Results", results.Count);
        SettingsSearchEmptyHint.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (wasSearching && !searching && !_openingSettingsSearchResult)
            _settingsSearchNavigation = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                _settingsSearchNavigation = null;
                if (!_isSearchingSettings && IsLoaded) SettingsContentScrollViewer.ScrollToVerticalOffset(_settingsSearchScrollOffset);
            }));
    }

    private void SettingsSearch_Clear(object sender, RoutedEventArgs args)
    {
        SettingsSearchBox.Clear();
        SettingsSearchBox.Focus();
    }

    private void SettingsSearch_PreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (ViewModel?.RecordingShortcut != null || ViewModel?.SelectedSettingsCategory == SettingsCategory.About) return;
        if (IsSettingsSearchNavigationActive) CancelSettingsSearchNavigation();
        if (args.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SettingsSearchBox.Focus();
            SettingsSearchBox.SelectAll();
            args.Handled = true;
        }
        else if (args.Key == Key.Escape && _isSearchingSettings)
        {
            SettingsSearch_Clear(sender, args);
            args.Handled = true;
        }
        else if (_isSearchingSettings && args.Key == Key.Enter && SettingsSearchResultList.SelectedItem is SettingsSearchEntry entry)
        {
            OpenSettingsSearchResult(entry);
            args.Handled = true;
        }
        else if (_isSearchingSettings && ReferenceEquals(Keyboard.FocusedElement, SettingsSearchBox) && args.Key is Key.Down or Key.Up && SettingsSearchResultList.Items.Count > 0)
        {
            int direction = args.Key == Key.Down ? 1 : -1;
            SettingsSearchResultList.SelectedIndex = Math.Clamp(SettingsSearchResultList.SelectedIndex + direction, 0, SettingsSearchResultList.Items.Count - 1);
            SettingsSearchResultList.ScrollIntoView(SettingsSearchResultList.SelectedItem);
            args.Handled = true;
        }
    }

    private void SettingsSearch_ResultClick(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton == MouseButton.Left &&
            ItemsControl.ContainerFromElement(SettingsSearchResultList, args.OriginalSource as DependencyObject) is ListBoxItem { Content: SettingsSearchEntry entry })
        {
            OpenSettingsSearchResult(entry);
            args.Handled = true;
        }
    }

    internal void OpenSettingsSearchResult(SettingsSearchEntry entry)
    {
        if (ViewModel is not { } model || !SettingsSearchEntries.Contains(entry)) return;
        double resultsOpacity = SettingsSearchResults.Opacity;
        CancelSettingsSearchNavigation();
        IsSettingsSearchNavigationActive = true;
        int version = _settingsSearchNavigationVersion;
        bool animate = SystemParameters.ClientAreaAnimation && IsVisible;
        if (animate && SettingsSearchResults.IsVisible)
        {
            var fade = new DoubleAnimation(resultsOpacity, 0, TimeSpan.FromMilliseconds(90))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            fade.Completed += (_, _) =>
            {
                if (_settingsSearchNavigationVersion == version) ShowSettingsSearchDestination(entry, model, version, animate);
            };
            SettingsSearchResults.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        }
        else ShowSettingsSearchDestination(entry, model, version, animate);
    }

    private void ShowSettingsSearchDestination(SettingsSearchEntry entry, MainViewModel model, int version, bool animate)
    {
        _openingSettingsSearchResult = true;
        try
        {
            if (animate) SettingsContentScrollViewer.Opacity = 0;
            SettingsSearchBox.Clear();
            model.SelectedSettingsCategory = entry.Category;
            if (entry.Key == "Settings_Card_ArchivePasswords_Title") model.IsArchivePasswordsExpanded = true;
            if (entry.Key == "Settings_AccentColor_Title") model.IsColorPaletteOpen = true;
        }
        finally { _openingSettingsSearchResult = false; }
        SettingsSearchResults.BeginAnimation(OpacityProperty, null);
        SettingsSearchResults.Opacity = 1;
        _settingsSearchNavigation = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _settingsSearchNavigation = null;
            if (_settingsSearchNavigationVersion != version) return;
            if (!IsLoaded || !IsVisible || _isSearchingSettings || ViewModel != model || model.SelectedSettingsCategory != entry.Category)
            {
                CancelSettingsSearchNavigation();
                return;
            }
            SettingsContentScrollViewer.UpdateLayout();
            FrameworkElement? target = _settingsSearchTargets.GetValueOrDefault(entry.Key);
            if (entry.Category == SettingsCategory.Shortcuts)
            {
                var shortcut = model.Shortcuts.FirstOrDefault(shortcut => shortcut.TitleKey == entry.Key);
                target = ShortcutsItems.ItemContainerGenerator.ContainerFromItem(shortcut) as FrameworkElement;
            }
            if (target is not { IsVisible: true })
            {
                CancelSettingsSearchNavigation();
                return;
            }
            foreach (var panel in new[] { CategoryGeneral, CategoryDownloadConnections, CategoryShortcuts, CategoryAbout })
                if (panel.IsVisible) ResetCategoryAnimation(panel, panel.RenderTransform as TranslateTransform);
            double start = SettingsContentScrollViewer.VerticalOffset;
            // The viewport clamps each frame while an expanded setting increases the extent.
            double destination = Math.Max(0, start + target.TranslatePoint(new Point(), SettingsContentScrollViewer).Y - 16);
            TabContentTransition.Begin(SettingsContentScrollViewer, animate);
            if (!animate || Math.Abs(destination - start) < 1)
            {
                SettingsContentScrollViewer.ScrollToVerticalOffset(destination);
                FinishSettingsSearchNavigation(target, version);
                return;
            }
            SetValue(SearchNavigationOffsetProperty, start);
            var scroll = new DoubleAnimation(start, destination, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            scroll.Completed += (_, _) =>
            {
                if (_settingsSearchNavigationVersion != version) return;
                FinishSettingsSearchNavigation(target, version);
            };
            BeginAnimation(SearchNavigationOffsetProperty, scroll, HandoffBehavior.SnapshotAndReplace);
        }));
    }

    private void FinishSettingsSearchNavigation(FrameworkElement target, int version)
    {
        _settingsSearchNavigation = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _settingsSearchNavigation = null;
            if (_settingsSearchNavigationVersion != version) return;
            IsSettingsSearchNavigationActive = false;
            BeginAnimation(SearchNavigationOffsetProperty, null);
            SettingsContentScrollViewer.UpdateLayout();
            // Focus only after arrival, so WPF cannot interrupt the scroll animation.
            var control = SearchElements(target).OfType<Control>().FirstOrDefault(control => control.Focusable && control.IsEnabled && control.IsVisible);
            control?.Focus();
            HighlightSettingsSearchTarget(target);
        }));
    }

    private void SettingsSearchNavigation_Interrupted(object sender, RoutedEventArgs args)
    {
        if (IsSettingsSearchNavigationActive) CancelSettingsSearchNavigation();
    }

    private void CancelSettingsSearchNavigation()
    {
        _settingsSearchNavigationVersion++;
        IsSettingsSearchNavigationActive = false;
        _settingsSearchNavigation?.Abort();
        _settingsSearchNavigation = null;
        BeginAnimation(SearchNavigationOffsetProperty, null);
        SettingsSearchResults?.BeginAnimation(OpacityProperty, null);
        if (SettingsSearchResults != null) SettingsSearchResults.Opacity = 1;
        if (SettingsContentScrollViewer != null) TabContentTransition.Reset(SettingsContentScrollViewer);
        if (_settingsSearchHighlight != null)
        {
            _settingsSearchHighlight.BeginAnimation(OpacityProperty, null);
            _settingsSearchHighlightLayer?.Remove(_settingsSearchHighlight);
        }
        _settingsSearchHighlight = null;
        _settingsSearchHighlightLayer = null;
    }

    private void HighlightSettingsSearchTarget(FrameworkElement target)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        if (AdornerLayer.GetAdornerLayer(target) is not { } layer || TryFindResource("AccentBlueBrush") is not Brush brush) return;
        var highlight = new SearchHighlight(target, brush) { IsHitTestVisible = false };
        _settingsSearchHighlight = highlight;
        _settingsSearchHighlightLayer = layer;
        layer.Add(highlight);
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, TimeSpan.Zero));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(300)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, TimeSpan.FromMilliseconds(1000))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        animation.Completed += (_, _) =>
        {
            layer.Remove(highlight);
            if (ReferenceEquals(_settingsSearchHighlight, highlight))
            {
                _settingsSearchHighlight = null;
                _settingsSearchHighlightLayer = null;
            }
        };
        highlight.BeginAnimation(OpacityProperty, animation);
    }

    private sealed class SearchHighlight(UIElement target, Brush brush) : Adorner(target)
    {
        protected override void OnRender(DrawingContext context)
        {
            var fill = brush.CloneCurrentValue();
            fill.Opacity = 0.16;
            context.DrawRectangle(fill, null, new Rect(AdornedElement.RenderSize));
        }
    }
}
