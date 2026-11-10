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

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;
        if ((bool)args.NewValue)
        {
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = new ScaleTransform(1, 1);
            element.MouseEnter += OnEnter;
            element.MouseLeave += OnLeave;
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
            element.MouseEnter -= OnEnter;
            element.MouseLeave -= OnLeave;
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
        if (element.RenderTransform is not ScaleTransform scale) return;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = scale.ScaleY = 1;
    }

    private static double HoverScale(FrameworkElement element) => element.IsEnabled ? 1.025 : 1;
    private static void OnEnter(object sender, MouseEventArgs args)
    {
        var element = (FrameworkElement)sender;
        if (!(bool)element.GetValue(PressedProperty)) Animate(element, HoverScale(element), 150);
    }
    private static void OnLeave(object sender, MouseEventArgs args)
    {
        var element = (FrameworkElement)sender;
        if (!(bool)element.GetValue(PressedProperty)) Animate(element, 1, 130);
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
        if (!element.IsEnabled) return;
        element.SetValue(PressedProperty, true);
        Animate(element, 0.94, 70);
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
        Animate(element, element.IsMouseOver ? HoverScale(element) : 1, 170);
    }

    private sealed class OwnerMotion
    {
        private readonly FrameworkElement _surface;
        private readonly MouseButtonEventHandler _press;
        private readonly MouseButtonEventHandler _release;
        private readonly MouseEventHandler _captureLost;
        public ButtonBase? Button { get; private set; }

        public OwnerMotion(FrameworkElement surface)
        {
            _surface = surface;
            _press = (_, _) => Press(_surface);
            _release = (_, _) => Release(_surface);
            _captureLost = (_, _) => Release(_surface);
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
        }

        public void Detach()
        {
            if (Button is null) return;
            Button.RemoveHandler(UIElement.PreviewMouseLeftButtonDownEvent, _press);
            Button.RemoveHandler(UIElement.PreviewMouseLeftButtonUpEvent, _release);
            Button.RemoveHandler(UIElement.LostMouseCaptureEvent, _captureLost);
            Button = null;
        }
    }

    private static void Animate(FrameworkElement element, double target, int duration)
    {
        if (element.RenderTransform is not ScaleTransform scale) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale.ScaleX, target, TimeSpan.FromMilliseconds(duration)) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale.ScaleY, target, TimeSpan.FromMilliseconds(duration)) { EasingFunction = ease });
    }
}
