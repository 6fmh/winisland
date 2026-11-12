using System;
using System.Windows;
using System.Windows.Media;

namespace Winisland.Controls;

// Image-fitted MacBook contour: inward top shoulders meet the display edge,
// while quadratic lower corners curl into the flat bottom. Units are WPF DIPs.
public sealed class NotchSurface : FrameworkElement
{
    public static readonly DependencyProperty ExpansionProperty = DependencyProperty.Register(
        nameof(Expansion), typeof(double), typeof(NotchSurface),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender,
            OnContourChanged, CoerceExpansion));

    public static readonly DependencyProperty IsDynamicIslandProperty = DependencyProperty.Register(
        nameof(IsDynamicIsland), typeof(bool), typeof(NotchSurface),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender,
            OnContourChanged));

    // Corner motion is independent of height: a taller custom notch or volume
    // HUD must not acquire the expanded panel's shoulders by accident.
    public double Expansion
    {
        get => (double)GetValue(ExpansionProperty);
        set => SetValue(ExpansionProperty, value);
    }

    public bool IsDynamicIsland
    {
        get => (bool)GetValue(IsDynamicIslandProperty);
        set => SetValue(IsDynamicIslandProperty, value);
    }

    public event EventHandler? ContourChanged;

    private static object CoerceExpansion(DependencyObject sender, object value) =>
        double.IsFinite((double)value) ? Math.Clamp((double)value, 0, 1) : 0.0;

    private static void OnContourChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((NotchSurface)sender).ContourChanged?.Invoke(sender, EventArgs.Empty);

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        // Both Apple silhouettes use opaque black without a decorative stroke.
        // The owning window supplies the detached island's screen-top inset.
        context.DrawGeometry(Brushes.Black, null, CreateGeometry(ActualWidth, ActualHeight, Expansion, IsDynamicIsland));
    }

    public static StreamGeometry CreateGeometry(double width, double height, double expansion = 0, bool dynamicIsland = false)
    {
        var geometry = new StreamGeometry();
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
        {
            geometry.Freeze();
            return geometry;
        }

        expansion = double.IsFinite(expansion) ? Math.Clamp(expansion, 0, 1) : 0;
        if (dynamicIsland) return CreateIslandGeometry(geometry, width, height, expansion);
        var shoulder = Math.Min(NotchMetrics.ClosedShoulder
            + (NotchMetrics.OpenShoulder - NotchMetrics.ClosedShoulder) * expansion,
            Math.Min(height, width / 4));
        var radius = Math.Min(NotchMetrics.ClosedLowerRadius
            + (NotchMetrics.OpenLowerRadius - NotchMetrics.ClosedLowerRadius) * expansion,
            Math.Min(height - shoulder, width / 2 - shoulder));

        // Quadratic controls fit the official product image better than circular
        // arcs at the lower corners. Retain fractional positions
        // during a spring and let WPF antialias curves at the current display DPI.
        using (var path = geometry.Open())
        {
            path.BeginFigure(new Point(0, 0), true, true);
            path.QuadraticBezierTo(new Point(shoulder, 0), new Point(shoulder, shoulder), true, false);
            path.LineTo(new Point(shoulder, height - radius), true, false);
            path.QuadraticBezierTo(new Point(shoulder, height), new Point(shoulder + radius, height), true, false);
            path.LineTo(new Point(width - shoulder - radius, height), true, false);
            path.QuadraticBezierTo(new Point(width - shoulder, height), new Point(width - shoulder, height - radius), true, false);
            path.LineTo(new Point(width - shoulder, shoulder), true, false);
            path.QuadraticBezierTo(new Point(width - shoulder, 0), new Point(width, 0), true, false);
            path.LineTo(new Point(0, 0), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static StreamGeometry CreateIslandGeometry(StreamGeometry geometry, double width, double height, double expansion)
    {
        var radius = Math.Min(Math.Min(width, height) / 2,
            height / 2 + (DynamicIslandMetrics.ExpandedRadius - height / 2) * expansion);

        // An idle/compact island has semicircular ends. The expanded contour
        // eases toward continuous corners with zero curvature at each straight
        // join. Two cubic segments per corner keep that transition smooth at
        // every DPI, without polygon edges or a square top attachment.
        var firstControl = Lerp(0.7347835102, 0.62, expansion);
        var secondControlX = Lerp(0.4804295973, 0.3182071695, expansion);
        var secondControlY = Lerp(0.1053568404, 0, expansion);
        var midpoint = Lerp(0.2928932188, 0.1591035847, expansion);
        using (var path = geometry.Open())
        {
            path.BeginFigure(new Point(radius, 0), true, true);
            path.LineTo(new Point(width - radius, 0), true, false);
            AddIslandCorner(path, width, height, radius, 0, firstControl, secondControlX, secondControlY, midpoint);
            path.LineTo(new Point(width, height - radius), true, false);
            AddIslandCorner(path, width, height, radius, 1, firstControl, secondControlX, secondControlY, midpoint);
            path.LineTo(new Point(radius, height), true, false);
            AddIslandCorner(path, width, height, radius, 2, firstControl, secondControlX, secondControlY, midpoint);
            path.LineTo(new Point(0, radius), true, false);
            AddIslandCorner(path, width, height, radius, 3, firstControl, secondControlX, secondControlY, midpoint);
        }
        geometry.Freeze();
        return geometry;
    }

    private static void AddIslandCorner(StreamGeometryContext path, double width, double height,
        double radius, int corner, double first, double secondX, double secondY, double midpoint)
    {
        Point Map(double x, double y) => corner switch
        {
            0 => new Point(width - x * radius, y * radius),
            1 => new Point(width - y * radius, height - x * radius),
            2 => new Point(x * radius, height - y * radius),
            _ => new Point(y * radius, x * radius)
        };
        path.BezierTo(Map(first, 0), Map(secondX, secondY), Map(midpoint, midpoint), true, false);
        path.BezierTo(Map(secondY, secondX), Map(0, first), Map(0, 1), true, false);
    }

    private static double Lerp(double start, double end, double amount) => start + (end - start) * amount;
}
