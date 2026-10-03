using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Reepax.ViewModels;

namespace Reepax.Views;

public partial class SettingsView : UserControl
{
    private bool _hasInitializedCategory;
    private MainViewModel? _hookedVm;

    public SettingsView()
    {
        InitializeComponent();
        Loaded += SettingsView_Loaded;
        Unloaded += SettingsView_Unloaded;
        DataContextChanged += SettingsView_DataContextChanged;
        IsVisibleChanged += SettingsView_IsVisibleChanged;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private bool _isCategoryIndicatorInitialized;

    private void SettingsView_Loaded(object sender, RoutedEventArgs e)
    {
        HookViewModel();
        UpdateGameInstallFolderVisibility(animate: false);
        UpdateCompletionSoundVolumeVisibility(animate: false);
        UpdateErrorSoundVolumeVisibility(animate: false);

        if (GeneralCategoryButton != null)
            GeneralCategoryButton.SizeChanged += (_, _) => UpdateSlidingCategoryIndicator(animate: false);
        if (DownloadsCategoryButton != null)
            DownloadsCategoryButton.SizeChanged += (_, _) => UpdateSlidingCategoryIndicator(animate: false);
        if (ShortcutsCategoryButton != null)
            ShortcutsCategoryButton.SizeChanged += (_, _) => UpdateSlidingCategoryIndicator(animate: false);
        if (AboutCategoryButton != null)
            AboutCategoryButton.SizeChanged += (_, _) => UpdateSlidingCategoryIndicator(animate: false);
        if (CategoryButtonsContainer != null)
            CategoryButtonsContainer.SizeChanged += (_, _) => UpdateSlidingCategoryIndicator(animate: false);

        Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateSlidingCategoryIndicator(animate: false);
            _isCategoryIndicatorInitialized = true;
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void SettingsView_Unloaded(object sender, RoutedEventArgs e)
    {
        UnhookViewModel();
    }

    private void SettingsView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        HookViewModel();
        UpdateGameInstallFolderVisibility(animate: false);
        UpdateCompletionSoundVolumeVisibility(animate: false);
        UpdateErrorSoundVolumeVisibility(animate: false);
        Dispatcher.BeginInvoke(new Action(() => UpdateSlidingCategoryIndicator(animate: false)),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void SettingsView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            Dispatcher.BeginInvoke(new Action(() => UpdateSlidingCategoryIndicator(animate: false)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void HookViewModel()
    {
        if (_hookedVm != null)
        {
            _hookedVm.PropertyChanged -= ViewModel_PropertyChanged;
        }
        _hookedVm = ViewModel;
        if (_hookedVm != null)
        {
            _hookedVm.PropertyChanged += ViewModel_PropertyChanged;
        }
    }

    private void UnhookViewModel()
    {
        if (_hookedVm != null)
        {
            _hookedVm.PropertyChanged -= ViewModel_PropertyChanged;
            _hookedVm = null;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CreateGameInstallFolder))
        {
            UpdateGameInstallFolderVisibility(animate: true);
        }
        else if (e.PropertyName == nameof(MainViewModel.EnableCompletionSound))
        {
            UpdateCompletionSoundVolumeVisibility(animate: true);
        }
        else if (e.PropertyName == nameof(MainViewModel.EnableErrorSound))
        {
            UpdateErrorSoundVolumeVisibility(animate: true);
        }
        else if (e.PropertyName == nameof(MainViewModel.SelectedSettingsCategory))
        {
            Dispatcher.BeginInvoke(new Action(() => UpdateSlidingCategoryIndicator(animate: true)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void UpdateSlidingCategoryIndicator(bool animate = true)
    {
        if (GeneralCategoryButton == null || DownloadsCategoryButton == null || 
            ShortcutsCategoryButton == null || AboutCategoryButton == null || 
            CategoryIndicatorTransform == null || 
            SlidingCategoryIndicator == null || CategoryButtonsContainer == null)
            return;

        var category = ViewModel?.SelectedSettingsCategory ?? SettingsCategory.General;
        Button targetButton = category switch
        {
            SettingsCategory.General or SettingsCategory.Notifications => GeneralCategoryButton,
            SettingsCategory.DownloadConnections => DownloadsCategoryButton,
            SettingsCategory.Shortcuts => ShortcutsCategoryButton,
            SettingsCategory.About => AboutCategoryButton,
            _ => GeneralCategoryButton
        };

        double targetHeight = targetButton.ActualHeight;
        if (targetHeight <= 0)
        {
            targetButton.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            targetHeight = targetButton.DesiredSize.Height > 0 ? targetButton.DesiredSize.Height : 36.0;
        }

        Point relativePoint;
        try
        {
            relativePoint = targetButton.TranslatePoint(new Point(0, 0), CategoryButtonsContainer);
        }
        catch
        {
            return;
        }

        double pillHeight = 18.0;
        double targetX = relativePoint.X + 12.0;
        double targetY = relativePoint.Y + (targetHeight - pillHeight) / 2.0;

        CategoryIndicatorTransform.X = targetX;

        if (!animate || !_isCategoryIndicatorInitialized)
        {
            CategoryIndicatorTransform.BeginAnimation(TranslateTransform.YProperty, null);
            CategoryIndicatorTransform.Y = targetY;
            SlidingCategoryIndicator.Opacity = 1.0;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(130);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var moveAnim = new DoubleAnimation
        {
            To = targetY,
            Duration = duration,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        };

        CategoryIndicatorTransform.BeginAnimation(TranslateTransform.YProperty, moveAnim);
    }

    private void SpeedLimit_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ViewModel == null) return;
        if (e.Delta > 0) ViewModel.IncrementSpeedLimit();
        else if (e.Delta < 0) ViewModel.DecrementSpeedLimit();
        e.Handled = true;
    }

    private void MaxDownloads_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ViewModel == null) return;
        if (e.Delta > 0) ViewModel.IncrementMaxDownloads();
        else if (e.Delta < 0) ViewModel.DecrementMaxDownloads();
        e.Handled = true;
    }

    private void Connections_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ViewModel == null) return;
        if (e.Delta > 0) ViewModel.IncrementConnections();
        else if (e.Delta < 0) ViewModel.DecrementConnections();
        e.Handled = true;
    }

    private void SettingsScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Allow child controls with their own wheel handlers (numeric spinner textboxes) to process the event
        if (e.OriginalSource is DependencyObject dep && FindVisualParent<TextBox>(dep) is { } tb && Equals(tb.Tag, "NumericSpinner"))
        {
            return;
        }

        if (sender is ScrollViewer scv)
        {
            scv.ScrollToVerticalOffset(scv.VerticalOffset - (e.Delta * 0.5));
            e.Handled = true;
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T parent) return parent;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private void Category_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;

        var transform = (element.RenderTransform as TranslateTransform)
                     ?? (element.RenderTransform = new TranslateTransform()) as TranslateTransform;

        if (element.IsVisible)
        {
            // Reset scroll position to top when switching category
            SettingsContentScrollViewer?.ScrollToTop();

            // Skip initial animation on cold start for the default General category
            if (!_hasInitializedCategory && element == CategoryGeneral)
            {
                _hasInitializedCategory = true;
                return;
            }
            _hasInitializedCategory = true;

            AnimateCategoryIn(element, transform);
        }
        else
        {
            ResetCategoryAnimation(element, transform);
        }
    }

    private static void ResetCategoryAnimation(FrameworkElement element, TranslateTransform? transform)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1.0;
        if (transform != null)
        {
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.Y = 0.0;
        }
    }

    private void AnimateCategoryIn(FrameworkElement element, TranslateTransform? transform)
    {
        ResetCategoryAnimation(element, transform);

        var duration = TimeSpan.FromMilliseconds(220);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var fadeAnim = new DoubleAnimation
        {
            From = 0.0,
            To = 1.0,
            Duration = duration,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        };

        fadeAnim.Completed += (s, e) =>
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1.0;
        };

        element.BeginAnimation(UIElement.OpacityProperty, fadeAnim);

        if (transform != null)
        {
            var slideAnim = new DoubleAnimation
            {
                From = 16.0,
                To = 0.0,
                Duration = duration,
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd
            };

            slideAnim.Completed += (s, e) =>
            {
                transform.BeginAnimation(TranslateTransform.YProperty, null);
                transform.Y = 0.0;
            };

            transform.BeginAnimation(TranslateTransform.YProperty, slideAnim);
        }
    }

    #region Smooth Numeric Roll Animation

    private string _lastSpeedLimit = "";
    private string _lastMaxDownloads = "";
    private string _lastConnections = "";

    private void SpeedLimitBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string newText = SpeedLimitBox?.Text ?? "";
        if (!string.IsNullOrEmpty(_lastSpeedLimit) && _lastSpeedLimit != newText && SpeedLimitGhostText != null && SpeedLimitTranslate != null && SpeedLimitGhostTranslate != null)
        {
            AnimateNumericChange(SpeedLimitBox!, SpeedLimitGhostText, SpeedLimitTranslate, SpeedLimitGhostTranslate, _lastSpeedLimit, newText);
        }
        _lastSpeedLimit = newText;
    }

    private void MaxDownloadsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string newText = MaxDownloadsBox?.Text ?? "";
        if (!string.IsNullOrEmpty(_lastMaxDownloads) && _lastMaxDownloads != newText && MaxDownloadsGhostText != null && MaxDownloadsTranslate != null && MaxDownloadsGhostTranslate != null)
        {
            AnimateNumericChange(MaxDownloadsBox!, MaxDownloadsGhostText, MaxDownloadsTranslate, MaxDownloadsGhostTranslate, _lastMaxDownloads, newText);
        }
        _lastMaxDownloads = newText;
    }

    private void ConnectionsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string newText = ConnectionsBox?.Text ?? "";
        if (!string.IsNullOrEmpty(_lastConnections) && _lastConnections != newText && ConnectionsGhostText != null && ConnectionsTranslate != null && ConnectionsGhostTranslate != null)
        {
            AnimateNumericChange(ConnectionsBox!, ConnectionsGhostText, ConnectionsTranslate, ConnectionsGhostTranslate, _lastConnections, newText);
        }
        _lastConnections = newText;
    }

    private void AnimateNumericChange(
        TextBox textBox,
        TextBlock ghostText,
        TranslateTransform textTranslate,
        TranslateTransform ghostTranslate,
        string oldValStr,
        string newValStr)
    {
        if (textBox.IsKeyboardFocused) return;
        if (string.Equals(oldValStr, newValStr, StringComparison.Ordinal)) return;

        if (!double.TryParse(oldValStr.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double oldVal) ||
            !double.TryParse(newValStr.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double newVal))
        {
            return;
        }

        if (Math.Abs(oldVal - newVal) < 0.0001) return;

        bool isUp = newVal > oldVal;
        double offset = 14.0;
        double ghostTargetY = isUp ? -offset : offset;
        double textStartY = isUp ? offset : -offset;

        ghostText.Text = oldValStr;
        ghostText.Opacity = 1.0;
        ghostTranslate.Y = 0.0;

        var duration = TimeSpan.FromMilliseconds(240);
        var bounceEase = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 };
        var smoothEase = new CubicEase { EasingMode = EasingMode.EaseOut };

        // Animate ghost out
        var ghostSlide = new DoubleAnimation(0.0, ghostTargetY, duration) { EasingFunction = smoothEase };
        var ghostFade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(180)) { EasingFunction = smoothEase };
        ghostTranslate.BeginAnimation(TranslateTransform.YProperty, ghostSlide);
        ghostText.BeginAnimation(UIElement.OpacityProperty, ghostFade);

        // Animate textBox in with subtle bounce
        var textSlide = new DoubleAnimation(textStartY, 0.0, duration) { EasingFunction = bounceEase };
        var textFade = new DoubleAnimation(0.0, 1.0, duration) { EasingFunction = smoothEase };

        textFade.Completed += (s, e) =>
        {
            textTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            textTranslate.Y = 0.0;
            textBox.BeginAnimation(UIElement.OpacityProperty, null);
            textBox.Opacity = 1.0;
            ghostText.Opacity = 0.0;
        };

        textTranslate.BeginAnimation(TranslateTransform.YProperty, textSlide);
        textBox.BeginAnimation(UIElement.OpacityProperty, textFade);
    }

    #endregion

    #region Game Install Directory Smooth Expand / Collapse Animation

    private bool _isGameInstallExpanded;

    private void UpdateGameInstallFolderVisibility(bool animate)
    {
        if (GameInstallPathContainer == null || GameInstallPathContent == null || GameInstallPathTranslate == null)
            return;

        bool isEnabled = ViewModel?.CreateGameInstallFolder == true;
        if (!animate)
        {
            _isGameInstallExpanded = isEnabled;
            GameInstallPathContainer.BeginAnimation(FrameworkElement.HeightProperty, null);
            GameInstallPathContent.BeginAnimation(UIElement.OpacityProperty, null);
            GameInstallPathTranslate.BeginAnimation(TranslateTransform.YProperty, null);

            if (isEnabled)
            {
                GameInstallPathContainer.Visibility = Visibility.Visible;
                GameInstallPathContainer.Height = double.NaN;
                GameInstallPathContent.Opacity = 1.0;
                GameInstallPathTranslate.Y = 0.0;
            }
            else
            {
                GameInstallPathContainer.Visibility = Visibility.Collapsed;
                GameInstallPathContainer.Height = 0.0;
                GameInstallPathContent.Opacity = 0.0;
                GameInstallPathTranslate.Y = -8.0;
            }
            return;
        }

        if (_isGameInstallExpanded == isEnabled)
            return;

        _isGameInstallExpanded = isEnabled;

        if (isEnabled)
        {
            // Expanding smoothly
            GameInstallPathContainer.Visibility = Visibility.Visible;
            GameInstallPathContainer.Height = double.NaN;
            GameInstallPathContainer.Measure(new Size(
                GameInstallPathContainer.ActualWidth > 0 ? GameInstallPathContainer.ActualWidth : 500,
                double.PositiveInfinity));
            double targetHeight = GameInstallPathContainer.DesiredSize.Height;
            if (targetHeight <= 0) targetHeight = 62.0;

            GameInstallPathContainer.Height = 0.0;
            GameInstallPathContent.Opacity = 0.0;
            GameInstallPathTranslate.Y = -8.0;

            var duration = TimeSpan.FromMilliseconds(280);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            var heightAnim = new DoubleAnimation
            {
                From = 0.0,
                To = targetHeight,
                Duration = duration,
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd
            };

            var opacityAnim = new DoubleAnimation
            {
                From = 0.0,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(240),
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd
            };

            var slideAnim = new DoubleAnimation
            {
                From = -8.0,
                To = 0.0,
                Duration = duration,
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd
            };

            heightAnim.Completed += (s, e) =>
            {
                GameInstallPathContainer.BeginAnimation(FrameworkElement.HeightProperty, null);
                GameInstallPathContainer.Height = double.NaN;
                GameInstallPathContent.BeginAnimation(UIElement.OpacityProperty, null);
                GameInstallPathContent.Opacity = 1.0;
                GameInstallPathTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                GameInstallPathTranslate.Y = 0.0;
            };

            GameInstallPathContainer.BeginAnimation(FrameworkElement.HeightProperty, heightAnim);
            GameInstallPathContent.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
            GameInstallPathTranslate.BeginAnimation(TranslateTransform.YProperty, slideAnim);
        }
        else
        {
            // Collapsing smoothly
            double startHeight = GameInstallPathContainer.ActualHeight;
            if (startHeight <= 0) startHeight = 62.0;

            var duration = TimeSpan.FromMilliseconds(240);
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

            var heightAnim = new DoubleAnimation
            {
                From = startHeight,
                To = 0.0,
                Duration = duration,
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd
            };

            var opacityAnim = new DoubleAnimation
            {
                From = GameInstallPathContent.Opacity,
                To = 0.0,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd
            };

            var slideAnim = new DoubleAnimation
            {
                From = 0.0,
                To = -8.0,
                Duration = duration,
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd
            };

            heightAnim.Completed += (s, e) =>
            {
                GameInstallPathContainer.BeginAnimation(FrameworkElement.HeightProperty, null);
                GameInstallPathContainer.Height = 0.0;
                GameInstallPathContainer.Visibility = Visibility.Collapsed;
                GameInstallPathContent.BeginAnimation(UIElement.OpacityProperty, null);
                GameInstallPathContent.Opacity = 0.0;
                GameInstallPathTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                GameInstallPathTranslate.Y = -8.0;
            };

            GameInstallPathContainer.BeginAnimation(FrameworkElement.HeightProperty, heightAnim);
            GameInstallPathContent.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
            GameInstallPathTranslate.BeginAnimation(TranslateTransform.YProperty, slideAnim);
        }
    }

    #endregion

    private void NewArchivePasswordTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (ViewModel?.AddArchivePasswordCommand.CanExecute(null) == true)
            {
                ViewModel.AddArchivePasswordCommand.Execute(null);
            }
            e.Handled = true;
        }
    }

    #region Sound Settings Popups, Volume Animation & Controls

    private bool _isCompletionSoundVolumeExpanded;
    private bool _isErrorSoundVolumeExpanded;

    private long _completionSoundClosedTimestamp;
    private long _errorSoundClosedTimestamp;

    private void UpdateCompletionSoundVolumeVisibility(bool animate = true)
    {
        if (CompletionSoundVolumeContainer == null || CompletionSoundVolumeContent == null || CompletionSoundVolumeTranslate == null)
            return;

        bool isEnabled = ViewModel?.EnableCompletionSound == true;
        AnimateExpander(CompletionSoundVolumeContainer, CompletionSoundVolumeContent, CompletionSoundVolumeTranslate, ref _isCompletionSoundVolumeExpanded, isEnabled, animate, 42.0);
    }

    private void UpdateErrorSoundVolumeVisibility(bool animate = true)
    {
        if (ErrorSoundVolumeContainer == null || ErrorSoundVolumeContent == null || ErrorSoundVolumeTranslate == null)
            return;

        bool isEnabled = ViewModel?.EnableErrorSound == true;
        AnimateExpander(ErrorSoundVolumeContainer, ErrorSoundVolumeContent, ErrorSoundVolumeTranslate, ref _isErrorSoundVolumeExpanded, isEnabled, animate, 42.0);
    }

    private static void AnimateExpander(Border container, FrameworkElement content, TranslateTransform translate, ref bool isExpanded, bool targetExpanded, bool animate, double fallbackHeight)
    {
        if (container == null || content == null || translate == null)
            return;

        if (!animate)
        {
            isExpanded = targetExpanded;
            container.BeginAnimation(FrameworkElement.HeightProperty, null);
            content.BeginAnimation(UIElement.OpacityProperty, null);
            translate.BeginAnimation(TranslateTransform.YProperty, null);

            if (targetExpanded)
            {
                container.Visibility = Visibility.Visible;
                container.Height = double.NaN;
                content.Opacity = 1.0;
                translate.Y = 0.0;
            }
            else
            {
                container.Visibility = Visibility.Collapsed;
                container.Height = 0.0;
                content.Opacity = 0.0;
                translate.Y = -8.0;
            }
            return;
        }

        if (isExpanded == targetExpanded)
            return;

        isExpanded = targetExpanded;

        if (targetExpanded)
        {
            container.Visibility = Visibility.Visible;
            container.Height = double.NaN;
            container.Measure(new Size(
                container.ActualWidth > 0 ? container.ActualWidth : 500,
                double.PositiveInfinity));
            double targetHeight = container.DesiredSize.Height;
            if (targetHeight <= 0) targetHeight = fallbackHeight;

            container.Height = 0.0;
            content.Opacity = 0.0;
            translate.Y = -8.0;

            var duration = TimeSpan.FromMilliseconds(280);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            var heightAnim = new DoubleAnimation
            {
                From = 0.0,
                To = targetHeight,
                Duration = duration,
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd
            };

            var opacityAnim = new DoubleAnimation
            {
                From = 0.0,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(240),
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd
            };

            var slideAnim = new DoubleAnimation
            {
                From = -8.0,
                To = 0.0,
                Duration = duration,
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd
            };

            heightAnim.Completed += (s, e) =>
            {
                container.BeginAnimation(FrameworkElement.HeightProperty, null);
                container.Height = double.NaN;
                content.BeginAnimation(UIElement.OpacityProperty, null);
                content.Opacity = 1.0;
                translate.BeginAnimation(TranslateTransform.YProperty, null);
                translate.Y = 0.0;
            };

            container.BeginAnimation(FrameworkElement.HeightProperty, heightAnim);
            content.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
            translate.BeginAnimation(TranslateTransform.YProperty, slideAnim);
        }
        else
        {
            double startHeight = container.ActualHeight;
            if (startHeight <= 0) startHeight = fallbackHeight;

            var duration = TimeSpan.FromMilliseconds(240);
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

            var heightAnim = new DoubleAnimation
            {
                From = startHeight,
                To = 0.0,
                Duration = duration,
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd
            };

            var opacityAnim = new DoubleAnimation
            {
                From = content.Opacity,
                To = 0.0,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd
            };

            var slideAnim = new DoubleAnimation
            {
                From = 0.0,
                To = -8.0,
                Duration = duration,
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd
            };

            heightAnim.Completed += (s, e) =>
            {
                container.BeginAnimation(FrameworkElement.HeightProperty, null);
                container.Height = 0.0;
                container.Visibility = Visibility.Collapsed;
                content.BeginAnimation(UIElement.OpacityProperty, null);
                content.Opacity = 0.0;
                translate.BeginAnimation(TranslateTransform.YProperty, null);
                translate.Y = -8.0;
            };

            container.BeginAnimation(FrameworkElement.HeightProperty, heightAnim);
            content.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
            translate.BeginAnimation(TranslateTransform.YProperty, slideAnim);
        }
    }

    private void CompletionSoundPopup_Closed(object? sender, EventArgs e)
    {
        _completionSoundClosedTimestamp = Environment.TickCount64;
    }

    private void CompletionSoundButton_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (CompletionSoundPopup.IsOpen || (Environment.TickCount64 - _completionSoundClosedTimestamp < 350))
        {
            CompletionSoundPopup.IsOpen = false;
            _completionSoundClosedTimestamp = Environment.TickCount64;
            e.Handled = true;
        }
    }

    private void CompletionSoundButton_Click(object sender, RoutedEventArgs e)
    {
        if (Environment.TickCount64 - _completionSoundClosedTimestamp < 350)
            return;

        CompletionSoundPopup.IsOpen = !CompletionSoundPopup.IsOpen;
    }

    private void CompletionSoundOption_Click(object sender, RoutedEventArgs e)
    {
        CompletionSoundPopup.IsOpen = false;
    }

    private void CompletionSound_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            ViewModel?.CycleCompletionSound(true);
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            ViewModel?.CycleCompletionSound(false);
            e.Handled = true;
        }
    }

    private void CompletionSound_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta < 0)
        {
            ViewModel?.CycleCompletionSound(true);
            e.Handled = true;
        }
        else if (e.Delta > 0)
        {
            ViewModel?.CycleCompletionSound(false);
            e.Handled = true;
        }
    }

    private void ErrorSoundPopup_Closed(object? sender, EventArgs e)
    {
        _errorSoundClosedTimestamp = Environment.TickCount64;
    }

    private void ErrorSoundButton_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ErrorSoundPopup.IsOpen || (Environment.TickCount64 - _errorSoundClosedTimestamp < 350))
        {
            ErrorSoundPopup.IsOpen = false;
            _errorSoundClosedTimestamp = Environment.TickCount64;
            e.Handled = true;
        }
    }

    private void ErrorSoundButton_Click(object sender, RoutedEventArgs e)
    {
        if (Environment.TickCount64 - _errorSoundClosedTimestamp < 350)
            return;

        ErrorSoundPopup.IsOpen = !ErrorSoundPopup.IsOpen;
    }

    private void ErrorSoundOption_Click(object sender, RoutedEventArgs e)
    {
        ErrorSoundPopup.IsOpen = false;
    }

    private void ErrorSound_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            ViewModel?.CycleErrorSound(true);
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            ViewModel?.CycleErrorSound(false);
            e.Handled = true;
        }
    }

    private void ErrorSound_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta < 0)
        {
            ViewModel?.CycleErrorSound(true);
            e.Handled = true;
        }
        else if (e.Delta > 0)
        {
            ViewModel?.CycleErrorSound(false);
            e.Handled = true;
        }
    }

    private void CompletionSoundVolumeSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        ViewModel?.PreviewCompletionVolumeSound();
    }

    private void CompletionSoundVolumeSlider_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ViewModel != null && ViewModel.EnableCompletionSound)
        {
            int delta = e.Delta > 0 ? 5 : -5;
            ViewModel.CompletionSoundVolume = Math.Clamp(ViewModel.CompletionSoundVolume + delta, 0, 100);
            ViewModel.PreviewCompletionVolumeSound();
            e.Handled = true;
        }
    }

    private void ErrorSoundVolumeSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        ViewModel?.PreviewErrorVolumeSound();
    }

    private void ErrorSoundVolumeSlider_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ViewModel != null && ViewModel.EnableErrorSound)
        {
            int delta = e.Delta > 0 ? 5 : -5;
            ViewModel.ErrorSoundVolume = Math.Clamp(ViewModel.ErrorSoundVolume + delta, 0, 100);
            ViewModel.PreviewErrorVolumeSound();
            e.Handled = true;
        }
    }

    #endregion
}
