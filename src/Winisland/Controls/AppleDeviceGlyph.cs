using System;
using System.Windows;
using System.Windows.Media;
using Winisland.Services;

namespace Winisland.Controls;

// Original product silhouettes, drawn in DIPs with a quiet ceramic/metal finish.
public sealed class AppleDeviceGlyph : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(AppleDeviceKind), typeof(AppleDeviceGlyph),
        new FrameworkPropertyMetadata(AppleDeviceKind.AirPodsPro, FrameworkPropertyMetadataOptions.AffectsRender));
    public AppleDeviceKind Kind { get => (AppleDeviceKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    private static readonly Brush Ceramic = Finish("#FFFFFF", "#C8CDD3");
    private static readonly Brush Metal = Finish("#F0F1F4", "#969CA5");
    private static readonly Brush Detail = Solid("#69717B");
    private static readonly Brush Vent = Solid("#282C31");
    private static readonly Brush Screen = Finish("#272D38", "#101218");
    private static readonly Geometry Headband = Shape("M15,36 V25 C15,-2 65,-2 65,25 V36");
    private static readonly Geometry Mesh = Shape("M21,20 C24,4 56,4 59,20");

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var scale = Math.Min(ActualWidth / 80, ActualHeight / 64);
        dc.PushTransform(new MatrixTransform(new Matrix(scale, 0, 0, scale, (ActualWidth - 80 * scale) / 2, (ActualHeight - 64 * scale) / 2)));
        switch (Kind)
        {
            case AppleDeviceKind.AirPods: DrawPods(dc, false); break;
            case AppleDeviceKind.AirPodsPro: DrawPods(dc, true); break;
            case AppleDeviceKind.AirPodsMax: DrawMax(dc); break;
            case AppleDeviceKind.Keyboard: DrawKeyboard(dc); break;
            case AppleDeviceKind.Mouse:
                dc.DrawRoundedRectangle(Ceramic, null, new Rect(23, 7, 34, 50), 17, 17);
                dc.DrawLine(new Pen(Solid("#B8BDC4"), .8), new Point(40, 8), new Point(40, 25)); break;
            case AppleDeviceKind.Trackpad:
                dc.DrawRoundedRectangle(Metal, null, new Rect(6, 13, 68, 42), 6, 6);
                dc.DrawRoundedRectangle(Ceramic, null, new Rect(7, 12, 66, 41), 5, 5); break;
            case AppleDeviceKind.iPhone: DrawPhone(dc, false); break;
            case AppleDeviceKind.iPad: DrawPhone(dc, true); break;
            default:
                dc.DrawRoundedRectangle(Ceramic, null, new Rect(14, 17, 52, 32), 10, 10);
                dc.DrawEllipse(Detail, null, new Point(40, 33), 2, 2); break;
        }
        dc.Pop();
    }

    private static void DrawPods(DrawingContext dc, bool pro)
    {
        for (var side = 0; side < 2; side++)
        {
            dc.PushTransform(new TranslateTransform(side == 0 ? 7 : 46, side == 0 ? 5 : 9));
            dc.PushTransform(new RotateTransform(side == 0 ? -13 : 13, 13, 24));
            var stemHeight = pro ? 30 : 38;
            dc.DrawRoundedRectangle(Ceramic, null, new Rect(side == 0 ? 17 : 6, 15, 8, stemHeight), 4, 4);
            dc.DrawEllipse(Ceramic, null, new Point(15, 14), pro ? 12 : 11, 10);
            if (pro)
            {
                dc.DrawRoundedRectangle(Ceramic, null, new Rect(side == 0 ? 1 : 21, 10, 8, 11), 4, 4);
                dc.DrawEllipse(Detail, null, new Point(side == 0 ? 2 : 28, 15.5), 1.3, 3.1);
            }
            dc.DrawEllipse(Vent, null, new Point(side == 0 ? 13 : 17, 11), 3.3, 1.8);
            dc.DrawRoundedRectangle(Detail, null, new Rect(side == 0 ? 19 : 8, 22, 2.6, 6), 1.3, 1.3);
            dc.DrawLine(new Pen(Metal, 1.5), new Point(side == 0 ? 19 : 8, 15 + stemHeight - 3), new Point(side == 0 ? 23 : 12, 15 + stemHeight - 3));
            dc.Pop(); dc.Pop();
        }
    }

    private static void DrawMax(DrawingContext dc)
    {
        dc.DrawGeometry(null, new Pen(Metal, 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Headband);
        dc.DrawGeometry(null, new Pen(Detail, 5), Mesh);
        dc.DrawRoundedRectangle(Detail, null, new Rect(12, 30, 17, 29), 8, 8);
        dc.DrawRoundedRectangle(Detail, null, new Rect(51, 30, 17, 29), 8, 8);
        dc.DrawRoundedRectangle(Metal, null, new Rect(9, 28, 16, 30), 7, 7);
        dc.DrawRoundedRectangle(Metal, null, new Rect(55, 28, 16, 30), 7, 7);
        dc.DrawRoundedRectangle(Metal, null, new Rect(62, 25, 4, 4), 1, 1);
    }

    private static void DrawKeyboard(DrawingContext dc)
    {
        dc.DrawRoundedRectangle(Metal, null, new Rect(2, 17, 76, 34), 5, 5);
        dc.DrawRoundedRectangle(Ceramic, null, new Rect(3, 15, 74, 34), 4, 4);
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 10; column++)
                dc.DrawRoundedRectangle(Metal, null, new Rect(7 + column * 6.6, 20 + row * 7, 5, 5), 1, 1);
        dc.DrawRoundedRectangle(Metal, null, new Rect(24, 41, 32, 4), 1, 1);
    }

    private static void DrawPhone(DrawingContext dc, bool tablet)
    {
        var width = tablet ? 47 : 29;
        var left = (80 - width) / 2.0;
        dc.DrawRoundedRectangle(Metal, null, new Rect(left, 3, width, 58), 6, 6);
        dc.DrawRoundedRectangle(Screen, null, new Rect(left + 2, 5, width - 4, 54), 4.5, 4.5);
        dc.DrawRoundedRectangle(Vent, null, new Rect(35, 7, 10, 2.5), 1.2, 1.2);
        dc.DrawRoundedRectangle(Detail, null, new Rect(34, 55, 12, 1.2), .6, .6);
    }

    private static Geometry Shape(string path) { var value = Geometry.Parse(path); value.Freeze(); return value; }
    private static Brush Solid(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
    private static Brush Finish(string top, string bottom)
    {
        var brush = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(top), (Color)ColorConverter.ConvertFromString(bottom), new Point(0, 0), new Point(.85, 1));
        brush.Freeze(); return brush;
    }
}
