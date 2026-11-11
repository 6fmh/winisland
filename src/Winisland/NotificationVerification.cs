using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Winisland.Controls;
using Winisland.Models;
using Winisland.Services;

namespace Winisland;

internal static class NotificationVerification
{
    internal static async Task RunAsync(NotchWindow notch, SettingsStore settings, string output, Action<bool, string> assert)
    {
        BackgroundVerification.RequirePrivateDesktop();
        var original = settings.Current;
        var first = new NotchNotification("fixture-1", "Messages", "Alex", "The design looks good. Ready to share it?", DateTimeOffset.UtcNow);
        var banner = (NotificationBanner)notch.FindName("NotificationBanner");
        var island = (FrameworkElement)notch.FindName("Island");
        var content = (FrameworkElement)notch.FindName("ExpandedContent");
        async Task Settle() { await Task.Delay(750); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); notch.UpdateLayout(); }
        void Capture(string name)
        {
            var target = new RenderTargetBitmap((int)Math.Ceiling(notch.ActualWidth), (int)Math.Ceiling(notch.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            target.Render(notch);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target));
            using var file = File.Create(Path.Combine(output, name)); encoder.Save(file);
        }
        bool Inside(FrameworkElement outer, FrameworkElement inner)
        {
            var p = inner.TranslatePoint(new Point(0, 0), outer);
            return p.X >= 0 && p.Y >= 0 && p.X + inner.ActualWidth <= outer.ActualWidth + .5 && p.Y + inner.ActualHeight <= outer.ActualHeight + .5;
        }
        try
        {
            settings.Update(original with { ShowNotifications = true, ReducedMotion = true, HideInFullscreen = false });
            notch.Collapse(); notch.ShowNotification(first); await Settle();
            assert(notch.ActiveNotificationForTest?.Id == first.Id && banner.IsVisible, "An incoming notification renders in the actual notch banner");
            assert(Inside(island, banner), "Notification icon, title and body container fit inside the black notch");
            assert(banner.TranslatePoint(new Point(), island).Y >= settings.Current.NotchHeight, "Notification content stays below the fixed MacBook camera area");
            assert(AutomationProperties.GetName(banner).Contains(first.Body), "Notification accessible announcement includes source, title and body");
            Capture("notification-message.png");
            var local = App.CreateLocalNotification("Your file is ready to share.");
            var actualIcon = local.Icon;
            notch.ShowNotification(local with { Id = first.Id, Title = "Upload complete" }); await Settle();
            assert(actualIcon is { IsFrozen: true } && ((Image)banner.FindName("AppIcon")).Source == actualIcon,
                "The visible notification leading icon renders actual frozen application artwork");
            assert(ReferenceEquals(App.CreateLocalNotification("Another local message").Icon, actualIcon),
                "Winisland's local notifications reuse its real bundled app icon");
            Capture("notification-app-icon.png");

            notch.ShowNotification(first);
            assert(notch.QueuedNotificationsForTest == 0, "Repeated accessibility events do not duplicate an active notification");
            var next = first with { Id = "fixture-2", AppName = "Calendar", Title = "Team review", Body = "Starts in five minutes" };
            notch.ShowNotification(next);
            assert(notch.QueuedNotificationsForTest == 1 && notch.ActiveNotificationForTest?.Id == first.Id, "A notification burst queues without replacing the message being read");
            notch.ExpireNotificationForTest(); await Settle();
            assert(notch.ActiveNotificationForTest?.Id == next.Id, "Timeout advances to the next recent queued notification");

            notch.Expand(true); await Settle();
            assert(notch.IsExpanded && notch.IsPinnedForTest && content.IsVisible && banner.IsVisible, "Pinned notch controls remain open when notifications are visible");
            var contentBottom = content.TranslatePoint(new Point(0, content.ActualHeight), island).Y;
            assert(banner.TranslatePoint(new Point(), island).Y >= contentBottom && Inside(island, banner), "Expanded notification footer does not overlap the current controls");
            notch.SelectTabForTest("controls"); await Settle();
            assert(Inside(island, banner) && notch.ActualHeight >= island.ActualHeight, "The tallest expanded panel and notification fit without overlay-window clipping");
            Capture("notification-expanded.png");
            notch.SelectTabForTest("music");
            notch.DismissNotificationForTest(true); notch.Collapse();

            var longMessage = first with { Id = "fixture-long", AppName = new string('A', 180), Title = new string('T', 220), Body = new string('B', 1500) };
            notch.ShowNotification(longMessage); await Settle();
            assert(Inside(island, banner) && banner.ActualHeight == 88, "Long notification text remains bounded and trims inside the banner");
            Capture("notification-long-text.png");
            for (var index = 0; index < 9; index++) notch.ShowNotification(first with { Id = "burst-" + index });
            assert(notch.QueuedNotificationsForTest == 5, "Notification burst memory is bounded to five queued entries");
            settings.Update(settings.Current with { ShowNotifications = false }); await Settle();
            notch.ShowNotification(first);
            assert(notch.ActiveNotificationForTest is null && notch.QueuedNotificationsForTest == 0 && !banner.IsVisible, "Disabling notifications clears transient content and suppresses arrivals");
            settings.Update(settings.Current with { ShowNotifications = true });
            notch.ShowNotification(first);
            ((Button)banner.FindName("DismissButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
            assert(notch.ActiveNotificationForTest is null && !banner.IsVisible, "The actual dismiss button restores the compact notch");
            assert(notch.Height == 384, "Dismissal restores the normal overlay window height");
            settings.Update(settings.Current with { ReducedMotion = false });
            notch.ShowNotification(first); await Settle();
            assert(banner.Opacity == 1 && !banner.HasAnimatedProperties && Inside(island, banner), "Notification motion settles at native text size with no retained opacity animation");
            Capture("notification-motion.png");
            // Exercise actual hover handlers and the real six-second timer.
            banner.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent });
            await Task.Delay(6250);
            assert(notch.ActiveNotificationForTest is not null, "Hover pauses the actual notification timeout while the message is being read");
            banner.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseLeaveEvent });
            await Task.Delay(6250);
            assert(notch.ActiveNotificationForTest is null, "Leaving the banner resumes its actual six-second timeout");

            var fullscreen = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false };
            try
            {
                fullscreen.Show();
                var hwnd = new WindowInteropHelper(fullscreen).Handle;
                var bounds = NativeMethods.ResolveScreen(settings.Current.MonitorDevice).Bounds;
                NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0010);
                notch.SetForegroundWindowForTest(hwnd);
                settings.Update(settings.Current with { HideInFullscreen = true });
                await Settle();
                notch.ShowNotification(first);
                assert(notch.ActiveNotificationForTest is null && !banner.IsVisible, "Fullscreen hiding suppresses incoming notifications without retaining them for replay");
            }
            finally
            {
                notch.SetForegroundWindowForTest(IntPtr.Zero);
                fullscreen.Close();
                settings.Update(settings.Current with { HideInFullscreen = false });
            }
        }
        finally { notch.DismissNotificationForTest(true); notch.Collapse(); settings.Update(original); }
    }
}
