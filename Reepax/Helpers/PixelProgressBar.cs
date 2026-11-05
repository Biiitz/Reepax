using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Reepax.Models;

namespace Reepax.Helpers;

public sealed class PixelProgressBar : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(PixelProgressBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender,
            (owner, _) => ((PixelProgressBar)owner).UpdateDisplayedProgress(),
            (_, value) => value is double progress && double.IsFinite(progress) ? Math.Clamp(progress, 0, 100) : 0.0));
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(DownloadStatus), typeof(PixelProgressBar),
        new FrameworkPropertyMetadata(DownloadStatus.Queued, FrameworkPropertyMetadataOptions.AffectsRender,
            (owner, _) =>
            {
                var bar = (PixelProgressBar)owner;
                bar.SnapToProgress();
                bar.UpdateShimmer();
            }));
    public static readonly DependencyProperty FillProperty = RegisterBrush(nameof(Fill));
    public static readonly DependencyProperty ShimmerFillProperty = RegisterBrush(nameof(ShimmerFill));
    public static readonly DependencyProperty PausedFillProperty = RegisterBrush(nameof(PausedFill));
    public static readonly DependencyProperty FailedFillProperty = RegisterBrush(nameof(FailedFill));
    private static readonly DependencyProperty DisplayedProgressProperty = DependencyProperty.Register(
        "DisplayedProgress", typeof(double), typeof(PixelProgressBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty ShimmerProgressProperty = DependencyProperty.Register(
        "ShimmerProgress", typeof(double), typeof(PixelProgressBar),
        new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    private readonly DispatcherTimer _shimmerTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(6) };
    private DoubleAnimation? _progressAnimation;
    private DoubleAnimation? _shimmerAnimation;
    private bool _resetBeforeRender = true;

    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public DownloadStatus Status { get => (DownloadStatus)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush ShimmerFill { get => (Brush)GetValue(ShimmerFillProperty); set => SetValue(ShimmerFillProperty, value); }
    public Brush PausedFill { get => (Brush)GetValue(PausedFillProperty); set => SetValue(PausedFillProperty, value); }
    public Brush FailedFill { get => (Brush)GetValue(FailedFillProperty); set => SetValue(FailedFillProperty, value); }
    internal bool IsShimmerScheduled => _shimmerTimer.IsEnabled;
    internal double ShimmerProgress => (double)GetValue(ShimmerProgressProperty);

    private static DependencyProperty RegisterBrush(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(PixelProgressBar),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public PixelProgressBar()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        SetResourceReference(FillProperty, "TextSecondaryBrush");
        SetResourceReference(ShimmerFillProperty, "TextPrimaryBrush");
        SetResourceReference(PausedFillProperty, "StatusPausedBrush");
        SetResourceReference(FailedFillProperty, "StatusFailedBrush");
        _shimmerTimer.Tick += (_, _) => BeginShimmer();
        Loaded += (_, _) => ResetPresentation();
        Unloaded += (_, _) => ResetPresentation();
        IsVisibleChanged += (_, _) => ResetPresentation();
        DataContextChanged += (_, _) => ResetPresentation();
    }

    private void ResetPresentation()
    {
        _resetBeforeRender = true;
        SnapToProgress();
        StopShimmer();
        UpdateShimmer();
    }

    private void SnapToProgress()
    {
        _progressAnimation = null;
        BeginAnimation(DisplayedProgressProperty, null);
        SetValue(DisplayedProgressProperty, Progress);
    }

    private void UpdateDisplayedProgress()
    {
        double previous = (double)GetValue(DisplayedProgressProperty);
        SnapToProgress();
        UpdateShimmer();
        // Recycled and hidden rows must adopt their current model without animating stale values.
        if (_resetBeforeRender || !IsLoaded || !IsVisible || Status != DownloadStatus.Downloading || Progress <= previous || Progress >= 100)
            return;
        var animation = new DoubleAnimation(previous, Progress, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Timeline.SetDesiredFrameRate(animation, 30);
        animation.Completed += (_, _) =>
        {
            if (ReferenceEquals(_progressAnimation, animation)) SnapToProgress();
        };
        _progressAnimation = animation;
        BeginAnimation(DisplayedProgressProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private bool CanShimmer => IsLoaded && IsVisible && Status == DownloadStatus.Downloading && Progress is > 0 and < 100;

    private void UpdateShimmer()
    {
        if (!CanShimmer) StopShimmer();
        else if (_shimmerAnimation == null && !_shimmerTimer.IsEnabled) _shimmerTimer.Start();
    }

    private void StopShimmer()
    {
        _shimmerTimer.Stop();
        _shimmerAnimation = null;
        BeginAnimation(ShimmerProgressProperty, null);
    }

    internal void BeginShimmer()
    {
        StopShimmer();
        if (!CanShimmer) return;
        var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(950));
        Timeline.SetDesiredFrameRate(animation, 30);
        animation.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_shimmerAnimation, animation)) return;
            StopShimmer();
            UpdateShimmer();
        };
        _shimmerAnimation = animation;
        BeginAnimation(ShimmerProgressProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        _resetBeforeRender = false;
        var dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Floor(RenderSize.Width * dpi.DpiScaleX);
        int height = (int)Math.Floor(RenderSize.Height * dpi.DpiScaleY);
        if (width < 1 || height < 1) return;
        int gap = Math.Max(1, (int)Math.Round(3 * dpi.DpiScaleY));
        int side = Math.Min(Math.Min(width, height), Math.Max(1, (int)Math.Round(5 * dpi.DpiScaleY)));
        int columns = (width + gap) / (side + gap);
        int rows = (height + gap) / (side + gap);
        int left = (width - (columns * side + (columns - 1) * gap)) / 2;
        int top = (height - (rows * side + (rows - 1) * gap)) / 2;
        double extent = columns * (double)GetValue(DisplayedProgressProperty) / 100;
        int filled = (int)Math.Floor(extent);
        double fraction = extent - filled;
        double shimmerCenter = -0.35 + ShimmerProgress * 1.7;
        Brush foreground = Status switch
        {
            DownloadStatus.Paused => PausedFill,
            DownloadStatus.Failed or DownloadStatus.Aborted => FailedFill,
            _ => Fill
        };
        for (int column = 0; column < filled + (fraction > 0 ? 1 : 0); column++)
        {
            if (column == filled) context.PushOpacity(fraction);
            for (int row = 0; row < rows; row++)
            {
                var pixel = new Rect((left + column * (side + gap)) / dpi.DpiScaleX,
                    (top + row * (side + gap)) / dpi.DpiScaleY, side / dpi.DpiScaleX, side / dpi.DpiScaleY);
                context.DrawRectangle(foreground, null, pixel);
                double position = (column + 0.5) / columns + ((row + 0.5) / rows - 0.5) * 0.12;
                double shimmer = Math.Max(0, 1 - Math.Abs(position - shimmerCenter) / 0.18);
                if (shimmer > 0)
                {
                    context.PushOpacity(shimmer * shimmer);
                    context.DrawRectangle(ShimmerFill, null, pixel);
                    context.Pop();
                }
            }
            if (column == filled) context.Pop();
        }
    }
}
