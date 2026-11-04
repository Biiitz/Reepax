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

        foreach (var button in new[] { GeneralCategoryButton, DownloadsCategoryButton, ShortcutsCategoryButton, AboutCategoryButton })
            button.SizeChanged += (_, _) => UpdateCategoryBranches(animate: false);
        CategoryButtonsContainer.SizeChanged += (_, _) => UpdateCategoryBranches(animate: false);

        if (SpectrumBox != null)
        {
            SpectrumBox.SizeChanged += (_, _) => UpdateSpectrumThumbPosition();
            SpectrumBox.IsVisibleChanged += (_, _) => { if (SpectrumBox.IsVisible) UpdateSpectrumThumbPosition(); };
        }
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void SettingsView_Loaded(object sender, RoutedEventArgs e)
    {
        HookViewModel();
        UpdateCompletionSoundVolumeVisibility(animate: false);
        UpdateErrorSoundVolumeVisibility(animate: false);

        UpdateSpectrumThumbPosition();

        Dispatcher.BeginInvoke(new Action(() => UpdateCategoryBranches(animate: false)),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void SettingsView_Unloaded(object sender, RoutedEventArgs e)
    {
        CloseSettingsDropdowns();
        UnhookViewModel();
    }

    private void SettingsView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        CloseSettingsDropdowns();
        HookViewModel();
        UpdateCompletionSoundVolumeVisibility(animate: false);
        UpdateErrorSoundVolumeVisibility(animate: false);

        UpdateSpectrumThumbPosition();
        Dispatcher.BeginInvoke(new Action(() => UpdateCategoryBranches(animate: false)),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void SettingsView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) CloseSettingsDropdowns();
        if (IsVisible)
        {
            Dispatcher.BeginInvoke(new Action(() => UpdateCategoryBranches(animate: false)),
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
        if (e.PropertyName is nameof(MainViewModel.SelectedThemeId) or nameof(MainViewModel.SelectedLanguage))
            CloseSettingsDropdowns();
        if (e.PropertyName == nameof(MainViewModel.PickerSaturation) ||
            e.PropertyName == nameof(MainViewModel.PickerValue) ||
            e.PropertyName == nameof(MainViewModel.PickerHue) ||
            e.PropertyName == nameof(MainViewModel.CurrentAccentColor))
        {
            UpdateSpectrumThumbPosition();
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
            CloseSettingsDropdowns();
            Dispatcher.BeginInvoke(new Action(() => UpdateCategoryBranches(animate: true)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void UpdateCategoryBranches(bool animate = true)
    {
        if (!IsLoaded) return;
        var buttons = new[] { GeneralCategoryButton, DownloadsCategoryButton, ShortcutsCategoryButton, AboutCategoryButton };
        if (Array.Exists(buttons, button => button.ActualHeight <= 0)) return;
        var centers = Array.ConvertAll(buttons, button =>
            button.TranslatePoint(new Point(0, button.ActualHeight / 2), CategoryBranchRail).Y);
        int selectedIndex = (ViewModel?.SelectedSettingsCategory ?? SettingsCategory.General) switch
        {
            SettingsCategory.DownloadConnections => 1,
            SettingsCategory.Shortcuts => 2,
            SettingsCategory.About => 3,
            _ => 0
        };
        CategoryBranchRail.UpdateBranches(centers, selectedIndex, animate && IsVisible);
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

    private void Category_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        if (element == CategoryGeneral && !element.IsVisible) CloseSettingsDropdowns();

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


    private void UpdateCompletionSoundVolumeVisibility(bool animate = true)
    {
        if (CompletionSoundVolumeContainer == null || CompletionSoundVolumeContent == null || CompletionSoundVolumeTranslate == null)
            return;

        bool isEnabled = ViewModel?.EnableCompletionSound == true;
        AnimateExpander(CompletionSoundVolumeContainer, CompletionSoundVolumeContent, CompletionSoundVolumeTranslate, ref _isCompletionSoundVolumeExpanded, isEnabled, animate, 78.0);
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

    private void CompletionSound_SelectionCommitted(object? sender, EventArgs args) =>
        ViewModel?.SelectAndPreviewCompletionSound(CompletionSoundButton.SelectedValue as string);

    private void ErrorSound_SelectionCommitted(object? sender, EventArgs args) =>
        ViewModel?.SelectAndPreviewErrorSound(ErrorSoundButton.SelectedValue as string);

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

    #region Accent Color Palette

    private void SpectrumBox_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (SpectrumBox == null || ViewModel == null)
            return;

        SpectrumBox.CaptureMouse();
        UpdateSpectrumFromMouse(e.GetPosition(SpectrumBox));
        e.Handled = true;
    }

    private void SpectrumBox_MouseMove(object sender, MouseEventArgs e)
    {
        if (SpectrumBox == null || ViewModel == null || !SpectrumBox.IsMouseCaptured)
            return;

        UpdateSpectrumFromMouse(e.GetPosition(SpectrumBox));
        e.Handled = true;
    }

    private void SpectrumBox_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (SpectrumBox != null && SpectrumBox.IsMouseCaptured)
        {
            SpectrumBox.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void UpdateSpectrumFromMouse(Point pos)
    {
        if (SpectrumBox == null || ViewModel == null)
            return;

        double width = SpectrumBox.ActualWidth;
        double height = SpectrumBox.ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        double s = Math.Clamp(pos.X / width, 0.0, 1.0);
        double v = Math.Clamp(1.0 - (pos.Y / height), 0.0, 1.0);

        ViewModel.UpdateFromHsv(ViewModel.PickerHue, s, v);
        UpdateSpectrumThumbPosition();
    }

    private void UpdateSpectrumThumbPosition()
    {
        if (SpectrumBox == null || SpectrumThumb == null || ViewModel == null)
            return;

        double width = SpectrumBox.ActualWidth;
        double height = SpectrumBox.ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        double s = Math.Clamp(ViewModel.PickerSaturation, 0.0, 1.0);
        double v = Math.Clamp(ViewModel.PickerValue, 0.0, 1.0);

        double thumbWidth = SpectrumThumb.ActualWidth > 0 ? SpectrumThumb.ActualWidth : 14.0;
        double thumbHeight = SpectrumThumb.ActualHeight > 0 ? SpectrumThumb.ActualHeight : 14.0;

        double x = Math.Clamp(s * width - (thumbWidth / 2.0), -thumbWidth / 2.0, width - thumbWidth / 2.0);
        double y = Math.Clamp((1.0 - v) * height - (thumbHeight / 2.0), -thumbHeight / 2.0, height - thumbHeight / 2.0);

        Canvas.SetLeft(SpectrumThumb, x);
        Canvas.SetTop(SpectrumThumb, y);
    }

    private void HexTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (sender is TextBox tb)
            {
                var be = tb.GetBindingExpression(TextBox.TextProperty);
                be?.UpdateSource();
                tb.SelectAll();
            }
            e.Handled = true;
        }
    }

    private void RgbTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (sender is TextBox tb)
            {
                var be = tb.GetBindingExpression(TextBox.TextProperty);
                be?.UpdateSource();
                tb.SelectAll();
            }
            e.Handled = true;
        }
    }

    #endregion
}
