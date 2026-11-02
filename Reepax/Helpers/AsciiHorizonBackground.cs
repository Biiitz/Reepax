using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Reepax.Helpers;

public sealed class AsciiHorizonBackground : FrameworkElement
{
    public static readonly DependencyProperty GlyphColorProperty = DependencyProperty.Register(
        nameof(GlyphColor), typeof(Color), typeof(AsciiHorizonBackground), new FrameworkPropertyMetadata(Colors.White,
            FrameworkPropertyMetadataOptions.AffectsRender, GlyphColorChanged));
    public Color GlyphColor { get => (Color)GetValue(GlyphColorProperty); set => SetValue(GlyphColorProperty, value); }

    private static void GlyphColorChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var element = (AsciiHorizonBackground)sender;
        element._glyphs = new AsciiGlyphAtlas(VisualTreeHelper.GetDpi(element).PixelsPerDip, (Color)args.NewValue);
    }
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(AsciiHorizonBackground), new PropertyMetadata(false, ActivityChanged));
    public static readonly DependencyProperty EnableAnimationProperty = DependencyProperty.Register(
        nameof(EnableAnimation), typeof(bool), typeof(AsciiHorizonBackground), new PropertyMetadata(true, ActivityChanged));
    private static readonly DependencyProperty RevealProgressProperty = DependencyProperty.Register(
        "RevealProgress", typeof(double), typeof(AsciiHorizonBackground),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private readonly Stopwatch _introClock = new();
    private AsciiGlyphAtlas _glyphs;
    private Window? _window;
    private bool _observingMotion;
    private int _revealVersion;
    private bool _introFinished;
    private Size _fieldSize;
    private Cell[] _cells = Array.Empty<Cell>();
    private readonly record struct Cell(Rect Bounds, double X, double Y, double Seed, bool Soft);
    internal int CellCount => _cells.Length;
    internal bool IsAnimating => _timer.IsEnabled;
    internal long AnimationTicks { get; private set; }
    internal double RevealProgress => (double)GetValue(RevealProgressProperty);
    internal double IntroProgress => _introFinished ? 1 : Math.Clamp(_introClock.Elapsed.TotalSeconds, 0, 1);

    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public bool EnableAnimation { get => (bool)GetValue(EnableAnimationProperty); set => SetValue(EnableAnimationProperty, value); }

    public AsciiHorizonBackground()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;
        _glyphs = new AsciiGlyphAtlas(VisualTreeHelper.GetDpi(this).PixelsPerDip);
        SetResourceReference(GlyphColorProperty, "BackdropGlyphColor");
        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(1000.0 / 30) };
        _timer.Tick += (_, _) =>
        {
            if (!_introFinished && _introClock.Elapsed.TotalSeconds >= 1)
            {
                _introFinished = true;
                _introClock.Stop();
            }
            AnimationTicks++;
            InvalidateVisual();
        };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => UpdateActivity();
        IsEnabledChanged += (_, _) => UpdateActivity();
    }

    private static void ActivityChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var element = (AsciiHorizonBackground)sender;
        if (args.Property == IsActiveProperty && (bool)args.NewValue)
        {
            element._introClock.Reset();
            element._introFinished = false;
        }
        element.UpdateReveal(animate: element.IsLoaded && element.IsVisible);
        element.UpdateActivity();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _window = Window.GetWindow(this);
        if (_window != null) _window.StateChanged += WindowStateChanged;
        if (!_observingMotion)
        {
            SystemParameters.StaticPropertyChanged += MotionChanged;
            _observingMotion = true;
        }
        UpdateReveal(animate: false);
        UpdateActivity();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _timer.Stop();
        _clock.Stop();
        _introClock.Stop();
        UpdateReveal(animate: false);
        if (_window != null) _window.StateChanged -= WindowStateChanged;
        _window = null;
        if (_observingMotion) SystemParameters.StaticPropertyChanged -= MotionChanged;
        _observingMotion = false;
    }

    private void WindowStateChanged(object? sender, EventArgs args) => UpdateActivity();
    private void MotionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
        {
            UpdateReveal(animate: false);
            UpdateActivity();
        }
    }

    private void UpdateReveal(bool animate)
    {
        double current = RevealProgress;
        double target = IsActive ? 1 : 0;
        int version = ++_revealVersion;
        bool transition = animate && EnableAnimation && SystemParameters.ClientAreaAnimation && Math.Abs(current - target) >= 0.001;
        BeginAnimation(RevealProgressProperty, null);
        SetValue(RevealProgressProperty, transition ? current : target);
        if (!transition) return;
        // A shared clock drives staggered cell fades, including their cached glow.
        var animation = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(IsActive ? 180 : 200))
        {
            FillBehavior = FillBehavior.HoldEnd
        };
        animation.Completed += (_, _) =>
        {
            if (_revealVersion != version) return;
            SetValue(RevealProgressProperty, target);
            BeginAnimation(RevealProgressProperty, null);
        };
        BeginAnimation(RevealProgressProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void UpdateActivity()
    {
        if ((!EnableAnimation || !SystemParameters.ClientAreaAnimation) && !_introFinished)
        {
            _introFinished = true;
            InvalidateVisual();
        }
        bool animate = IsLoaded && IsVisible && IsActive && IsEnabled && EnableAnimation &&
            SystemParameters.ClientAreaAnimation && _window?.WindowState != WindowState.Minimized;
        if (animate)
        {
            _clock.Start();
            if (!_introFinished) _introClock.Start();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            _clock.Stop();
            _introClock.Stop();
        }
        if (_introFinished) _introClock.Stop();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _glyphs = new AsciiGlyphAtlas(newDpi.PixelsPerDip, GlyphColor);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        DrawField(context, RenderSize, _clock.Elapsed.TotalSeconds, RevealProgress, IntroProgress);
    }

    internal void DrawField(DrawingContext context, Size size, double seconds, double revealProgress = 1, double introProgress = 1)
    {
        if (size.Width <= 0 || size.Height <= 0 || !double.IsFinite(size.Width) || !double.IsFinite(size.Height)) return;
        if (_fieldSize != size) BuildCells(size);
        if (revealProgress <= 0) return;
        introProgress = Math.Clamp(introProgress, 0, 1);
        double time = seconds * 0.55;
        foreach (var cell in _cells)
        {
            double reveal = Smooth(cell.Seed * 0.4, cell.Seed * 0.4 + 0.6, revealProgress);
            if (reveal <= 0) continue;
            double arch = 0.16 + 0.88 * cell.X * cell.X + 0.018 * Math.Sin(time * 0.7 + Math.Abs(cell.X) * 0.8);
            double curtain = Smooth(-0.05, 0.20, arch - cell.Y);
            double frameDistance = Math.Max(Math.Abs(cell.X), 1 - cell.Y);
            double vignette = Smooth(0.30, 1, frameDistance);
            double flow = 0.5 + 0.22 * Math.Sin(cell.X * 4.7 + cell.Y * 5.3 + time * 0.55) +
                0.15 * Math.Sin(cell.X * 9.2 - cell.Y * 3.9 - time * 0.38);
            double light = Math.Clamp((0.10 + vignette * 0.46 + flow * 0.24) * curtain, 0, 1);
            int glyph = Math.Sin(time * 0.6 + cell.Seed * 17 + cell.X * 3) >= 0 ? 1 : 0;
            double opacity = Math.Clamp(light * (0.28 + cell.Seed * 0.5) * 0.71, 0, 0.6) * reveal;
            if (introProgress < 1)
            {
                double intro = Smooth(cell.Seed * 0.3, cell.Seed * 0.3 + 0.55, introProgress);
                double scramble = 1 - Smooth(0.65, 1, intro);
                // Stable cell seeds stagger binary bursts without creating per-character animations.
                uint frame = (uint)Math.Floor(introProgress * (20 + cell.Seed * 12));
                uint noise = unchecked(frame * 374761393u ^ (uint)(cell.Seed * uint.MaxValue));
                noise ^= noise >> 13;
                if (scramble > 0.01) glyph = (int)(noise & 1);
                opacity *= intro * (1 - scramble * (0.15 + (noise & 255) / 255.0 * 0.15));
            }
            if (opacity < 0.006) continue;
            if (light > 0.5)
            {
                context.PushOpacity(opacity * 0.3);
                context.DrawImage(_glyphs.Glow[glyph], cell.Bounds);
                context.Pop();
            }
            context.PushOpacity(opacity * (cell.Soft ? 0.75 : 1));
            context.DrawImage(cell.Soft ? _glyphs.Soft[glyph] : _glyphs.Sharp[glyph], cell.Bounds);
            context.Pop();
        }
    }

    private void BuildCells(Size size)
    {
        _fieldSize = size;
        // Limit draw work on large and high-resolution windows without adding visual children.
        double cellSize = Math.Max(AsciiGlyphAtlas.CellSize, Math.Sqrt(size.Width * size.Height / 3200));
        int columns = Math.Max(1, (int)Math.Ceiling(size.Width / cellSize));
        int rows = Math.Max(1, (int)Math.Ceiling(size.Height / cellSize));
        var cells = new System.Collections.Generic.List<Cell>(columns * rows * 3 / 4);
        double imageSize = cellSize * AsciiGlyphAtlas.ImageSize / AsciiGlyphAtlas.CellSize;
        for (int row = 0; row < rows; row++)
        for (int column = 0; column < columns; column++)
        {
            uint hash = unchecked((uint)(column * 374761393 + row * 668265263 + 1442695041));
            hash = unchecked((hash ^ (hash >> 13)) * 1274126177);
            hash ^= hash >> 16;
            if ((hash & 65535) > 49151) continue;
            double seed = (hash >> 16) / 65535.0;
            double x = (column + 0.5) * cellSize;
            double y = (row + 0.5) * cellSize;
            cells.Add(new(new Rect(x - imageSize / 2, y - imageSize / 2, imageSize, imageSize),
                x / size.Width * 2 - 1, y / size.Height, seed, seed < 0.72));
        }
        _cells = cells.ToArray();
    }

    private static double Smooth(double from, double to, double value)
    {
        double t = Math.Clamp((value - from) / (to - from), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
