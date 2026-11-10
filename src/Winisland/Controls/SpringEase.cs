using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace Winisland.Controls;

// A damped oscillator step response with a finite, zero-velocity landing.
// SwiftUI's response describes spring stiffness rather than a hard end time.
internal sealed class SpringEase : EasingFunctionBase
{
    public static readonly DependencyProperty DampingRatioProperty = DependencyProperty.Register(
        nameof(DampingRatio), typeof(double), typeof(SpringEase),
        new PropertyMetadata(1.0, null, CoerceDamping));
    public static readonly DependencyProperty ResponseSecondsProperty = DependencyProperty.Register(
        nameof(ResponseSeconds), typeof(double), typeof(SpringEase),
        new PropertyMetadata(0.45, null, CoerceSeconds));
    public static readonly DependencyProperty AnimationSecondsProperty = DependencyProperty.Register(
        nameof(AnimationSeconds), typeof(double), typeof(SpringEase),
        new PropertyMetadata(0.60, null, CoerceSeconds));
    public static readonly DependencyProperty InitialVelocityProperty = DependencyProperty.Register(
        nameof(InitialVelocity), typeof(double), typeof(SpringEase),
        new PropertyMetadata(0.0, null, CoerceVelocity));

    public double DampingRatio
    {
        get => (double)GetValue(DampingRatioProperty);
        set => SetValue(DampingRatioProperty, value);
    }
    public double ResponseSeconds
    {
        get => (double)GetValue(ResponseSecondsProperty);
        set => SetValue(ResponseSecondsProperty, value);
    }
    public double AnimationSeconds
    {
        get => (double)GetValue(AnimationSecondsProperty);
        set => SetValue(AnimationSecondsProperty, value);
    }

    // Target-relative units per second, as in Apple's spring model. A new
    // destination can inherit the presented velocity of the previous spring.
    public double InitialVelocity
    {
        get => (double)GetValue(InitialVelocityProperty);
        set => SetValue(InitialVelocityProperty, value);
    }

    public SpringEase() => EasingMode = EasingMode.EaseIn;

    protected override double EaseInCore(double time)
    {
        if (!double.IsFinite(time) || time <= 0) return 0;
        if (time >= 1) return 1;
        var frequency = 2 * Math.PI * AnimationSeconds / ResponseSeconds;
        var damping = DampingRatio;
        var initialVelocity = InitialVelocity * AnimationSeconds;
        var value = Sample(time, frequency, damping, initialVelocity);
        var landing = Sample(1, frequency, damping, initialVelocity);
        var remainder = 1 - landing.Position;
        var slope = landing.Velocity;
        // This small Hermite correction preserves the initial velocity and
        // makes the final value and velocity exact. A WPF clock can then finish
        // without cutting off a spring's remaining movement or snapping a pixel.
        var correction = (3 * remainder + slope) * time * time
            - (2 * remainder + slope) * time * time * time;
        return value.Position + correction;
    }

    internal double VelocityAt(double time)
    {
        if (!double.IsFinite(time) || time >= 1) return 0;
        if (time <= 0) return InitialVelocity;
        var frequency = 2 * Math.PI * AnimationSeconds / ResponseSeconds;
        var initialVelocity = InitialVelocity * AnimationSeconds;
        var sample = Sample(time, frequency, DampingRatio, initialVelocity);
        var landing = Sample(1, frequency, DampingRatio, initialVelocity);
        var remainder = 1 - landing.Position;
        var correction = 2 * (3 * remainder + landing.Velocity) * time
            - 3 * (2 * remainder + landing.Velocity) * time * time;
        return (sample.Velocity + correction) / AnimationSeconds;
    }

    private static (double Position, double Velocity) Sample(double time, double frequency, double damping, double initialVelocity)
    {
        if (damping >= 0.999999)
        {
            var tail = Math.Exp(-frequency * time);
            var slope = frequency - initialVelocity;
            return (1 - (1 + slope * time) * tail,
                (initialVelocity + frequency * slope * time) * tail);
        }
        var damped = Math.Sqrt(1 - damping * damping);
        var angle = frequency * damped * time;
        var envelope = Math.Exp(-damping * frequency * time);
        var dampedFrequency = frequency * damped;
        var sineCoefficient = (damping * frequency - initialVelocity) / dampedFrequency;
        return (1 - envelope * (Math.Cos(angle) + sineCoefficient * Math.Sin(angle)),
            envelope * (initialVelocity * Math.Cos(angle)
                + (frequency * frequency - damping * frequency * initialVelocity) / dampedFrequency * Math.Sin(angle)));
    }

    private static object CoerceDamping(DependencyObject owner, object value) =>
        double.IsFinite((double)value) ? Math.Clamp((double)value, 0.5, 1) : 1.0;
    private static object CoerceSeconds(DependencyObject owner, object value) =>
        double.IsFinite((double)value) ? Math.Clamp((double)value, 0.01, 10) : 0.60;
    private static object CoerceVelocity(DependencyObject owner, object value) =>
        double.IsFinite((double)value) ? (double)value : 0.0;

    protected override Freezable CreateInstanceCore() => new SpringEase();
}
