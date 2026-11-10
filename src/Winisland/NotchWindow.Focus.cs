using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Winisland.Controls;
using Winisland.Services;

namespace Winisland;

public partial class NotchWindow
{
    private TimeSpan _focusDuration = TimeSpan.FromMinutes(25);
    private bool _focusEditing, _focusEditorStarting, _syncingFocusSettings, _focusEditError;
    private string _focusEditorOriginal = "";
    internal bool IsFocusEditing => _focusEditing;

    private void InitializeFocus()
    {
        _focusDuration = ConfiguredFocusDuration(_settings.Current);
        if (!_focus.IsStarted) _focus.Reset(_focusDuration);
        RefreshFocus();
    }

    private static TimeSpan ConfiguredFocusDuration(IslandSettings settings) =>
        TimeSpan.FromSeconds(settings.FocusDurationSeconds > 0
            ? Math.Clamp(settings.FocusDurationSeconds, 1, 86400) : Math.Clamp(settings.FocusMinutes, 1, 120) * 60);

    private void SyncFocusSettings()
    {
        if (_syncingFocusSettings) return;
        var configured = ConfiguredFocusDuration(_settings.Current);
        if (configured == _focusDuration) return;
        _focusDuration = configured;
        if (!_focus.IsStarted) _focus.Reset(configured);
        RefreshFocus();
    }

    private void RefreshFocus()
    {
        var duration = _focus.IsStarted ? _focus.Duration : _focusDuration;
        var displayed = FormatTimer(_focus.IsStarted ? _focus.Remaining : _focusDuration);
        if (!_focusEditing) TimerEditor.Text = displayed;
        if (FindName("TimerLabel") is TextBlock compatibilityLabel) compatibilityLabel.Text = displayed;
        FocusState.Text = _focus.IsStarted && !_focus.IsRunning ? "Paused"
            : duration == TimeSpan.FromMinutes(5) ? "Break" : "Focus";
        StartTimerButton.Content = _focus.IsStarted ? _focus.IsRunning ? "Pause" : "Resume" : "Start";
        System.Windows.Automation.AutomationProperties.SetName(StartTimerButton, $"{StartTimerButton.Content} timer");
        FocusProgress.Value = _focus.IsStarted
            ? Math.Clamp(100 * (1 - _focus.Remaining.TotalSeconds / _focus.Duration.TotalSeconds), 0, 100) : 0;
        if (!_focusEditing && !_focusEditError)
        {
            FocusEditHint.Text = "Click the time to set a duration";
            FocusEditHint.Foreground = (Brush)FindResource("Muted");
        }
        foreach (var button in new[] { Minutes5, Minutes15, Minutes25, Minutes50 })
        {
            var selected = TimeSpan.FromMinutes(int.Parse((string)button.Tag)) == duration;
            button.Background = selected ? new SolidColorBrush(Color.FromRgb(57, 42, 14)) : Brushes.Transparent;
            button.Foreground = (Brush)FindResource(selected ? "FocusOrange" : "Muted");
        }
    }

    private void OnTimerEditorGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_focusEditing || _focusEditorStarting) return;
        _focusEditorStarting = true;
        try
        {
            EnsureKeyboardInput();
            _focusEditorOriginal = TimerEditor.Text;
            _focusEditing = true;
            _focusEditError = false;
            FocusEditHint.Text = "Enter to set · Esc to cancel";
            FocusEditHint.Foreground = (Brush)FindResource("Muted");
            TimerEditor.SelectAll();
            // Pinning can move keyboard focus to the active tab; return it to the
            // field after that operation without selecting text on every click.
            Dispatcher.BeginInvoke(() =>
            {
                if (!_focusEditing) return;
                if (!TimerEditor.IsKeyboardFocusWithin) TimerEditor.Focus();
                TimerEditor.SelectAll();
            }, DispatcherPriority.Input);
        }
        finally { _focusEditorStarting = false; }
    }

    private void OnTimerEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_focusEditorStarting || !_focusEditing) return;
        CommitFocusEdit(false);
    }

    private void OnTimerEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (CommitFocusEdit(true)) StartTimerButton.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _focusEditing = false;
            _focusEditError = false;
            RefreshFocus();
            FocusTab.Focus();
            e.Handled = true;
        }
    }

    private bool CommitFocusEdit(bool keepInvalidFocused)
    {
        if (!_focusEditing) return true;
        if (string.Equals(TimerEditor.Text.Trim(), _focusEditorOriginal.Trim(), StringComparison.Ordinal))
        {
            _focusEditing = false;
            _focusEditError = false;
            RefreshFocus();
            return true;
        }
        if (!FocusDurationParser.TryParse(TimerEditor.Text, out var duration, out var error))
        {
            _focusEditError = true;
            FocusEditHint.Text = error;
            FocusEditHint.Foreground = new SolidColorBrush(Color.FromRgb(255, 105, 97));
            if (!keepInvalidFocused) { _focusEditing = false; RefreshFocus(); }
            return false;
        }
        _focusEditing = false;
        _focusEditError = false;
        SetConfiguredFocusDuration(duration, true);
        return true;
    }

    private void SetConfiguredFocusDuration(TimeSpan duration, bool preserveSession)
    {
        _focusDuration = duration;
        if (preserveSession) _focus.SetDuration(duration);
        else _focus.Reset(duration);
        try
        {
            _syncingFocusSettings = true;
            _settings.Update(_settings.Current with
            {
                FocusDurationSeconds = (int)duration.TotalSeconds,
                FocusMinutes = Math.Clamp((int)Math.Ceiling(duration.TotalMinutes), 1, 120)
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Save focus duration", ex);
            _focusEditError = true;
            FocusEditHint.Text = "Time set for now. Couldn't save it.";
            FocusEditHint.Foreground = new SolidColorBrush(Color.FromRgb(255, 159, 10));
        }
        finally { _syncingFocusSettings = false; }
        RefreshFocus();
    }

    private void OnDurationClick(object sender, RoutedEventArgs e)
    {
        _focusEditing = _focusEditError = false;
        SetConfiguredFocusDuration(TimeSpan.FromMinutes(int.Parse((string)((Button)sender).Tag)), false);
        if (TimerEditor.IsKeyboardFocusWithin) StartTimerButton.Focus();
    }

    private void OnStartTimerClick(object sender, RoutedEventArgs e)
    {
        if (_focusEditing && !CommitFocusEdit(true)) return;
        if (_focus.IsStarted) _focus.TogglePause();
        else _focus.Start(_focusDuration);
    }

    private void OnResetTimerClick(object sender, RoutedEventArgs e)
    {
        _focusEditing = _focusEditError = false;
        _focus.Reset(_focusDuration);
        RefreshFocus();
        if (TimerEditor.IsKeyboardFocusWithin) StartTimerButton.Focus();
    }

    private void OnFocusCompleted(object? sender, EventArgs e)
    {
        Notification?.Invoke(this, "Focus session complete. Take a little break.");
        ShowHud("Focus complete", NotchIconKind.Check, 100, "✓");
    }

    private static string FormatTimer(TimeSpan time) => FocusDurationParser.Format(time);
}
