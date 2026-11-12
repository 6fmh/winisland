using System;
using System.Collections.Generic;
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
        void Capture(string name, double scale = 1)
        {
            var target = new RenderTargetBitmap((int)Math.Ceiling(notch.ActualWidth * scale), (int)Math.Ceiling(notch.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            target.Render(notch);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target));
            using var file = File.Create(Path.Combine(output, name)); encoder.Save(file);
        }
        bool Inside(FrameworkElement outer, FrameworkElement inner)
        {
            var p = inner.TranslatePoint(new Point(0, 0), outer);
            return p.X >= 0 && p.Y >= 0 && p.X + inner.ActualWidth <= outer.ActualWidth + .5 && p.Y + inner.ActualHeight <= outer.ActualHeight + .5;
        }
        void CheckSpacing(string state)
        {
            var row = (FrameworkElement)banner.FindName("MessageContent");
            var top = row.TranslatePoint(new Point(), island).Y;
            var bottom = island.ActualHeight - top - row.ActualHeight;
            var dismiss = (Button)banner.FindName("DismissButton");
            var dismissCenter = dismiss.TranslatePoint(new Point(0, dismiss.ActualHeight / 2), island).Y;
            File.WriteAllText(Path.Combine(output, $"popup-spacing-layout-{state}.json"),
                System.Text.Json.JsonSerializer.Serialize(new { banner.ActualWidth, banner.ActualHeight, banner.ContentHeight,
                    rowWidth = row.ActualWidth, rowHeight = row.ActualHeight,
                    source = new { height = ((TextBlock)banner.FindName("SourceLabel")).ActualHeight, desired = ((TextBlock)banner.FindName("SourceLabel")).DesiredSize, position = ((TextBlock)banner.FindName("SourceLabel")).TranslatePoint(new Point(), row) },
                    title = new { height = ((TextBlock)banner.FindName("TitleLabel")).ActualHeight, desired = ((TextBlock)banner.FindName("TitleLabel")).DesiredSize, position = ((TextBlock)banner.FindName("TitleLabel")).TranslatePoint(new Point(), row) },
                    body = new { height = ((TextBlock)banner.FindName("BodyLabel")).ActualHeight, desired = ((TextBlock)banner.FindName("BodyLabel")).DesiredSize, position = ((TextBlock)banner.FindName("BodyLabel")).TranslatePoint(new Point(), row) } }));
            foreach (var name in new[] { "SourceLabel", "TitleLabel", "BodyLabel" })
            {
                var label = (TextBlock)banner.FindName(name);
                assert(label.Visibility == Visibility.Collapsed || Inside(row, label),
                    "Notification source, title and preview fit inside the measured content row without vertical clipping");
            }
            assert(Math.Abs(top - bottom) <= 1,
                "Notification visible content has equal space above and below the black surface");
            assert(Math.Abs(dismissCenter - top - row.ActualHeight / 2) <= 1,
                "The notification dismiss target centers with its artwork and message");
            File.WriteAllText(Path.Combine(output, $"popup-spacing-notification-{state}.json"),
                System.Text.Json.JsonSerializer.Serialize(new { top, bottom, contentHeight = row.ActualHeight, housingHeight = island.ActualHeight, dismissCenter }));
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

            foreach (var dynamic in new[] { false, true })
            {
                settings.Update(settings.Current with { UseDynamicIsland = dynamic, NotchWidth = 190, NotchHeight = 32 });
                notch.ShowNotification(first with { Icon = actualIcon });
                await Settle();
                foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
                    Capture($"popup-notification-{(dynamic ? "island" : "macbook")}-{scale * 100:0}.png", scale);
                assert(Inside(island, banner) && ((Button)banner.FindName("DismissButton")).ActualWidth == 44,
                    $"{(dynamic ? "Island" : "MacBook")} notification keeps readable content and a 44-DIP dismiss target");
                CheckSpacing($"{(dynamic ? "island" : "macbook")}-message");
                notch.ShowNotification(first with { Body = "" }); await Settle();
                assert(Math.Abs(banner.ActualHeight - 44) < .01 && ((TextBlock)banner.FindName("BodyLabel")).Visibility == Visibility.Collapsed && Inside(island, banner),
                    "A title-only notification removes the empty message row and contracts its housing");
                CheckSpacing($"{(dynamic ? "island" : "macbook")}-title-only");
                Capture($"popup-notification-{(dynamic ? "island" : "macbook")}-title-only.png");
                notch.ShowNotification(first); await Settle();
                File.WriteAllText(Path.Combine(output, $"popup-notification-{(dynamic ? "island" : "macbook")}-updated-layout.json"),
                    System.Text.Json.JsonSerializer.Serialize(new { banner.Height, banner.ActualHeight, banner.ContentHeight,
                        islandHeight = island.ActualHeight, activeId = notch.ActiveNotificationForTest?.Id,
                        queued = notch.QueuedNotificationsForTest, inside = Inside(island, banner) }));
                assert(banner.ActualHeight > 44 && Math.Abs(banner.ActualHeight - banner.ContentHeight) < .01 && notch.ActiveNotificationForTest?.Id == first.Id
                    && notch.QueuedNotificationsForTest == 0 && Inside(island, banner),
                    "Updated notification content recomputes its height without duplicating the event");
            }
            settings.Update(settings.Current with { UseDynamicIsland = false });

            notch.ShowNotification(first);
            assert(notch.QueuedNotificationsForTest == 0, "Repeated accessibility events do not duplicate an active notification");
            var next = first with { Id = "fixture-2", AppName = "Calendar", Title = "Team review", Body = "Starts in five minutes" };
            notch.ShowNotification(next);
            assert(notch.QueuedNotificationsForTest == 1 && notch.ActiveNotificationForTest?.Id == first.Id, "A notification burst queues without replacing the message being read");
            next = next with { Body = "The meeting moved to Room 2" };
            notch.ShowNotification(next);
            notch.ExpireNotificationForTest(); await Settle();
            assert(notch.ActiveNotificationForTest?.Id == next.Id && notch.ActiveNotificationForTest.Body == next.Body,
                "Timeout advances to the latest payload of the next recent queued notification");

            notch.Expand(true); await Settle();
            assert(notch.IsExpanded && notch.IsPinnedForTest && content.IsVisible && banner.IsVisible, "Pinned notch controls remain open when notifications are visible");
            var contentBottom = content.TranslatePoint(new Point(0, content.ActualHeight), island).Y;
            assert(banner.TranslatePoint(new Point(), island).Y >= contentBottom && Inside(island, banner), "Expanded notification footer does not overlap the current controls");
            notch.SelectTabForTest("controls"); await Settle();
            assert(Inside(island, banner) && notch.ActualHeight >= island.ActualHeight, "The tallest expanded panel and notification fit without overlay-window clipping");
            Capture("notification-expanded.png");
            notch.SelectTabForTest("music");
            notch.DismissNotificationForTest(true); notch.Collapse();

            foreach (var dynamic in new[] { false, true })
            {
                settings.Update(settings.Current with { UseDynamicIsland = dynamic });
                notch.ShowNotification(first with { Activate = () => { } });
                notch.SelectTabForTest("music");
                notch.Expand(true); await Settle();
                var entry = dynamic
                    ? (Control)((DynamicIslandPlayer)notch.FindName("DynamicPlayer")).FindName("MenuButton")
                    : (Control)notch.FindName("MusicTab");
                var footerOpen = (Button)banner.FindName("OpenButton");
                var footerDismiss = (Button)banner.FindName("DismissButton");
                assert(entry.Focus() && FocusMovesTo(footerOpen),
                    $"{(dynamic ? "Island" : "MacBook")} Tab navigation enters the notification action from the real expanded controls");
                assert(FocusMovesTo(footerDismiss) && FocusMovesTo(entry),
                    "Notification dismiss remains in the same keyboard cycle as the expanded controls");
                assert(entry.Focus() && FocusMovesTo(footerDismiss, FocusNavigationDirection.Previous),
                    "Reverse Tab reaches the notification footer from the expanded controls");
                footerDismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
                assert(notch.ActiveNotificationForTest is null && HasVisibleFocusWithin(island),
                    "Dismissing a keyboard-focused notification restores focus to visible enabled notch controls");
                assert(FocusMovesTo(entry),
                    "After notification dismissal, keyboard traversal wraps among the remaining visible controls");
                notch.ShowNotification(first with { Activate = () => { } });
                notch.ShowNotification(first with { Id = "keyboard-next", Title = "Next keyboard notification", Timestamp = DateTimeOffset.UtcNow, Activate = () => { } });
                await Settle();
                assert(entry.Focus() && FocusMovesTo(footerDismiss),
                    "Keyboard traversal reaches a notification while its next payload is queued");
                footerDismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
                assert(notch.ActiveNotificationForTest?.Id == "keyboard-next" && HasVisibleFocusWithin(island),
                    "Advancing a queued notification preserves focus on a visible enabled notch action");
                notch.DismissNotificationForTest(true);
                notch.ShowNotification(first with { Activate = () => { } }); await Settle();
                assert(entry.Focus(), "Main controls retain focus while a separate notification is visible");
                footerDismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
                assert(entry.IsKeyboardFocused, "Dismissing an unfocused notification does not move keyboard focus from the current control");
                var callbackObservedMainFocus = true;
                var callbackCount = 0;
                notch.ShowNotification(first with { Activate = () => { callbackCount++; callbackObservedMainFocus = entry.IsKeyboardFocused; entry.Focus(); } });
                await Settle();
                assert(entry.Focus() && FocusMovesTo(footerOpen), "Native Tab reaches the external notification activation action");
                footerOpen.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
                assert(callbackCount == 1 && !callbackObservedMainFocus && entry.IsKeyboardFocused,
                    "Notification opening yields focus to its activation callback without restoring main focus first or overriding callback focus later");
                notch.Collapse();
            }
            settings.Update(settings.Current with { UseDynamicIsland = false });

            var longMessage = first with { Id = "fixture-long", AppName = new string('A', 180), Title = new string('T', 220), Body = new string('B', 1500) };
            foreach (var dynamic in new[] { false, true })
            {
                settings.Update(settings.Current with { UseDynamicIsland = dynamic });
                notch.ShowNotification(longMessage); await Settle();
                var title = (TextBlock)banner.FindName("TitleLabel");
                var body = (TextBlock)banner.FindName("BodyLabel");
                assert(Inside(island, banner) && banner.ActualHeight >= 44 && banner.ActualHeight <= 100,
                    "Long notification text remains bounded and trims inside the banner");
                assert(title.ActualHeight <= 40 && body.ActualHeight <= 36 && Inside(banner, title) && Inside(banner, body)
                    && Equals(body.ToolTip, longMessage.Body) && AutomationProperties.GetName(banner).Contains(longMessage.Body),
                    "Long titles and previews wrap to two lines and preserve the complete message through tooltip and automation");
                CheckSpacing($"{(dynamic ? "island" : "macbook")}-long");
                foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
                    Capture($"popup-notification-{(dynamic ? "island" : "macbook")}-long-{scale * 100:0}.png", scale);
            }
            settings.Update(settings.Current with { UseDynamicIsland = false });
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
            var activationCount = 0;
            notch.ShowNotification(first with { Activate = () => activationCount++ }); await Settle();
            var open = (Button)banner.FindName("OpenButton");
            notch.Activate();
            assert(open.Focus() && open.IsKeyboardFocused, "An actionable notification accepts keyboard focus without taking it on arrival");
            await Settle();
            Capture("popup-notification-keyboard-focus.png");
            assert(HasBlueFocusPixels(open), "The notification open action renders a visible blue keyboard focus outline");
            await Task.Delay(6250);
            assert(notch.ActiveNotificationForTest is not null, "Keyboard focus pauses the actual notification timeout while its action is being read");
            assert(open.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)) && ((Button)banner.FindName("DismissButton")).IsKeyboardFocused,
                "Tab moves from opening a notification to its native dismiss control");
            await Settle();
            Capture("popup-notification-dismiss-focus.png");
            assert(HasBlueFocusPixels((Button)banner.FindName("DismissButton")), "The notification dismiss target renders a visible circular keyboard focus outline");
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
            assert(activationCount == 1 && notch.ActiveNotificationForTest is null, "Opening invokes the actual callback once and dismisses the notification");
            Keyboard.ClearFocus();
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

    internal static bool FocusMovesTo(IInputElement target, FocusNavigationDirection direction = FocusNavigationDirection.Next)
    {
        var visited = new HashSet<IInputElement>();
        for (var count = 0; count < 128; count++)
        {
            var focused = Keyboard.FocusedElement;
            if (focused is null || !visited.Add(focused)) return false;
            var request = new TraversalRequest(direction);
            var moved = focused switch
            {
                UIElement element => element.MoveFocus(request),
                ContentElement element => element.MoveFocus(request),
                _ => false
            };
            if (!moved) return false;
            if (ReferenceEquals(Keyboard.FocusedElement, target)) return true;
        }
        return false;
    }

    internal static bool HasVisibleFocusWithin(FrameworkElement ancestor)
    {
        if (Keyboard.FocusedElement is not UIElement { IsVisible: true, IsEnabled: true } focused) return false;
        for (DependencyObject? node = focused; node is not null;)
        {
            if (ReferenceEquals(node, ancestor)) return true;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    internal static bool HasBlueFocusPixels(FrameworkElement element)
    {
        var frame = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.Fill }, null, new Rect(element.RenderSize));
        frame.Render(visual);
        var pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4];
        frame.CopyPixels(pixels, frame.PixelWidth * 4, 0);
        var blue = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i] > 150 && pixels[i + 1] > 60 && pixels[i + 2] < 60 && pixels[i + 3] > 150) blue++;
        return blue >= 20;
    }
}
