using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Winisland.Controls;

public enum NotchIconKind
{
    Music, Folder, Timer, Pin, Settings, Speaker, MutedSpeaker, Upload,
    File, Copy, Close, Reset, Check, Play, Pause, Previous, Next, Battery, ChargingBattery, Plus,
    Calendar, Grid, Note, Clock, CheckCircle, ChevronLeft, ChevronRight, Globe, Bell, Search, External,
    Controls, Wifi, Bluetooth, Airplane, Sun, Moon, Display, Capture, Power, Download
}

// Original vector drawings, with consistent round strokes. No platform icon font
// or redistributed SF Symbols is needed, so Windows 10 and 11 render alike.
public sealed class NotchIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(NotchIconKind), typeof(NotchIcon),
        new FrameworkPropertyMetadata(NotchIconKind.Music, FrameworkPropertyMetadataOptions.AffectsRender,
            (owner, _) => ((NotchIcon)owner)._iconTransform = null));
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(NotchIcon),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BatteryLevelProperty = DependencyProperty.Register(
        nameof(BatteryLevel), typeof(double), typeof(NotchIcon),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender,
            null, (_, value) => double.IsFinite((double)value) ? Math.Clamp((double)value, 0, 100) : 0.0));
    public static readonly DependencyProperty SoundLevelProperty = DependencyProperty.Register(
        nameof(SoundLevel), typeof(double), typeof(NotchIcon),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender,
            null, (_, value) => double.IsFinite((double)value) ? Math.Clamp((double)value, 0, 100) : 0.0));

    public NotchIconKind Kind { get => (NotchIconKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double BatteryLevel { get => (double)GetValue(BatteryLevelProperty); set => SetValue(BatteryLevelProperty, value); }
    public double SoundLevel { get => (double)GetValue(SoundLevelProperty); set => SetValue(SoundLevelProperty, value); }

    private Pen? _outlinePen;
    private Brush? _outlineBrush;
    private MatrixTransform? _iconTransform;
    private Size _transformSize;
    private static readonly Geometry ChargingBolt = Parse("M12,7 L8,13 H11 L10,17 L15,11 H12 Z");
    private static readonly Geometry SpeakerWave1 = Parse("M15.2,9 C17,10.5 17,13.5 15.2,15");
    private static readonly Geometry SpeakerWave2 = Parse("M17.6,6.5 C21,9.2 21,14.8 17.6,17.5");
    private static readonly Geometry SpeakerWave3 = Parse("M20,4 C25,8 25,16 20,20");
    private static readonly IReadOnlyDictionary<NotchIconKind, Geometry> Outlines = new Dictionary<NotchIconKind, Geometry>
    {
        [NotchIconKind.Music] = Parse("M9,17 V5 L19,3 V15 M9,9 L19,7"),
        [NotchIconKind.Folder] = Parse("M3,7 Q3,5 5,5 H9 L11,7 H19 Q21,7 21,9 V18 Q21,20 19,20 H5 Q3,20 3,18 Z"),
        [NotchIconKind.Timer] = Parse("M9,2 H15 M12,2 V5 M18,5 L20,7 M12,9 V13 L15,15 M12,5 A8,8 0 1 1 11.99,5"),
        [NotchIconKind.Pin] = Parse("M8,3 H16 L15,9 L18,13 H6 L9,9 Z M12,13 V21"),
        [NotchIconKind.Settings] = CreateSettingsGeometry(),
        [NotchIconKind.MutedSpeaker] = Parse("M16.5,9 L22.5,15 M22.5,9 L16.5,15"),
        [NotchIconKind.Upload] = Parse("M12,16 V3 M7,8 L12,3 L17,8 M4,15 V19 Q4,21 6,21 H18 Q20,21 20,19 V15"),
        [NotchIconKind.Download] = Parse("M12,3 V16 M7,11 L12,16 L17,11 M4,17 V19 Q4,21 6,21 H18 Q20,21 20,19 V17"),
        [NotchIconKind.File] = Parse("M6,2 H14 L20,8 V20 Q20,22 18,22 H6 Q4,22 4,20 V4 Q4,2 6,2 Z M14,2 V8 H20"),
        [NotchIconKind.Copy] = Parse("M8,8 H19 Q21,8 21,10 V19 Q21,21 19,21 H10 Q8,21 8,19 Z M16,8 V5 Q16,3 14,3 H5 Q3,3 3,5 V14 Q3,16 5,16 H8"),
        [NotchIconKind.Close] = Parse("M6,6 L18,18 M18,6 L6,18"),
        [NotchIconKind.Reset] = Parse("M4,9 A8,8 0 1 1 5,18 M4,3 V9 H10"),
        [NotchIconKind.Check] = Parse("M5,12 L10,17 L20,6"),
        [NotchIconKind.Battery] = Parse("M3,6 H18 Q20,6 20,8 V16 Q20,18 18,18 H3 Q1,18 1,16 V8 Q1,6 3,6 Z M23,10 V14"),
        [NotchIconKind.ChargingBattery] = Parse("M3,6 H18 Q20,6 20,8 V16 Q20,18 18,18 H3 Q1,18 1,16 V8 Q1,6 3,6 Z M23,10 V14"),
        [NotchIconKind.Plus] = Parse("M12,4 V20 M4,12 H20"),
        [NotchIconKind.Calendar] = Parse("M5,4 H19 Q21,4 21,6 V19 Q21,21 19,21 H5 Q3,21 3,19 V6 Q3,4 5,4 Z M7,2 V6 M17,2 V6 M3,9 H21 M7,13 H9 M12,13 H14 M17,13 H18 M7,17 H9 M12,17 H14"),
        [NotchIconKind.Grid] = Parse("M4,3 H8 Q9,3 9,4 V8 Q9,9 8,9 H4 Q3,9 3,8 V4 Q3,3 4,3 Z M16,3 H20 Q21,3 21,4 V8 Q21,9 20,9 H16 Q15,9 15,8 V4 Q15,3 16,3 Z M4,15 H8 Q9,15 9,16 V20 Q9,21 8,21 H4 Q3,21 3,20 V16 Q3,15 4,15 Z M16,15 H20 Q21,15 21,16 V20 Q21,21 20,21 H16 Q15,21 15,20 V16 Q15,15 16,15 Z"),
        [NotchIconKind.Note] = Parse("M5,3 H19 Q21,3 21,5 V16 L16,21 H5 Q3,21 3,19 V5 Q3,3 5,3 Z M16,21 V16 H21 M7,8 H17 M7,12 H17 M7,16 H12"),
        [NotchIconKind.Clock] = Parse("M12,2 A10,10 0 1 1 11.99,2 M12,6 V12 L16,15"),
        [NotchIconKind.CheckCircle] = Parse("M12,2 A10,10 0 1 1 11.99,2 M7,12 L10,15 L17,8"),
        [NotchIconKind.ChevronLeft] = Parse("M15,5 L8,12 L15,19"),
        [NotchIconKind.ChevronRight] = Parse("M9,5 L16,12 L9,19"),
        [NotchIconKind.Globe] = Parse("M12,2 A10,10 0 1 1 11.99,2 M2,12 H22 M12,2 C5,8 5,16 12,22 C19,16 19,8 12,2 M4,6 H20 M4,18 H20"),
        [NotchIconKind.Bell] = Parse("M5,17 H19 L17,14 V9 A5,5 0 0 0 7,9 V14 Z M10,20 Q12,23 14,20 M12,2 V4"),
        [NotchIconKind.Search] = Parse("M10,3 A7,7 0 1 1 9.99,3 M15,15 L21,21"),
        [NotchIconKind.External] = Parse("M14,3 H21 V10 M21,3 L11,13 M10,5 H5 Q3,5 3,7 V19 Q3,21 5,21 H17 Q19,21 19,19 V14"),
        [NotchIconKind.Controls] = Parse("M6.5,3.5 H17.5 A3.5,3.5 0 0 1 17.5,10.5 H6.5 A3.5,3.5 0 0 1 6.5,3.5 Z"),
        [NotchIconKind.Wifi] = Parse("M2.5,8 C8,3.25 16,3.25 21.5,8 M5.5,11.5 C9.3,8.25 14.7,8.25 18.5,11.5 M8.5,15 C10.5,13.25 13.5,13.25 15.5,15"),
        [NotchIconKind.Bluetooth] = Parse("M12,2 V22 M12,2 L19,8 L5,19 M5,5 L19,16 L12,22"),
        [NotchIconKind.Sun] = Parse("M12,7 A5,5 0 1 1 11.99,7 M12,1 V3 M12,21 V23 M1,12 H3 M21,12 H23 M4,4 L6,6 M18,18 L20,20 M4,20 L6,18 M18,6 L20,4"),
        [NotchIconKind.Display] = Parse("M4,3 H20 Q22,3 22,5 V16 Q22,18 20,18 H4 Q2,18 2,16 V5 Q2,3 4,3 Z M12,18 V22 M7,22 H17"),
        [NotchIconKind.Capture] = Parse("M8,3 H4 Q3,3 3,4 V8 M16,3 H20 Q21,3 21,4 V8 M3,16 V20 Q3,21 4,21 H8 M21,16 V20 Q21,21 20,21 H16 M8,8 H16 V16 H8 Z"),
        [NotchIconKind.Power] = Parse("M12,2 V12 M6,5 A9,9 0 1 0 18,5")
    };
    private static readonly IReadOnlyDictionary<NotchIconKind, Geometry> Fills = new Dictionary<NotchIconKind, Geometry>
    {
        [NotchIconKind.Speaker] = CreateSpeakerGeometry(),
        [NotchIconKind.MutedSpeaker] = CreateSpeakerGeometry(),
        [NotchIconKind.Controls] = CreateControlSwitchFill(),
        [NotchIconKind.Airplane] = Parse("M10.5,9 L3,12.5 Q2.5,12.75 2.5,13.5 V15 L10.5,12.8 V18 L7.5,20 V22 L12,20.5 L16.5,22 V20 L13.5,18 V12.8 L21.5,15 V13.5 Q21.5,12.75 21,12.5 L13.5,9 V4 Q13.5,1.5 12,1.5 Q10.5,1.5 10.5,4 Z"),
        [NotchIconKind.Moon] = Parse("M20.8,15 A9.1,9.1 0 1 1 9,3.2 A8.2,8.2 0 0 0 20.8,15 Z"),
        [NotchIconKind.Play] = Parse("M7,3 Q6,2.5 6,4 V20 Q6,21.5 7.5,20.5 L21,13 Q22.5,12 21,11 Z"),
        [NotchIconKind.Pause] = Parse("M6,3 H9 Q10,3 10,4 V20 Q10,21 9,21 H6 Q5,21 5,20 V4 Q5,3 6,3 Z M15,3 H18 Q19,3 19,4 V20 Q19,21 18,21 H15 Q14,21 14,20 V4 Q14,3 15,3 Z"),
        [NotchIconKind.Previous] = Parse("M12,5 V19 L2,12 Z M22,5 V19 L12,12 Z"),
        [NotchIconKind.Next] = Parse("M2,5 L12,12 L2,19 Z M12,5 L22,12 L12,19 Z")
    };

    static NotchIcon()
    {
        WidthProperty.OverrideMetadata(typeof(NotchIcon), new FrameworkPropertyMetadata(16.0));
        HeightProperty.OverrideMetadata(typeof(NotchIcon), new FrameworkPropertyMetadata(16.0));
        // Buttons own the action, focus and accessible name; drawings are decorative.
        IsHitTestVisibleProperty.OverrideMetadata(typeof(NotchIcon), new FrameworkPropertyMetadata(false));
    }

    protected override void OnRender(DrawingContext context)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        if (_iconTransform is null || _transformSize != RenderSize)
        {
            // Wide SF-style symbols retain their natural aspect ratio. Fitting
            // every drawing into a square made battery and skip marks too small.
            var viewBox = Kind switch
            {
                NotchIconKind.Battery or NotchIconKind.ChargingBattery => new Rect(-1, 4, 26, 16),
                NotchIconKind.Previous or NotchIconKind.Next => new Rect(0, 3, 24, 18),
                _ => new Rect(-1, -1, 26, 26)
            };
            var scale = Math.Min(ActualWidth / viewBox.Width, ActualHeight / viewBox.Height);
            _iconTransform = new MatrixTransform(new Matrix(scale, 0, 0, scale,
                (ActualWidth - viewBox.Width * scale) / 2 - viewBox.X * scale,
                (ActualHeight - viewBox.Height * scale) / 2 - viewBox.Y * scale));
            _iconTransform.Freeze();
            _transformSize = RenderSize;
        }
        if (_outlinePen is null || !ReferenceEquals(_outlineBrush, Foreground))
        {
            _outlineBrush = Foreground;
            _outlinePen = new Pen(Foreground, 1.85)
            {
                StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round
            };
            // Retain the brush reference so a mutable theme/tint brush stays live.
        }
        context.PushTransform(_iconTransform);
        if (Outlines.TryGetValue(Kind, out var outline)) context.DrawGeometry(null, _outlinePen, outline);
        if (Fills.TryGetValue(Kind, out var fill)) context.DrawGeometry(Foreground, null, fill);
        if (Kind == NotchIconKind.Music)
        {
            context.DrawEllipse(Foreground, null, new Point(6.8, 17.6), 3, 2.25);
            context.DrawEllipse(Foreground, null, new Point(16.8, 15.6), 3, 2.25);
        }
        if (Kind == NotchIconKind.Controls)
        {
            context.DrawEllipse(Foreground, null, new Point(6.5, 7), 1.9, 1.9);
        }
        if (Kind == NotchIconKind.Speaker)
        {
            if (SoundLevel > 0) context.DrawGeometry(null, _outlinePen, SpeakerWave1);
            if (SoundLevel > 100.0 / 3) context.DrawGeometry(null, _outlinePen, SpeakerWave2);
            if (SoundLevel > 200.0 / 3) context.DrawGeometry(null, _outlinePen, SpeakerWave3);
        }
        if (Kind == NotchIconKind.Wifi) context.DrawEllipse(Foreground, null, new Point(12, 19), 1.4, 1.4);
        if (Kind == NotchIconKind.Battery && BatteryLevel > 0)
            context.DrawRoundedRectangle(Foreground, null, new Rect(3.5, 8.5, 14 * BatteryLevel / 100, 7), 0.8, 0.8);
        if (Kind == NotchIconKind.ChargingBattery) context.DrawGeometry(Foreground, null, ChargingBolt);
        context.Pop();
    }

    private static Geometry CreateSettingsGeometry()
    {
        // Eight identical teeth, centered in the same 24-unit box as every icon.
        // The short round-joined tooth edges stay legible at a 16-DIP tab size.
        var contour = new StreamGeometry();
        using (var path = contour.Open())
        {
            for (var tooth = 0; tooth < 8; tooth++)
            {
                var angle = -Math.PI / 2 + tooth * Math.PI / 4;
                var rootBefore = GearPoint(angle - Math.PI / 12, 8.0);
                if (tooth == 0) path.BeginFigure(rootBefore, true, true);
                else path.LineTo(rootBefore, true, false);
                path.LineTo(GearPoint(angle - Math.PI / 20, 10.0), true, false);
                path.LineTo(GearPoint(angle + Math.PI / 20, 10.0), true, false);
                path.LineTo(GearPoint(angle + Math.PI / 12, 8.0), true, false);
            }
        }
        contour.Freeze();
        var geometry = new GeometryGroup();
        geometry.Children.Add(contour);
        geometry.Children.Add(new EllipseGeometry(new Point(12, 12), 3.2, 3.2));
        geometry.Freeze();
        return geometry;
    }

    private static Point GearPoint(double angle, double radius) =>
        new(12 + Math.Cos(angle) * radius, 12 + Math.Sin(angle) * radius);

    private static Geometry CreateSpeakerGeometry() =>
        Parse("M3.5,8.5 H6.7 L11.1,4.9 Q12.2,4 12.2,5.5 V18.5 Q12.2,20 11.1,19.1 L6.7,15.5 H3.5 Q2.5,15.5 2.5,14.5 V9.5 Q2.5,8.5 3.5,8.5 Z");

    private static Geometry CreateControlSwitchFill()
    {
        var geometry = new GeometryGroup { FillRule = FillRule.EvenOdd };
        geometry.Children.Add(new RectangleGeometry(new Rect(3, 13.5, 18, 7), 3.5, 3.5));
        geometry.Children.Add(new EllipseGeometry(new Point(17.5, 17), 1.9, 1.9));
        geometry.Freeze();
        return geometry;
    }

    private static Geometry Parse(string data) { var geometry = Geometry.Parse(data); geometry.Freeze(); return geometry; }
}
