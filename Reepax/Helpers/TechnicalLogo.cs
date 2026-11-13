using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Reepax.Helpers;

public sealed class TechnicalLogo : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(TechnicalLogo),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(TechnicalLogo), new PropertyMetadata(false, ActiveChanged));
    public static readonly DependencyProperty EnableAnimationProperty = DependencyProperty.Register(
        nameof(EnableAnimation), typeof(bool), typeof(TechnicalLogo), new PropertyMetadata(true, MotionChanged));
    private static readonly Brush ScanBrush = CreateScanBrush();
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _shineTimer;
    private readonly Func<TimeSpan> _createShineDelay;
    private readonly Stopwatch _clock = new();
    private readonly LogoOutlineArt _art = LogoOutlineArt.Default;
    private Window? _window;
    private bool _observingMotion;
    private double _lastTick;
    private double _intro = 1;
    private double _shineElapsed = -1;
    private Size _artSize;
    private Rect _imageBounds;
    private Geometry _outline = Geometry.Empty;
    private RectangleGeometry[] _introBands = Array.Empty<RectangleGeometry>();
    internal bool IsAnimating => _timer.IsEnabled;
    internal double IntroProgress => _intro;
    internal double ShineProgress => _shineElapsed < 0 ? 0 : Math.Clamp(_shineElapsed / 0.85, 0, 1);
    internal bool IsShineScheduled => _shineTimer.IsEnabled;
    internal TimeSpan NextShineDelay => _shineTimer.Interval;
    internal int ShineStarts { get; private set; }
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public bool EnableAnimation { get => (bool)GetValue(EnableAnimationProperty); set => SetValue(EnableAnimationProperty, value); }

    public TechnicalLogo() : this(CreateShineDelay) { }

    internal TechnicalLogo(Func<TimeSpan> createShineDelay)
    {
        _createShineDelay = createShineDelay;
        SetResourceReference(ForegroundProperty, "BackdropLogoBrush");
        Focusable = false;
        IsHitTestVisible = false;
        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(1000.0 / 60) };
        _timer.Tick += Tick;
        _shineTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
        _shineTimer.Tick += (_, _) => { _shineTimer.Stop(); BeginShine(); };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => UpdateActivity();
        IsEnabledChanged += (_, _) => UpdateActivity();
    }

    private static void ActiveChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var logo = (TechnicalLogo)sender;
        if ((bool)args.NewValue)
        {
            logo._intro = 0;
            logo._shineElapsed = -1;
        }
        logo.UpdateActivity();
        logo.InvalidateVisual();
    }

    private static void MotionChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((TechnicalLogo)sender).UpdateActivity();

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
        _shineTimer.Stop();
        _clock.Stop();
        _shineElapsed = -1;
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

    internal static TimeSpan CreateShineDelay() => TimeSpan.FromMilliseconds(Random.Shared.Next(7000, 16001));

    private bool CanAnimate => EnableAnimation && SystemParameters.ClientAreaAnimation && IsLoaded && IsVisible &&
        IsEnabled && IsActive && _window?.WindowState != WindowState.Minimized;

    internal void BeginShine()
    {
        if (!CanAnimate || _intro < 1 || _shineElapsed >= 0) return;
        _shineElapsed = 0;
        ShineStarts++;
        _shineTimer.Stop();
        UpdateActivity();
    }

    private void UpdateActivity()
    {
        bool motion = EnableAnimation && SystemParameters.ClientAreaAnimation;
        if (!motion)
        {
            _intro = 1;
            _shineElapsed = -1;
            InvalidateVisual();
        }
        bool run = CanAnimate && (_intro < 1 || _shineElapsed >= 0);
        if (CanAnimate && _intro >= 1 && _shineElapsed < 0)
        {
            if (!_shineTimer.IsEnabled)
            {
                _shineTimer.Interval = _createShineDelay();
                _shineTimer.Start();
            }
        }
        else _shineTimer.Stop();
        if (run && !_timer.IsEnabled)
        {
            _lastTick = _clock.Elapsed.TotalSeconds;
            _clock.Start();
            _timer.Start();
        }
        else if (!run)
        {
            _timer.Stop();
            _clock.Stop();
        }
    }

    private void Tick(object? sender, EventArgs args)
    {
        double elapsed = _clock.Elapsed.TotalSeconds;
        double delta = Math.Clamp(elapsed - _lastTick, 0, 0.05);
        _lastTick = elapsed;
        _intro = Math.Min(1, _intro + delta);
        if (_shineElapsed >= 0)
        {
            _shineElapsed += delta;
            if (_shineElapsed >= 0.85) _shineElapsed = -1;
        }
        InvalidateVisual();
        UpdateActivity();
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        DrawLogo(context, RenderSize, _intro, ShineProgress);
    }

    internal void DrawLogo(DrawingContext context, Size size, double intro, double shine = 0)
    {
        if (size.Width <= 60 || size.Height <= 60) return;
        ArrangeArt(size);
        const double opacity = 0.28;
        if (intro >= 1)
        {
            context.PushOpacity(opacity);
            DrawThemedArt(context);
            context.Pop();
        }
        else
        {
            // Stagger fixed scan bands without moving or distorting the logo.
            for (int band = 0; band < _introBands.Length; band++)
            {
                double delay = band * 0.025 + (band * 7 % 5) * 0.025;
                double reveal = Smooth(delay, delay + 0.42, intro);
                if (reveal <= 0) continue;
                context.PushOpacity(opacity * reveal);
                context.PushClip(_introBands[band]);
                DrawThemedArt(context);
                context.Pop();
                context.Pop();
            }
        }
        double settled = Smooth(0.65, 1, intro);
        if (settled <= 0) return;
        double scan = Math.Sin(Math.PI * shine) * settled;
        if (scan < 0.001) return;
        context.PushClip(_outline);
        context.PushOpacity(scan * 0.18);
        double height = _imageBounds.Height * 0.24;
        double travel = _imageBounds.Height + height + _imageBounds.Width * 0.22;
        double top = _imageBounds.Top - height - _imageBounds.Width * 0.11 + travel * shine;
        context.PushTransform(new SkewTransform(0, -12, _imageBounds.X + _imageBounds.Width / 2, 0));
        context.DrawRectangle(ScanBrush, null, new Rect(_imageBounds.X, top, _imageBounds.Width, height));
        context.Pop();
        context.Pop();
        context.Pop();
    }

    private void DrawThemedArt(DrawingContext context)
    {
        if (Foreground is SolidColorBrush brush && brush.Color == Colors.White)
            context.DrawImage(_art.Image, _imageBounds);
        else
            context.DrawGeometry(Foreground, null, _outline);
    }

    private void ArrangeArt(Size size)
    {
        if (_artSize == size) return;
        _artSize = size;
        double scale = Math.Min((size.Width - 60) / _art.Image.PixelWidth, (size.Height - 60) / _art.Image.PixelHeight);
        _imageBounds = new Rect((size.Width - _art.Image.PixelWidth * scale) / 2,
            (size.Height - _art.Image.PixelHeight * scale) / 2, _art.Image.PixelWidth * scale, _art.Image.PixelHeight * scale);
        _outline = _art.Outline.CloneCurrentValue();
        _outline.Transform = new MatrixTransform(scale, 0, 0, scale, _imageBounds.X, _imageBounds.Y);
        _outline.Freeze();
        _introBands = new RectangleGeometry[16];
        for (int band = 0; band < _introBands.Length; band++)
        {
            double top = _imageBounds.Top + _imageBounds.Height * band / _introBands.Length;
            double bottom = _imageBounds.Top + _imageBounds.Height * (band + 1) / _introBands.Length;
            _introBands[band] = new RectangleGeometry(new Rect(_imageBounds.Left, top, _imageBounds.Width, bottom - top));
            _introBands[band].Freeze();
        }
    }

    private static Brush CreateScanBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
            GradientStops = new GradientStopCollection
            {
                new(Colors.Transparent, 0), new(Colors.White, 0.5), new(Colors.Transparent, 1)
            }
        };
        brush.Freeze();
        return brush;
    }

    private static double Smooth(double from, double to, double value)
    {
        double progress = Math.Clamp((value - from) / (to - from), 0, 1);
        return progress * progress * (3 - 2 * progress);
    }
}
