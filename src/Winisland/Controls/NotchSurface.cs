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
            OnExpansionChanged, CoerceExpansion));

    // Corner motion is independent of height: a taller custom notch or volume
    // HUD must not acquire the expanded panel's shoulders by accident.
    public double Expansion
    {
        get => (double)GetValue(ExpansionProperty);
        set => SetValue(ExpansionProperty, value);
    }

    public event EventHandler? ContourChanged;

    private static object CoerceExpansion(DependencyObject sender, object value) =>
        double.IsFinite((double)value) ? Math.Clamp((double)value, 0, 1) : 0.0;

    private static void OnExpansionChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((NotchSurface)sender).ContourChanged?.Invoke(sender, EventArgs.Empty);

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        // No stroke, border or top inset: the black fill touches the screen edge.
        context.DrawGeometry(Brushes.Black, null, CreateGeometry(ActualWidth, ActualHeight, Expansion));
    }

    public static StreamGeometry CreateGeometry(double width, double height, double expansion = 0)
    {
        var geometry = new StreamGeometry();
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
        {
            geometry.Freeze();
            return geometry;
        }

        expansion = double.IsFinite(expansion) ? Math.Clamp(expansion, 0, 1) : 0;
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
}
