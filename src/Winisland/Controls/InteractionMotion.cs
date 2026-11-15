using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Winisland.Controls;

public static class InteractionMotion
{
    public const string PreferenceKey = "MotionEnabled";
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(InteractionMotion), new PropertyMetadata(false, OnEnabledChanged));
    private static readonly DependencyProperty PressedProperty = DependencyProperty.RegisterAttached(
        "Pressed", typeof(bool), typeof(InteractionMotion), new PropertyMetadata(false));
    private static readonly DependencyProperty OwnerMotionProperty = DependencyProperty.RegisterAttached(
        "OwnerMotion", typeof(OwnerMotion), typeof(InteractionMotion), new PropertyMetadata(null));
    private static readonly DependencyProperty AnimationClockProperty = DependencyProperty.RegisterAttached(
        "AnimationClock", typeof(ControlMotion), typeof(InteractionMotion), new PropertyMetadata(null));
    private sealed record ControlMotion(AnimationClock ClockX, AnimationClock ClockY,
        SpringEase EaseX, SpringEase EaseY, double DistanceX, double DistanceY);

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;
        if ((bool)args.NewValue)
        {
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = new ScaleTransform(1, 1);
            element.PreviewMouseLeftButtonDown += OnPress;
            element.PreviewMouseLeftButtonUp += OnRelease;
            element.LostMouseCapture += OnCaptureLost;
            element.Loaded += OnLoaded;
            element.Unloaded += OnUnloaded;
            element.IsEnabledChanged += OnSurfaceEnabledChanged;
            var owner = new OwnerMotion(element);
            element.SetValue(OwnerMotionProperty, owner);
            owner.Attach();
        }
        else
        {
            element.PreviewMouseLeftButtonDown -= OnPress;
            element.PreviewMouseLeftButtonUp -= OnRelease;
            element.LostMouseCapture -= OnCaptureLost;
            element.Loaded -= OnLoaded;
            element.Unloaded -= OnUnloaded;
            element.IsEnabledChanged -= OnSurfaceEnabledChanged;
            GetOwner(element)?.Detach();
            element.ClearValue(OwnerMotionProperty);
            Reset(element);
        }
    }

    private static OwnerMotion? GetOwner(FrameworkElement element) =>
        (OwnerMotion?)element.GetValue(OwnerMotionProperty);
    private static void OnLoaded(object sender, RoutedEventArgs args) => GetOwner((FrameworkElement)sender)?.Attach();
    private static void OnUnloaded(object sender, RoutedEventArgs args)
    {
        var element = (FrameworkElement)sender;
        GetOwner(element)?.Detach();
        Reset(element);
    }
    private static void OnSurfaceEnabledChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        var element = (FrameworkElement)sender;
        if ((bool)args.NewValue) GetOwner(element)?.Attach();
        else { GetOwner(element)?.Detach(); Reset(element); }
    }
    private static void Reset(FrameworkElement element)
    {
        element.SetValue(PressedProperty, false);
        element.ClearValue(AnimationClockProperty);
        if (element.RenderTransform is not ScaleTransform scale) return;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = scale.ScaleY = 1;
    }

    private static void OnPress(object sender, MouseButtonEventArgs args)
    {
        var element = (FrameworkElement)sender;
        // A Button captures input to itself, so its inner Border cannot observe
        // the eventual release. Use owner events; retain this fallback for a
        // standalone surface without a ButtonBase ancestor.
        if (GetOwner(element)?.Button is null) Press(element);
    }
    private static void Press(FrameworkElement element)
    {
        if (!element.IsEnabled || (bool)element.GetValue(PressedProperty)) return;
        element.SetValue(PressedProperty, true);
        // Pointer hover is already conveyed by the control's fill. Keep text
        // at native size until an intentional press, with subdued desktop motion.
        Animate(element, 0.97, 80);
    }
    private static void OnRelease(object sender, MouseButtonEventArgs args)
    {
        var element = (FrameworkElement)sender;
        if (GetOwner(element)?.Button is null) Release(element);
    }
    private static void OnCaptureLost(object sender, MouseEventArgs args)
    {
        var element = (FrameworkElement)sender;
        if (GetOwner(element)?.Button is null) Release(element);
    }
    private static void Release(FrameworkElement element)
    {
        if (!(bool)element.GetValue(PressedProperty)) return;
        element.SetValue(PressedProperty, false);
        Animate(element, 1, 150);
    }

    private sealed class OwnerMotion
    {
        private readonly FrameworkElement _surface;
        private readonly MouseButtonEventHandler _press;
        private readonly MouseButtonEventHandler _release;
        private readonly MouseEventHandler _captureLost;
        private readonly KeyEventHandler _keyDown;
        private readonly KeyEventHandler _keyUp;
        private readonly KeyboardFocusChangedEventHandler _focusLost;
        public ButtonBase? Button { get; private set; }

        public OwnerMotion(FrameworkElement surface)
        {
            _surface = surface;
            _press = (_, _) => Press(_surface);
            _release = (_, _) => Release(_surface);
            _captureLost = (_, _) => Release(_surface);
            _keyDown = (_, args) =>
            {
                if (args.Key == Key.Space && !args.IsRepeat
                    && (Keyboard.Modifiers & (ModifierKeys.Alt | ModifierKeys.Control)) == 0)
                    Press(_surface);
            };
            _keyUp = (_, args) => { if (args.Key == Key.Space) Release(_surface); };
            _focusLost = (_, _) => Release(_surface);
        }

        public void Attach()
        {
            if (!_surface.IsEnabled) return;
            var button = _surface.TemplatedParent as ButtonBase;
            for (DependencyObject? ancestor = _surface; button is null && ancestor is not null;
                ancestor = VisualTreeHelper.GetParent(ancestor))
                button = ancestor as ButtonBase;
            if (ReferenceEquals(Button, button)) return;
            Detach();
            Button = button;
            if (Button is null) return;
            // Observe even handled releases without changing the Button's own
            // input handling, capture, click behavior or render transform.
            Button.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, _press, true);
            Button.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, _release, true);
            Button.AddHandler(UIElement.LostMouseCaptureEvent, _captureLost, true);
            Button.AddHandler(Keyboard.PreviewKeyDownEvent, _keyDown, true);
            Button.AddHandler(Keyboard.PreviewKeyUpEvent, _keyUp, true);
            Button.AddHandler(Keyboard.LostKeyboardFocusEvent, _focusLost, true);
        }

        public void Detach()
        {
            if (Button is null) return;
            Button.RemoveHandler(UIElement.PreviewMouseLeftButtonDownEvent, _press);
            Button.RemoveHandler(UIElement.PreviewMouseLeftButtonUpEvent, _release);
            Button.RemoveHandler(UIElement.LostMouseCaptureEvent, _captureLost);
            Button.RemoveHandler(Keyboard.PreviewKeyDownEvent, _keyDown);
            Button.RemoveHandler(Keyboard.PreviewKeyUpEvent, _keyUp);
            Button.RemoveHandler(Keyboard.LostKeyboardFocusEvent, _focusLost);
            Button = null;
        }
    }

    private static void Animate(FrameworkElement element, double target, int duration)
    {
        if (element.RenderTransform is not ScaleTransform scale) return;
        var fromX = scale.ScaleX;
        var fromY = scale.ScaleY;
        if (!scale.HasAnimatedProperties && Math.Abs(fromX - target) < 0.000001
            && Math.Abs(fromY - target) < 0.000001) return;
        var seconds = duration / 1000.0;
        var velocityX = 0.0;
        var velocityY = 0.0;
        if (element.GetValue(AnimationClockProperty) is ControlMotion previous)
        {
            velocityX = previous.DistanceX * previous.EaseX.VelocityAt(
                (previous.ClockX.CurrentTime?.TotalSeconds ?? 0) / previous.EaseX.AnimationSeconds);
            velocityY = previous.DistanceY * previous.EaseY.VelocityAt(
                (previous.ClockY.CurrentTime?.TotalSeconds ?? 0) / previous.EaseY.AnimationSeconds);
        }
        SpringEase Ease(double distance, double velocity) => new()
        {
            DampingRatio = 1, ResponseSeconds = seconds * 0.9, AnimationSeconds = seconds,
            InitialVelocity = Math.Abs(distance) > 0.000001 ? velocity / distance : 0
        };
        var easeX = Ease(target - fromX, velocityX);
        var easeY = Ease(target - fromY, velocityY);
        var clockX = new DoubleAnimation(fromX, target, TimeSpan.FromMilliseconds(duration))
            { EasingFunction = easeX }.CreateClock();
        var clockY = new DoubleAnimation(fromY, target, TimeSpan.FromMilliseconds(duration))
            { EasingFunction = easeY }.CreateClock();
        var motion = new ControlMotion(clockX, clockY, easeX, easeY, target - fromX, target - fromY);
        element.SetValue(AnimationClockProperty, motion);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = fromX;
        scale.ScaleY = fromY;
        clockX.Completed += (_, _) =>
        {
            // An interrupted clock must never clear its replacement. Once this
            // transition settles, release both clocks instead of holding them.
            if (!ReferenceEquals(element.GetValue(AnimationClockProperty), motion)) return;
            scale.ScaleX = scale.ScaleY = target;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            element.ClearValue(AnimationClockProperty);
        };
        scale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, clockX);
        scale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, clockY);
    }
}
