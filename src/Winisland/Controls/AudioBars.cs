using System;
using System.Windows;
using System.Windows.Media;

namespace Winisland.Controls;

// Each bar follows its current frequency band. Fast attack preserves audible hits;
// a short release smooths the landing without turning the beat into a rolling wave.
public sealed class AudioBars : FrameworkElement
{
    private const int BandCount = 13;
    private const int BarCount = 5;
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(AudioBars),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    private readonly double[] _levels = new double[BarCount];
    private bool _reducedMotion;

    public void Update(ReadOnlySpan<float> bands, double elapsed, bool reducedMotion)
    {
        _reducedMotion = reducedMotion;
        if (reducedMotion)
        {
            Array.Clear(_levels);
            InvalidateVisual();
            return;
        }
        if (!double.IsFinite(elapsed) || elapsed <= 0) return;
        elapsed = Math.Min(elapsed, 0.1);
        for (var i = 0; i < BarCount; i++)
        {
            // Preserve all 13 analyzed bands in five compact frequency groups.
            // RMS pooling keeps bass, voice and treble represented in the wing.
            var start = i * BandCount / BarCount; var end = (i + 1) * BandCount / BarCount;
            double power = 0;
            for (var band = start; band < end; band++)
            {
                var level = band < bands.Length && float.IsFinite(bands[band]) ? Math.Clamp(bands[band], 0, 1) : 0;
                power += level * level;
            }
            var target = Math.Sqrt(power / (end - start));
            var timeConstant = target > _levels[i] ? 0.012 : 0.085;
            _levels[i] += (target - _levels[i]) * (1 - Math.Exp(-elapsed / timeConstant));
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var stride = ActualWidth / BarCount;
        var width = Math.Min(2, stride * 0.62);
        var baseline = Math.Min(2, ActualHeight);
        for (var i = 0; i < BarCount; i++)
        {
            var height = Math.Min(ActualHeight, _reducedMotion ? 7 : baseline + _levels[i] * (ActualHeight - baseline));
            context.DrawRoundedRectangle(Foreground, null,
                new Rect(i * stride + (stride - width) / 2, (ActualHeight - height) / 2, width, height), width / 2, width / 2);
        }
    }
}
