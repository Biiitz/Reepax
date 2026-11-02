using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Reepax.Helpers;

public sealed class PixelLogoLoader : FrameworkElement
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(PixelLogoLoader), new PropertyMetadata(false, ActivityChanged));
    public static readonly DependencyProperty EnableAnimationProperty = DependencyProperty.Register(
        nameof(EnableAnimation), typeof(bool), typeof(PixelLogoLoader), new PropertyMetadata(true, MotionChanged));
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(PixelLogoLoader),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    private const int Columns = 5;
    private const int Rows = 7;
    private static readonly Lazy<Point[]> Pixels = new(CreatePixels);
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private Window? _window;
    private bool _observingMotion;

    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public bool EnableAnimation { get => (bool)GetValue(EnableAnimationProperty); set => SetValue(EnableAnimationProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    internal bool IsAnimating => _timer.IsEnabled;
    internal int PixelCount => Pixels.Value.Length;

    public PixelLogoLoader()
    {
        IsHitTestVisible = false;
        Focusable = false;
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(1000.0 / 30) };
        _timer.Tick += (_, _) => InvalidateVisual();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => UpdateActivity();
        IsEnabledChanged += (_, _) => UpdateActivity();
    }

    private static Point[] CreatePixels()
    {
        var outline = LogoOutlineArt.Default.Outline;
        var bounds = outline.Bounds;
        var pixels = new List<Point>();
        for (int row = 0; row < Rows; row++)
            for (int column = 0; column < Columns; column++)
                if (outline.FillContains(new Point(bounds.X + (column + 0.5) * bounds.Width / Columns,
                    bounds.Y + (row + 0.5) * bounds.Height / Rows)))
                    pixels.Add(new Point(column, row));
        return pixels.ToArray();
    }

    private static void ActivityChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var loader = (PixelLogoLoader)sender;
        if ((bool)args.NewValue) loader._clock.Restart();
        loader.UpdateActivity();
    }

    private static void MotionChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((PixelLogoLoader)sender).UpdateActivity();

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _window = Window.GetWindow(this);
        if (_window != null) _window.StateChanged += WindowStateChanged;
        if (!_observingMotion)
        {
            SystemParameters.StaticPropertyChanged += SystemMotionChanged;
            _observingMotion = true;
        }
        UpdateActivity();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _timer.Stop();
        _clock.Stop();
        if (_window != null) _window.StateChanged -= WindowStateChanged;
        _window = null;
        if (_observingMotion) SystemParameters.StaticPropertyChanged -= SystemMotionChanged;
        _observingMotion = false;
    }

    private void WindowStateChanged(object? sender, EventArgs args) => UpdateActivity();

    private void SystemMotionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) UpdateActivity();
    }

    private void UpdateActivity()
    {
        if (IsActive && IsLoaded && IsVisible && IsEnabled && EnableAnimation &&
            SystemParameters.ClientAreaAnimation && _window?.WindowState != WindowState.Minimized)
        {
            _clock.Start();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            _clock.Stop();
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        DrawLogo(context, RenderSize, _clock.Elapsed.TotalSeconds, EnableAnimation && SystemParameters.ClientAreaAnimation);
    }

    internal void DrawLogo(DrawingContext context, Size size, double seconds, bool animate)
    {
        double pitch = Math.Min(size.Width / Columns, size.Height / Rows);
        if (pitch <= 0 || !double.IsFinite(pitch)) return;
        double side = pitch * 0.68;
        double left = (size.Width - pitch * Columns) / 2;
        double top = (size.Height - pitch * Rows) / 2;
        foreach (var pixel in Pixels.Value)
        {
            double phase = (pixel.Y + pixel.X * 0.65) / (Rows + Columns * 0.65);
            double pulse = animate ? Math.Pow((1 + Math.Cos(Math.Tau * (seconds / 1.35 - phase))) / 2, 4) : 1;
            context.PushOpacity(0.18 + 0.82 * pulse);
            context.DrawRectangle(Foreground, null, new Rect(left + (pixel.X + 0.16) * pitch,
                top + (pixel.Y + 0.16) * pitch, side, side));
            context.Pop();
        }
    }
}
