using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Reepax.ViewModels;

namespace Reepax.Views;

/// <summary>
/// Interaction logic for QuickSettingsPopupView.xaml
/// </summary>
public partial class QuickSettingsPopupView : UserControl
{
    private long _closedTimestamp;
    private string _lastQuickSpeedLimit = "";
    private string _lastQuickMaxDownloads = "";
    private string _lastQuickConnections = "";

    public static readonly DependencyProperty IsOpenProperty =
        DependencyProperty.Register(
            nameof(IsOpen),
            typeof(bool),
            typeof(QuickSettingsPopupView),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) =>
            {
                if (d is QuickSettingsPopupView view && view.PopupRoot != null)
                {
                    if ((bool)e.NewValue)
                    {
                        view.SyncDataContext(view.DataContext);
                    }
                    view.PopupRoot.IsOpen = (bool)e.NewValue;
                }
            }));

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public static readonly DependencyProperty PlacementTargetProperty =
        DependencyProperty.Register(
            nameof(PlacementTarget),
            typeof(UIElement),
            typeof(QuickSettingsPopupView),
            new PropertyMetadata(null, (d, e) =>
            {
                if (d is QuickSettingsPopupView view && view.PopupRoot != null)
                {
                    view.PopupRoot.PlacementTarget = e.NewValue as UIElement;
                }
            }));

    public UIElement? PlacementTarget
    {
        get => (UIElement?)GetValue(PlacementTargetProperty);
        set => SetValue(PlacementTargetProperty, value);
    }

    public event EventHandler? OpenAllSettingsRequested;

    private MainViewModel? ViewModel => (DataContext as MainViewModel) ?? (PopupContent?.DataContext as MainViewModel);

    public QuickSettingsPopupView()
    {
        InitializeComponent();

        DataContextChanged += (s, e) =>
        {
            SyncDataContext(e.NewValue);
        };

        Loaded += (s, e) =>
        {
            SyncDataContext(DataContext);
        };

        if (PopupRoot != null)
        {
            PopupRoot.Opened += (s, e) =>
            {
                SyncDataContext(DataContext);
            };
        }
    }

    private void SyncDataContext(object? newDc)
    {
        var dc = newDc ?? DataContext ?? (PlacementTarget as FrameworkElement)?.DataContext;
        if (PopupRoot != null && PopupRoot.DataContext != dc)
        {
            PopupRoot.DataContext = dc;
        }
        if (PopupContent != null && PopupContent.DataContext != dc)
        {
            PopupContent.DataContext = dc;
        }
    }

    public void Toggle(FrameworkElement? anchor = null)
    {
        if (Environment.TickCount64 - _closedTimestamp < 350)
        {
            return;
        }

        var target = anchor ?? PlacementTarget as FrameworkElement;
        if (target != null)
        {
            PopupRoot.PlacementTarget = target;
            if (target.ActualWidth > 0)
            {
                PopupRoot.HorizontalOffset = target.ActualWidth - 352;
            }
            else
            {
                PopupRoot.HorizontalOffset = -304;
            }
        }
        else
        {
            PopupRoot.HorizontalOffset = -304;
        }

        PopupRoot.VerticalOffset = -13;
        SyncDataContext(target?.DataContext ?? DataContext);
        IsOpen = !IsOpen;
    }

    public void HandlePreviewMouseDown(MouseButtonEventArgs e)
    {
        if (IsOpen || (Environment.TickCount64 - _closedTimestamp < 350))
        {
            IsOpen = false;
            _closedTimestamp = Environment.TickCount64;
            e.Handled = true;
        }
    }

    private void PopupRoot_Closed(object? sender, EventArgs e)
    {
        _closedTimestamp = Environment.TickCount64;
        IsOpen = false;
        ResetQuickSettingsNumericAnimations();
    }

    private void OpenAllSettings_Click(object sender, RoutedEventArgs e)
    {
        IsOpen = false;
        OpenAllSettingsRequested?.Invoke(this, EventArgs.Empty);
        ViewModel?.SwitchToSettingsTab();
    }

    #region MouseWheel Handlers

    private void MaxDownloads_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0)
        {
            ViewModel?.IncrementMaxDownloads();
        }
        else if (e.Delta < 0)
        {
            ViewModel?.DecrementMaxDownloads();
        }
        e.Handled = true;
    }

    private void Connections_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0)
        {
            ViewModel?.IncrementConnections();
        }
        else if (e.Delta < 0)
        {
            ViewModel?.DecrementConnections();
        }
        e.Handled = true;
    }

    private void SpeedLimit_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0)
        {
            ViewModel?.IncrementSpeedLimit();
        }
        else if (e.Delta < 0)
        {
            ViewModel?.DecrementSpeedLimit();
        }
        e.Handled = true;
    }

    #endregion

    #region Numeric Roll Animations

    private void QuickSpeedLimitBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string newText = QuickSpeedLimitBox?.Text ?? "";
        if (!string.IsNullOrEmpty(_lastQuickSpeedLimit) && _lastQuickSpeedLimit != newText &&
            QuickSpeedLimitGhostText != null && QuickSpeedLimitTranslate != null && QuickSpeedLimitGhostTranslate != null)
        {
            AnimateNumericChange(QuickSpeedLimitBox!, QuickSpeedLimitGhostText, QuickSpeedLimitTranslate, QuickSpeedLimitGhostTranslate, _lastQuickSpeedLimit, newText);
        }
        _lastQuickSpeedLimit = newText;
    }

    private void QuickMaxDownloadsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string newText = QuickMaxDownloadsBox?.Text ?? "";
        if (!string.IsNullOrEmpty(_lastQuickMaxDownloads) && _lastQuickMaxDownloads != newText &&
            QuickMaxDownloadsGhostText != null && QuickMaxDownloadsTranslate != null && QuickMaxDownloadsGhostTranslate != null)
        {
            AnimateNumericChange(QuickMaxDownloadsBox!, QuickMaxDownloadsGhostText, QuickMaxDownloadsTranslate, QuickMaxDownloadsGhostTranslate, _lastQuickMaxDownloads, newText);
        }
        _lastQuickMaxDownloads = newText;
    }

    private void QuickConnectionsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string newText = QuickConnectionsBox?.Text ?? "";
        if (!string.IsNullOrEmpty(_lastQuickConnections) && _lastQuickConnections != newText &&
            QuickConnectionsGhostText != null && QuickConnectionsTranslate != null && QuickConnectionsGhostTranslate != null)
        {
            AnimateNumericChange(QuickConnectionsBox!, QuickConnectionsGhostText, QuickConnectionsTranslate, QuickConnectionsGhostTranslate, _lastQuickConnections, newText);
        }
        _lastQuickConnections = newText;
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
        double offset = 12.0;
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

    private void ResetQuickSettingsNumericAnimations()
    {
        if (QuickSpeedLimitTranslate != null)
        {
            QuickSpeedLimitTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            QuickSpeedLimitTranslate.Y = 0;
        }
        if (QuickSpeedLimitBox != null)
        {
            QuickSpeedLimitBox.BeginAnimation(UIElement.OpacityProperty, null);
            QuickSpeedLimitBox.Opacity = 1.0;
        }
        if (QuickSpeedLimitGhostText != null)
        {
            QuickSpeedLimitGhostText.Opacity = 0;
        }

        if (QuickMaxDownloadsTranslate != null)
        {
            QuickMaxDownloadsTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            QuickMaxDownloadsTranslate.Y = 0;
        }
        if (QuickMaxDownloadsBox != null)
        {
            QuickMaxDownloadsBox.BeginAnimation(UIElement.OpacityProperty, null);
            QuickMaxDownloadsBox.Opacity = 1.0;
        }
        if (QuickMaxDownloadsGhostText != null)
        {
            QuickMaxDownloadsGhostText.Opacity = 0;
        }

        if (QuickConnectionsTranslate != null)
        {
            QuickConnectionsTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            QuickConnectionsTranslate.Y = 0;
        }
        if (QuickConnectionsBox != null)
        {
            QuickConnectionsBox.BeginAnimation(UIElement.OpacityProperty, null);
            QuickConnectionsBox.Opacity = 1.0;
        }
        if (QuickConnectionsGhostText != null)
        {
            QuickConnectionsGhostText.Opacity = 0;
        }
    }

    #endregion
}
