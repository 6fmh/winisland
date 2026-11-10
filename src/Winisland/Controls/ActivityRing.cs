using System;
using System.Windows;
using System.Windows.Media;

namespace Winisland.Controls;

// A small circular activity indicator for the Windows focus timer.
public sealed class ActivityRing : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(ActivityRing),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(ActivityRing),
        new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    protected override void OnRender(DrawingContext context)
    {
        if (ActualWidth <= 4 || ActualHeight <= 4) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Min(ActualWidth, ActualHeight) / 2 - 2;
        var pen = new Pen(Foreground, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        context.PushOpacity(0.2);
        context.DrawEllipse(null, pen, center, radius, radius);
        context.Pop();
        var progress = double.IsFinite(Progress) ? Math.Clamp(Progress, 0, 100) : 0;
        var start = new Point(center.X, center.Y - radius);
        if (progress >= 99.999) context.DrawEllipse(null, pen, center, radius, radius);
        else if (progress > 0)
        {
            var angle = 2 * Math.PI * progress / 100;
            var end = new Point(center.X + Math.Sin(angle) * radius, center.Y - Math.Cos(angle) * radius);
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                path.BeginFigure(start, false, false);
                path.ArcTo(end, new Size(radius, radius), 0, progress > 50, SweepDirection.Clockwise, true, false);
            }
            geometry.Freeze();
            context.DrawGeometry(null, pen, geometry);
        }
        else context.DrawEllipse(Foreground, null, start, 1.5, 1.5);
    }
}
