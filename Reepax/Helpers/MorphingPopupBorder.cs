using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Reepax.Helpers;

public sealed class MorphingPopupBorder : Border
{
    internal static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(MorphingPopupBorder),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender, ProgressChanged));

    internal double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    internal Rect Origin { get; set; }
    internal bool OpensUpward { get; set; }
    private Geometry? _previousClip;
    private double _previousOpacity = 1;
    private CacheMode? _previousCache;
    private Effect? _previousEffect;
    private readonly RectangleGeometry _reveal = new();
    internal bool IsMorphing { get; private set; }

    internal void BeginMorph()
    {
        EndMorph();
        IsMorphing = true;
        _previousEffect = Effect;
        // Avoid rerasterizing the entire shadow and text for every contour change.
        SetCurrentValue(EffectProperty, null);
        if (Child is UIElement content)
        {
            _previousClip = content.Clip;
            _previousOpacity = content.Opacity;
            _previousCache = content.CacheMode;
            content.SetCurrentValue(CacheModeProperty, new BitmapCache());
            content.SetCurrentValue(ClipProperty, _previousClip == null ? _reveal :
                new CombinedGeometry(GeometryCombineMode.Intersect, _previousClip, _reveal));
        }
        Progress = 0;
        InvalidateVisual();
    }

    internal void EndMorph()
    {
        BeginAnimation(ProgressProperty, null);
        if (IsMorphing)
        {
            IsMorphing = false;
            SetCurrentValue(EffectProperty, _previousEffect);
            if (Child is UIElement content)
            {
                content.SetCurrentValue(ClipProperty, _previousClip);
                content.SetCurrentValue(OpacityProperty, _previousOpacity);
                content.SetCurrentValue(CacheModeProperty, _previousCache);
            }
        }
        Progress = 1;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (!IsMorphing)
        {
            base.OnRender(drawingContext);
            return;
        }
        Geometry geometry = CreateOutline(RenderSize, Origin, Progress, CornerRadius, OpensUpward);
        double thickness = Math.Max(BorderThickness.Left, BorderThickness.Top);
        drawingContext.DrawGeometry(Background, thickness > 0 && BorderBrush != null ? new Pen(BorderBrush, thickness) : null, geometry);
    }

    private static void ProgressChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((MorphingPopupBorder)sender).UpdateContent();

    internal void UpdateContent()
    {
        if (!IsMorphing || Child is not UIElement content) return;
        // Reveal full-size text; never scale fonts or relayout the options during the morph.
        double visible = Math.Clamp((Progress - 0.75) / 0.25, 0, 1);
        content.SetCurrentValue(OpacityProperty, _previousOpacity * visible * visible * (3 - 2 * visible));
        double p = Math.Clamp(Progress, 0, 1);
        double originHeight = Origin.IsEmpty ? 0 : Origin.Height;
        double height = Math.Max(0, originHeight + (RenderSize.Height - originHeight) * p);
        double width = Math.Max(0, Origin.Width + (RenderSize.Width - Origin.Width) * p);
        var offset = VisualTreeHelper.GetOffset(content);
        _reveal.Rect = new Rect(Origin.X * (1 - p) - offset.X,
            (OpensUpward ? RenderSize.Height - height : 0) - offset.Y, width, height);
    }

    internal static Geometry CreateOutline(Size size, Rect origin, double progress, CornerRadius corner, bool upward)
    {
        double p = Math.Clamp(progress, 0, 1.035);
        if (origin.IsEmpty || origin.Width <= 0) origin = new Rect(0, 0, Math.Max(1, size.Width), 1);
        double x = origin.X * (1 - Math.Min(p, 1));
        double width = Math.Max(1, origin.Width + (size.Width - origin.Width) * p);
        double height = Math.Max(1, origin.Height + (size.Height - origin.Height) * p);
        double y = origin.Y + (0 - origin.Y) * Math.Min(p, 1);
        if (upward && p > 1) y = size.Height - height;
        double radius = Math.Min(Math.Min(width, height) / 2,
            corner.TopLeft + Math.Max(0, origin.Height / 2 - corner.TopLeft) * Math.Pow(1 - Math.Min(p, 1), 2));
        double neck = Math.Sin(Math.Min(p, 1) * Math.PI) * Math.Min(10, width * 0.06);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            Point P(double px, double py) => new(x + px, y + (upward ? height - py : py));
            context.BeginFigure(P(radius, 0), true, true);
            context.LineTo(P(width - radius, 0), true, false);
            context.QuadraticBezierTo(P(width, 0), P(width, radius), true, false);
            context.BezierTo(P(width - neck, height * 0.25), P(width, height * 0.48), P(width, height - radius), true, false);
            context.QuadraticBezierTo(P(width, height), P(width - radius, height), true, false);
            context.LineTo(P(radius, height), true, false);
            context.QuadraticBezierTo(P(0, height), P(0, height - radius), true, false);
            context.BezierTo(P(0, height * 0.48), P(neck, height * 0.25), P(0, radius), true, false);
            context.QuadraticBezierTo(P(0, 0), P(radius, 0), true, false);
        }
        return geometry;
    }
}
