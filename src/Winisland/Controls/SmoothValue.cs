using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;

namespace Winisland.Controls;

// Animate the rail's presentation, never the control's value. Input and player
// commands remain immediate, precise, and independent of display cadence.
public static class SmoothValue
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SmoothValue), new PropertyMetadata(false, OnEnabledChanged));
    public static readonly DependencyProperty MotionEnabledProperty = DependencyProperty.RegisterAttached(
        "MotionEnabled", typeof(bool), typeof(SmoothValue), new PropertyMetadata(true, OnMotionChanged));
    public static readonly DependencyProperty DisplayValueProperty = DependencyProperty.RegisterAttached(
        "DisplayValue", typeof(double), typeof(SmoothValue), new PropertyMetadata(0.0));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(Presentation), typeof(SmoothValue));

    public static bool GetEnabled(DependencyObject owner) => (bool)owner.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject owner, bool value) => owner.SetValue(EnabledProperty, value);
    public static bool GetMotionEnabled(DependencyObject owner) => (bool)owner.GetValue(MotionEnabledProperty);
    public static void SetMotionEnabled(DependencyObject owner, bool value) => owner.SetValue(MotionEnabledProperty, value);
    public static double GetDisplayValue(DependencyObject owner) => (double)owner.GetValue(DisplayValueProperty);

    private static void OnEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not RangeBase control) return;
        if (control.GetValue(StateProperty) is Presentation previous) previous.Dispose();
        control.ClearValue(StateProperty);
        if ((bool)args.NewValue) control.SetValue(StateProperty, new Presentation(control));
    }

    private static void OnMotionChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (!(bool)args.NewValue && sender.GetValue(StateProperty) is Presentation state) state.Snap();
    }

    private sealed class Presentation : IDisposable
    {
        private readonly RangeBase _owner;
        private int _version;
        private bool _disposed;

        public Presentation(RangeBase owner)
        {
            _owner = owner;
            owner.ValueChanged += OnValueChanged;
            owner.IsMouseCaptureWithinChanged += OnCaptureChanged;
            owner.IsVisibleChanged += OnVisibilityChanged;
            owner.Loaded += OnLoaded;
            owner.Unloaded += OnUnloaded;
            Snap();
        }

        private void OnLoaded(object sender, RoutedEventArgs args) => Snap();
        private void OnUnloaded(object sender, RoutedEventArgs args) => Snap();
        private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (!(bool)args.NewValue) Snap();
        }
        private void OnCaptureChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            if ((bool)args.NewValue) Snap();
        }

        private void OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> args)
        {
            if (_disposed) return;
            // RangeBase.ValueChanged bubbles out of templated child rails. A
            // presented frame is not a new target or an input command.
            if (!ReferenceEquals(args.OriginalSource, _owner)) { args.Handled = true; return; }
            if (!_owner.IsLoaded || !_owner.IsVisible || !GetMotionEnabled(_owner) || !SystemParameters.ClientAreaAnimation
                || _owner.IsMouseCaptureWithin || _owner is ScrubSlider { IsAdjusting: true })
            {
                Snap();
                return;
            }
            var from = GetDisplayValue(_owner);
            var target = _owner.Value;
            if (Math.Abs(from - target) < 0.001) { Snap(); return; }
            var version = ++_version;
            var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(130))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            animation.Completed += (_, _) => { if (version == _version && !_disposed) Snap(); };
            _owner.BeginAnimation(DisplayValueProperty, animation, HandoffBehavior.SnapshotAndReplace);
            // This DP belongs to presentation. SetValue clears the current-value
            // override so a newly installed clock retains its captured position.
            _owner.SetValue(DisplayValueProperty, target);
        }

        public void Snap()
        {
            ++_version;
            _owner.BeginAnimation(DisplayValueProperty, null);
            _owner.SetCurrentValue(DisplayValueProperty, _owner.Value);
        }

        public void Dispose()
        {
            _disposed = true;
            _owner.ValueChanged -= OnValueChanged;
            _owner.IsMouseCaptureWithinChanged -= OnCaptureChanged;
            _owner.IsVisibleChanged -= OnVisibilityChanged;
            _owner.Loaded -= OnLoaded;
            _owner.Unloaded -= OnUnloaded;
            Snap();
        }
    }
}
