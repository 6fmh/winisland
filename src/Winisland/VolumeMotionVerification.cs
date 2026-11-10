using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Winisland.Controls;

namespace Winisland;

internal static class VolumeMotionVerification
{
    internal static async Task RunAsync(NotchWindow notch, Action<bool, string> assert)
    {
        var previousMotion = Application.Current.Resources[InteractionMotion.PreferenceKey];
        var slider = new VolumeSlider
        {
            Width = 240, Height = 32, Minimum = 0, Maximum = 100, Value = 20,
            Style = (Style)notch.FindResource("VolumeSliderStyle")
        };
        var window = new Window
        {
            Width = 300, Height = 80, WindowStyle = WindowStyle.None,
            ShowInTaskbar = false, ShowActivated = false, Content = slider, Background = Brushes.Black
        };
        try
        {
            Application.Current.Resources[InteractionMotion.PreferenceKey] = true;
            window.Show();
            window.UpdateLayout();
            // Capture a presented baseline before changing the target. Loading
            // and the first frame can take longer on an inactive test desktop.
            var firstFrame = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler ready = (_, _) => firstFrame.TrySetResult(true);
            CompositionTarget.Rendering += ready;
            try { await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
            finally { CompositionTarget.Rendering -= ready; }
            var rail = (ProgressBar)slider.Template.FindName("Rail", slider);
            assert(System.Windows.Data.BindingOperations.GetBindingExpression(rail, System.Windows.Controls.Primitives.RangeBase.ValueProperty)?.Status == System.Windows.Data.BindingStatus.Active,
                "Volume presentation binds correctly inside deferred control templates");
            var actualChanges = 0;
            slider.ValueChanged += (_, _) => actualChanges++;
            var frames = new List<double>();
            EventHandler observe = (_, _) => frames.Add(rail.Value);
            CompositionTarget.Rendering += observe;
            try
            {
                slider.Value = 80;
                assert(slider.Value == 80, "Volume target responds immediately while its presentation animates");
                await Task.Delay(190);
            }
            finally { CompositionTarget.Rendering -= observe; }
            if (SystemParameters.ClientAreaAnimation)
                assert(frames.Exists(value => value is > 20.1 and < 79.9),
                    "The rendered volume rail presents intermediate values instead of jumping to its target");
            assert(Math.Abs(rail.Value - 80) < 0.01 && actualChanges == 1,
                $"Volume presentation lands exactly and never sends extra player-volume writes (rail={rail.Value}, target={slider.Value}, changes={actualChanges})");

            slider.Value = 30;
            await Task.Delay(40);
            var beforeReversal = rail.Value;
            slider.Value = 95;
            assert(SystemParameters.ClientAreaAnimation ? Math.Abs(rail.Value - beforeReversal) < 0.01 : rail.Value == 95,
                "Rapid volume changes preserve continuity and honor the Windows animation preference");
            await Task.Delay(190);
            assert(Math.Abs(rail.Value - 95) < 0.01, "Interrupted volume animation settles at the latest target");

            slider.Value = 5;
            Application.Current.Resources[InteractionMotion.PreferenceKey] = false;
            assert(Math.Abs(rail.Value - 5) < 0.01,
                "Enabling reduced motion immediately finishes an active volume transition");
            slider.Value = 62.35;
            assert(Math.Abs(rail.Value - 62.35) < 0.01,
                "Reduced motion preserves precise fractional volume without an animation");

            Application.Current.Resources[InteractionMotion.PreferenceKey] = true;
            var progress = new ProgressBar
            {
                Minimum = 0, Maximum = 100, Value = 10,
                Style = (Style)notch.FindResource("NotchProgressBar")
            };
            window.Content = progress;
            window.UpdateLayout();
            progress.Value = 70;
            await Task.Delay(190);
            assert(Math.Abs(SmoothValue.GetDisplayValue(progress) - 70) < 0.01,
                "Volume HUD and progress bars share the smooth presentation behavior");
        }
        finally
        {
            window.Close();
            Application.Current.Resources[InteractionMotion.PreferenceKey] = previousMotion;
        }
    }
}
