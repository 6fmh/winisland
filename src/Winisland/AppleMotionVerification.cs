using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Winisland.Controls;
using Winisland.Services;

namespace Winisland;

// Exercise interrupted transitions in the existing private-desktop self-test.
// Numerical derivatives observe the installed easing timelines independently of
// SpringEase.VelocityAt; no production test hooks or physical input are needed.
internal static class AppleMotionVerification
{
    private static readonly FieldInfo SizeMotionField = typeof(NotchWindow).GetField(
        "_sizeMotion", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private sealed record Velocity(double Width, double Height, double Contour, double ClockSeconds);
    private sealed record Sample(string Stage, double FrameSeconds, double Width, double Height,
        double ActualWidth, double ActualHeight, double Contour, double Opacity, double Offset,
        double ScaleX, double ScaleY, bool ContentVisible, bool ContentAcceptsInput,
        bool SurfaceAnimated, bool ContentAnimated, bool OffsetAnimated, bool MotionPresent,
        Velocity Velocity);
    private sealed record Boundary(Sample Before, Sample After);

    internal static async Task RunAsync(App app, NotchWindow notch, SettingsStore store,
        MediaService media, string output, Action<bool, string> assert)
    {
        BackgroundVerification.RequirePrivateDesktop();
        if (!app.Dispatcher.CheckAccess()) throw new InvalidOperationException("Motion verification requires the UI dispatcher.");
        var savedSettings = store.Current;
        var savedMedia = media.Current;
        var wasExpanded = notch.IsExpanded;
        var wasPinned = notch.IsPinnedForTest;
        var savedTab = (string?)typeof(NotchWindow).GetField("_selectedTab",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(notch) ?? "music";
        var island = (FrameworkElement)notch.FindName("Island");
        var surface = (NotchSurface)notch.FindName("Surface");
        var content = (FrameworkElement)notch.FindName("ExpandedContent");
        var scale = (ScaleTransform)notch.FindName("ContentScale");
        var offset = (TranslateTransform)notch.FindName("ContentOffset");
        var samples = new List<Sample>();
        var frames = new RenderingFrames();
        var windowsAnimationEnabled = SystemParameters.ClientAreaAnimation;
        string? failure = null;
        try
        {
            store.Update(savedSettings with
            {
                ReducedMotion = true, HideInFullscreen = false, ExpandOnHover = false,
                ShowLiveActivity = false, ShowVolumeHud = false,
                NotchWidth = NotchMetrics.DefaultWidth, NotchHeight = NotchMetrics.DefaultHeight
            });
            media.SetSnapshotForTest(new MediaSnapshot());
            notch.SelectTabForTest("music");
            notch.Collapse();
            notch.UpdateLayout();
            var closed = Read("initial-reduced-closed");
            notch.Expand(false);
            var reducedOpen = Read("initial-reduced-open");
            assert(AtOpenTarget(reducedOpen) && !reducedOpen.SurfaceAnimated
                && !reducedOpen.ContentAnimated && !reducedOpen.OffsetAnimated,
                "Reduced motion opens at the final dimensions with native-size content and no transition clocks");
            notch.Collapse();
            assert(AtClosedTarget(Read("initial-reduced-collapse")),
                "Reduced motion closes immediately and removes content input");

            if (!windowsAnimationEnabled)
            {
                store.Update(store.Current with { ReducedMotion = false });
                notch.Expand(false);
                assert(AtOpenTarget(Read("windows-animation-disabled-open")),
                    "The Windows animation preference keeps opening immediate; rendered-motion sampling is skipped");
                return;
            }

            store.Update(store.Current with { ReducedMotion = false });
            notch.Expand(false);
            var pendingOpen = Read("same-frame-open");
            notch.Collapse();
            var pendingClose = Read("same-frame-reversal");
            assert(SamePosition(closed, pendingOpen) && SamePosition(closed, pendingClose),
                "An expand-collapse reversal in one dispatcher turn preserves the last presented surface");
            await frames.NextAsync(() => Read("same-frame-reversal-rendered"));
            assert(SamePosition(closed, Read("same-frame-reversal-after-render")),
                "A same-frame size reversal does not reveal an unrendered expansion target on the next frame");

            // Start each fixture from a settled value without waiting an assumed
            // number of milliseconds for an animation to become active.
            store.Update(store.Current with { ReducedMotion = true });
            notch.Collapse();
            store.Update(store.Current with { ReducedMotion = false });
            notch.Expand(false);
            var closing = await Retarget("interrupt-opening", sample =>
                InMiddle(sample) && sample.Opacity is > 0.05 and < 0.95
                && sample.Velocity.Width > 10, notch.Collapse);
            assert(SamePosition(closing.Before, closing.After),
                "Closing during a rendered opening preserves width, height and contour position");
            assert(SameVelocity(closing.Before.Velocity, closing.After.Velocity),
                "Closing during a rendered opening inherits all three presented spring velocities");
            assert(SameContent(closing.Before, closing.After) && !closing.After.ContentAcceptsInput,
                "Interrupted content dismissal preserves opacity and translation while yielding input immediately");
            Capture(notch, Path.Combine(output, "apple-motion-retarget.png"));

            var reopening = await Retarget("interrupt-closing", sample =>
                InMiddle(sample) && sample.Velocity.Width < -10, () => notch.Expand(false));
            assert(SamePosition(reopening.Before, reopening.After)
                && SameVelocity(reopening.Before.Velocity, reopening.After.Velocity),
                "Reopening during a rendered contraction preserves position and inward velocity");
            assert(SameContent(reopening.Before, reopening.After) && reopening.After.ContentAcceptsInput,
                "Reopening an interrupted dismissal keeps the presented content and restores input immediately");
            await Until("reopened-settling", sample => AtOpenTarget(sample) && !sample.MotionPresent
                && !sample.ContentAnimated && !sample.OffsetAnimated);
            Capture(notch, Path.Combine(output, "apple-motion-open.png"));

            var pinning = await Retarget("pin-settled-open", sample => AtOpenTarget(sample), () => notch.Expand(true));
            assert(notch.IsPinnedForTest && SamePosition(pinning.Before, pinning.After)
                && SameContent(pinning.Before, pinning.After) && !pinning.After.ContentAnimated
                && !pinning.After.OffsetAnimated && !pinning.After.MotionPresent,
                "Pinning a settled open panel does not replay its surface or content entrance");
            await frames.NextAsync(() => Read("pinned-next-frame"));
            assert(AtOpenTarget(Read("pinned-stable")),
                "An already-open panel stays fully visible after pinning");

            notch.Collapse();
            var reducedClosing = await Retarget("reduce-motion-during-close", sample =>
                InMiddle(sample) && sample.Velocity.Width < -10,
                () => store.Update(store.Current with { ReducedMotion = true }));
            assert(AtClosedTarget(reducedClosing.After) && !reducedClosing.After.MotionPresent
                && !reducedClosing.After.SurfaceAnimated && !reducedClosing.After.ContentAnimated
                && !reducedClosing.After.OffsetAnimated,
                "Enabling reduced motion during contraction settles surface and hidden content immediately");
            await frames.NextAsync(() => Read("reduced-close-next-frame"));
            assert(AtClosedTarget(Read("reduced-close-stable")),
                "Cancelled closing clocks cannot restore content after reduced motion settles it");

            store.Update(store.Current with { ReducedMotion = false });
            notch.Expand(false);
            var reducedOpening = await Retarget("reduce-motion-during-open", sample =>
                InMiddle(sample) && sample.Velocity.Width > 10,
                () => store.Update(store.Current with { ReducedMotion = true }));
            assert(AtOpenTarget(reducedOpening.After) && !reducedOpening.After.MotionPresent
                && !reducedOpening.After.SurfaceAnimated && !reducedOpening.After.ContentAnimated
                && !reducedOpening.After.OffsetAnimated,
                "Enabling reduced motion during expansion settles visible content and surface immediately");
            await frames.NextAsync(() => Read("reduced-open-next-frame"));
            assert(AtOpenTarget(Read("reduced-open-stable")),
                "Cancelled opening clocks cannot move the reduced-motion final state");
            assert(samples.TrueForAll(sample => Math.Abs(sample.ScaleX - 1) < 0.000001
                && Math.Abs(sample.ScaleY - 1) < 0.000001),
                "Expanded content keeps its native scale on every sampled opening, reversal, closing and pinning frame");
        }
        catch (Exception ex)
        {
            failure = ex.ToString();
            throw;
        }
        finally
        {
            try
            {
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "apple-motion-runtime.json"), JsonSerializer.Serialize(new
                {
                    passed = failure is null, windowsAnimationEnabled,
                    motionSamplingSkipped = !windowsAnimationEnabled,
                    privateDesktop = BackgroundVerification.CurrentDesktop,
                    failure, samples
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally
            {
                store.Update(store.Current with { ReducedMotion = true });
                notch.Collapse();
                media.SetSnapshotForTest(savedMedia);
                notch.SelectTabForTest(savedTab);
                store.Update(savedSettings);
                if (wasExpanded) notch.Expand(wasPinned);
            }
        }

        Sample Read(string stage)
        {
            var motion = SizeMotionField.GetValue(notch);
            var value = new Sample(stage, frames.LastFrame.TotalSeconds, island.Width, island.Height,
                island.ActualWidth, island.ActualHeight, surface.Expansion, content.Opacity, offset.Y,
                scale.ScaleX, scale.ScaleY, content.Visibility == Visibility.Visible, content.IsHitTestVisible,
                island.HasAnimatedProperties || surface.HasAnimatedProperties, content.HasAnimatedProperties,
                offset.HasAnimatedProperties, motion is not null, ReadVelocity(motion));
            samples.Add(value);
            return value;
        }

        bool InMiddle(Sample sample) => sample.Width > store.Current.NotchWidth
            + (NotchMetrics.ExpandedWidth - store.Current.NotchWidth) * 0.10
            && sample.Width < store.Current.NotchWidth
            + (NotchMetrics.ExpandedWidth - store.Current.NotchWidth) * 0.85;

        bool AtOpenTarget(Sample sample) => Math.Abs(sample.Width - NotchMetrics.ExpandedWidth) < 0.01
            && Math.Abs(sample.Height - NotchMetrics.ExpandedHeight) < 0.01 && Math.Abs(sample.Contour - 1) < 0.000001
            && sample.ContentVisible && sample.ContentAcceptsInput && sample.Opacity == 1 && sample.Offset == 0
            && sample.ScaleX == 1 && sample.ScaleY == 1;

        bool AtClosedTarget(Sample sample) => Math.Abs(sample.Width - store.Current.NotchWidth) < 0.01
            && Math.Abs(sample.Height - store.Current.NotchHeight) < 0.01 && sample.Contour == 0
            && !sample.ContentVisible && !sample.ContentAcceptsInput && sample.Opacity == 0 && sample.Offset == 0
            && sample.ScaleX == 1 && sample.ScaleY == 1;

        async Task<Sample> Until(string stage, Func<Sample, bool> condition)
        {
            var timeout = Stopwatch.StartNew();
            do
            {
                var sample = await frames.NextAsync(() => Read(stage));
                if (condition(sample)) return sample;
            }
            while (timeout.Elapsed < TimeSpan.FromSeconds(3));
            throw new InvalidOperationException($"Motion fixture did not reach {stage}; see apple-motion-runtime.json.");
        }

        async Task<Boundary> Retarget(string stage, Func<Sample, bool> condition, Action change)
        {
            var timeout = Stopwatch.StartNew();
            do
            {
                var boundary = await frames.NextAsync<Boundary?>(() =>
                {
                    var before = Read(stage + "-before");
                    if (!condition(before)) return null;
                    change();
                    return new Boundary(before, Read(stage + "-after"));
                });
                if (boundary is not null) return boundary;
            }
            while (timeout.Elapsed < TimeSpan.FromSeconds(3));
            throw new InvalidOperationException($"No rendered mid-transition frame reached {stage}; see apple-motion-runtime.json.");
        }
    }

    private static bool SamePosition(Sample before, Sample after) => Math.Abs(before.Width - after.Width) < 0.01
        && Math.Abs(before.Height - after.Height) < 0.01 && Math.Abs(before.Contour - after.Contour) < 0.000001;

    private static bool SameContent(Sample before, Sample after) => Math.Abs(before.Opacity - after.Opacity) < 0.000001
        && Math.Abs(before.Offset - after.Offset) < 0.000001;

    private static bool SameVelocity(Velocity before, Velocity after) => Close(before.Width, after.Width)
        && Close(before.Height, after.Height) && Close(before.Contour, after.Contour);

    private static bool Close(double before, double after) => double.IsFinite(before) && double.IsFinite(after)
        && Math.Abs(before - after) <= Math.Max(0.0001, Math.Abs(before) * 0.002);

    private static Velocity ReadVelocity(object? motion)
    {
        if (motion is null) return new Velocity(0, 0, 0, 0);
        var type = motion.GetType();
        object Read(string name) => type.GetProperty(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(motion)!;
        var clock = (AnimationClock)Read("Clock");
        var seconds = (double)Read("Seconds");
        var elapsed = clock.CurrentTime?.TotalSeconds ?? 0;
        var progress = Math.Clamp(elapsed / seconds, 0, 1);
        var widthEase = ((DoubleAnimation)clock.Timeline).EasingFunction!;
        double Derivative(IEasingFunction ease, double distance) => NumericalDerivative(ease, progress) * distance / seconds;
        return new Velocity(Derivative(widthEase, (double)Read("WidthDistance")),
            Derivative((IEasingFunction)Read("HeightEase"), (double)Read("HeightDistance")),
            Derivative((IEasingFunction)Read("ContourEase"), (double)Read("ContourDistance")), elapsed);
    }

    private static double NumericalDerivative(IEasingFunction ease, double time)
    {
        const double step = 0.0001;
        if (time >= 1) return 0;
        if (time <= step)
        {
            // A forward stencil measures the velocity of a newly installed clock
            // at its boundary without depending on the production derivative.
            return (-25 * ease.Ease(time) + 48 * ease.Ease(time + step)
                - 36 * ease.Ease(time + 2 * step) + 16 * ease.Ease(time + 3 * step)
                - 3 * ease.Ease(time + 4 * step)) / (12 * step);
        }
        var span = Math.Min(step, (1 - time) / 2);
        return (ease.Ease(time + span) - ease.Ease(time - span)) / (2 * span);
    }

    private static void Capture(NotchWindow notch, string path)
    {
        notch.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(notch.ActualWidth),
            (int)Math.Ceiling(notch.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(notch);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private sealed class RenderingFrames
    {
        internal TimeSpan LastFrame { get; private set; } = TimeSpan.Zero;

        internal async Task<T> NextAsync<T>(Func<T> sample)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler? handler = null;
            handler = (_, args) =>
            {
                var frame = ((RenderingEventArgs)args).RenderingTime;
                if (frame == LastFrame) return;
                LastFrame = frame;
                CompositionTarget.Rendering -= handler;
                try { completion.TrySetResult(sample()); }
                catch (Exception ex) { completion.TrySetException(ex); }
            };
            CompositionTarget.Rendering += handler;
            try
            {
                if (await Task.WhenAny(completion.Task, Task.Delay(5000)) != completion.Task)
                    throw new TimeoutException("Private-desktop motion verification did not receive a rendering frame.");
                return await completion.Task;
            }
            finally { CompositionTarget.Rendering -= handler; }
        }
    }
}
