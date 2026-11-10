using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Winisland.Services;

namespace Winisland;

internal static partial class FocusVerification
{
    internal static async Task RunAsync(NotchWindow notch, SettingsStore store, FocusTimer focus, Action<bool, string> assert)
    {
        await RunCoreAsync(assert);
        var originalSettings = store.Current;
        var editor = (TextBox)notch.FindName("TimerEditor");
        var hint = (TextBlock)notch.FindName("FocusEditHint");
        var start = (Button)notch.FindName("StartTimerButton");
        try
        {
            store.Update(originalSettings with { FocusMinutes = 25, FocusDurationSeconds = 1500 });
            focus.Reset(TimeSpan.FromMinutes(25));
            notch.SelectTabForTest("focus");
            notch.Expand(true);
            notch.UpdateLayout();
            editor.Focus();
            await Task.Delay(40);
            assert(editor.IsKeyboardFocusWithin && editor.SelectionLength == editor.Text.Length,
                "The focus countdown is an editable field that selects its time for typing");
            editor.Text = "45";
            Press(Key.Enter);
            await Task.Delay(30);
            assert(focus.Duration == TimeSpan.FromMinutes(45) && !focus.IsStarted && editor.Text == "45:00",
                "Typing minutes and pressing Enter sets the actual focus duration");
            assert(store.Current.FocusDurationSeconds == 2700 && new SettingsStore().Current.FocusDurationSeconds == 2700,
                "Custom focus durations survive settings reload");

            focus.Start(TimeSpan.FromSeconds(50));
            editor.Focus();
            await Task.Delay(30);
            var beforeUnchanged = focus.Remaining;
            await Task.Delay(60);
            start.Focus();
            assert(focus.IsRunning && focus.Duration == TimeSpan.FromSeconds(50) && focus.Remaining < beforeUnchanged,
                "Leaving an unchanged countdown field preserves the running timer and elapsed time");

            editor.Focus();
            await Task.Delay(30);
            editor.Text = "0:00";
            Press(Key.Enter);
            assert(editor.IsKeyboardFocusWithin && focus.Duration == TimeSpan.FromSeconds(50) && !string.IsNullOrWhiteSpace(hint.Text),
                "Invalid typed focus time shows an inline error without changing or stopping the timer");
            editor.Text = "8:12";
            Press(Key.Escape);
            assert(notch.IsExpanded && focus.Duration == TimeSpan.FromSeconds(50) && focus.IsRunning,
                "Escape cancels a time edit while preserving both the timer and the open notch");

            focus.Start(TimeSpan.FromMinutes(1));
            editor.Focus();
            await Task.Delay(30);
            editor.Text = "12:34";
            await Task.Delay(1100);
            assert(editor.Text == "12:34" && focus.IsRunning,
                "Timer ticks continue without overwriting a time being typed");
            Press(Key.Escape);

            focus.TogglePause();
            editor.Focus();
            await Task.Delay(30);
            editor.Text = "1:30:00";
            Press(Key.Enter);
            assert(focus.IsStarted && !focus.IsRunning && focus.Duration == TimeSpan.FromMinutes(90)
                && focus.Remaining == TimeSpan.FromMinutes(90) && editor.Text == "1:30:00",
                "Editing a paused timer accepts hour durations and keeps the session paused");
            focus.TogglePause();
            editor.Focus();
            await Task.Delay(30);
            editor.Text = "2:15";
            Press(Key.Enter);
            assert(focus.IsRunning && focus.IsStarted && focus.Duration == TimeSpan.FromSeconds(135)
                && focus.Remaining > TimeSpan.FromSeconds(134),
                "Editing a running timer changes its remaining duration and keeps it running");

            ((Button)notch.FindName("Minutes15")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            assert(!focus.IsStarted && focus.Duration == TimeSpan.FromMinutes(15) && store.Current.FocusDurationSeconds == 900,
                "A focus preset resets the active session and persists its selected duration");
        }
        finally
        {
            if (editor.IsKeyboardFocusWithin) Press(Key.Escape);
            focus.Reset(TimeSpan.FromSeconds(originalSettings.FocusDurationSeconds > 0
                ? originalSettings.FocusDurationSeconds : originalSettings.FocusMinutes * 60));
            store.Update(originalSettings);
            notch.SelectTabForTest("music");
        }

        void Press(Key key)
        {
            editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor), Environment.TickCount, key)
            { RoutedEvent = Keyboard.KeyDownEvent });
        }
    }

}
