using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Winisland.Controls;

public sealed class AdjustmentCompletedEventArgs : EventArgs
{
    public bool Canceled { get; }
    public AdjustmentCompletedEventArgs(bool canceled) => Canceled = canceled;
}

// Shared pointer behavior for volume and playback: the whole rail starts a drag.
public class ScrubSlider : Slider
{
    private bool _localPointerCapture;
    internal bool HasPointerCapture => IsMouseCaptured || _localPointerCapture;
    public bool IsAdjusting { get; private set; }
    public double WheelChange { get; set; } = 5;
    public event EventHandler? AdjustmentStarted;
    public event EventHandler<AdjustmentCompletedEventArgs>? AdjustmentCompleted;

    public ScrubSlider()
    {
        AddHandler(ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>((_, args) =>
        {
            if (!ReferenceEquals(args.OriginalSource, this)) args.Handled = true;
        }));
        IsEnabledChanged += (_, _) => { if (!IsEnabled) CancelAdjustment(); };
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (!BeginPointerAdjustment(e.GetPosition(this))) return;
        e.Handled = true;
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (!IsAdjusting || !IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        MovePointerAdjustment(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (!IsAdjusting || !IsMouseCaptured) return;
        EndPointerAdjustment(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (IsAdjusting) FinishAdjustment(true);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) CancelAdjustment();
        base.OnPreviewKeyDown(e);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!IsEnabled) return;
        if (!IsAdjusting)
        {
            var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? SmallChange : WheelChange;
            SetCurrentValue(ValueProperty, Math.Clamp(Math.Round(Value) + Math.Sign(e.Delta) * step, Minimum, Maximum));
        }
        e.Handled = true;
    }

    public void CancelAdjustment() => FinishAdjustment(true);

    private void FinishAdjustment(bool canceled)
    {
        if (!IsAdjusting) return;
        IsAdjusting = false;
        _localPointerCapture = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        AdjustmentCompleted?.Invoke(this, new AdjustmentCompletedEventArgs(canceled));
    }

    // The same input path accepts control-local coordinates in verification.
    // No global cursor movement or injected desktop input is needed.
    internal bool BeginPointerAdjustment(Point position, bool captureMouse = true)
    {
        if (!IsEnabled) return false;
        if (Window.GetWindow(this)?.IsActive == true) Focus();
        if (captureMouse && !CaptureMouse()) return false;
        _localPointerCapture = !captureMouse;
        IsAdjusting = true;
        AdjustmentStarted?.Invoke(this, EventArgs.Empty);
        SetValueFromPointer(position);
        return true;
    }

    internal void MovePointerAdjustment(Point position)
    {
        if (IsAdjusting && HasPointerCapture) SetValueFromPointer(position);
    }

    internal void EndPointerAdjustment(Point position)
    {
        if (!IsAdjusting || !HasPointerCapture) return;
        SetValueFromPointer(position);
        FinishAdjustment(false);
    }

    private void SetValueFromPointer(Point position)
    {
        if (Template.FindName("PART_Track", this) is Track track)
        {
            // Use the absolute range; Track.ValueFromPoint depends on the last
            // arranged thumb position, which can lag behind rapid input events.
            var thumbWidth = track.Thumb.ActualWidth;
            var railWidth = track.ActualWidth - thumbWidth;
            if (railWidth <= 0) return;
            var fraction = Math.Clamp((TranslatePoint(position, track).X - thumbWidth / 2) / railWidth, 0, 1);
            if (IsDirectionReversed) fraction = 1 - fraction;
            SetCurrentValue(ValueProperty, Minimum + fraction * (Maximum - Minimum));
        }
    }
}
