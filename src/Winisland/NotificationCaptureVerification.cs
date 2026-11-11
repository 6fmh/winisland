using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Winisland.Models;
using Winisland.Services;

namespace Winisland;

internal static class NotificationCaptureVerification
{
    internal static async Task RunAsync(Action<bool, string> assert, Action<NotchNotification>? present = null)
    {
        BackgroundVerification.RequirePrivateDesktop();
        await NotificationIconVerification.RunAsync(assert);
        var unrelated = new[] { new WindowsNotificationService.NotificationText("QuickSettings", "", "Settings", true) };
        assert(WindowsNotificationService.Parse(unrelated, "fixture") is null,
            "Notification capture rejects unrelated shell flyouts and notification history");
        var fields = new[]
        {
            new WindowsNotificationService.NotificationText("ToastView", "", "", false),
            new WindowsNotificationService.NotificationText("AppNameText", "", "Messages", true),
            new WindowsNotificationService.NotificationText("ToastTextField1", "", "Alex", true),
            new WindowsNotificationService.NotificationText("ToastTextField2", "", "See you at six.\0", true)
        };
        var parsed = WindowsNotificationService.Parse(fields, "fixture");
        assert(parsed is { AppName: "Messages", Title: "Alex", Body: "See you at six.", Id: "fixture" },
            "Notification parser separates source, title and preview and removes control characters");
        var history = new[] { new WindowsNotificationService.NotificationText("NotificationCenter", "", "", false) }.Concat(fields).ToArray();
        assert(WindowsNotificationService.Parse(history, "fixture") is null,
            "Opening Windows Notification Center cannot replay its toast history onto the notch");
        var currentShell = fields.Select(node => node.Id == "ToastView" ? node with { Id = "", Class = "ActionCenter.FlexibleToastView" } : node).ToArray();
        assert(WindowsNotificationService.Parse(currentShell, "fixture") is { Title: "Alex" },
            "Notification admission recognizes the current deployed shell's FlexibleToastView accessibility class");
        var namedHistory = new[] { new WindowsNotificationService.NotificationText("", "", "Notification Center", false) }.Concat(fields).ToArray();
        assert(WindowsNotificationService.Parse(namedHistory, "fixture") is null,
            "A notification center root with only its accessible name cannot replay history");
        var numeric = fields.Select(node => node.Id == "ToastTextField1" ? node with { Id = "1" }
            : node.Id == "ToastTextField2" ? node with { Id = "2" } : node).ToArray();
        assert(WindowsNotificationService.Parse(numeric, "fixture") is { Title: "Alex", Body: "See you at six." },
            "Notification parser supports numeric accessibility text field identifiers");
        var oversized = fields.Select(node => node.Id == "ToastTextField2" ? node with { Name = new string('x', 5000) } : node).ToArray();
        assert(WindowsNotificationService.Parse(oversized, "fixture")?.Body.Length == 600,
            "Notification capture bounds transient content from accessibility providers");

        var source = new TextBlock { Text = "Messages" };
        var title = new TextBlock { Text = "Alex" };
        var body = new TextBlock { Text = "See you at six." };
        var hidden = new TextBlock { Text = "Hidden history must not be shown.", Visibility = Visibility.Hidden };
        AutomationProperties.SetAutomationId(source, "AppNameText");
        AutomationProperties.SetAutomationId(title, "ToastTextField1");
        AutomationProperties.SetAutomationId(body, "ToastTextField2");
        AutomationProperties.SetAutomationId(hidden, "ToastTextField3");
        var panel = new StackPanel(); panel.Children.Add(source); panel.Children.Add(title); panel.Children.Add(body); panel.Children.Add(hidden);
        var fixture = new Window
        {
            Title = "Winisland private notification fixture", Width = 320, Height = 120,
            Left = 40, Top = 260, ShowInTaskbar = false, ShowActivated = false, Content = panel
        };
        AutomationProperties.SetAutomationId(fixture, "ToastView");
        IntPtr handle = IntPtr.Zero;
        fixture.SourceInitialized += (_, _) => handle = new WindowInteropHelper(fixture).Handle;
        using var capture = new WindowsNotificationService(fixture.Dispatcher, candidate => candidate != IntPtr.Zero && candidate == handle);
        var first = new TaskCompletionSource<NotchNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = new TaskCompletionSource<NotchNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        capture.Received += (_, notification) =>
        {
            count++; present?.Invoke(notification);
            if (count == 1) first.TrySetResult(notification); else next.TrySetResult(notification);
        };
        try
        {
            capture.Start();
            assert(capture.IsListening, "Notification capture registers native event hooks on the private desktop");
            fixture.Show();
            var completed = await Task.WhenAny(first.Task, Task.Delay(3000));
            assert(completed == first.Task, "A real native window event captures its accessible notification tree without polling the desktop");
            if (completed != first.Task) return;
            var received = await first.Task;
            assert(received is { AppName: "Messages", Title: "Alex", Body: "See you at six." },
                "Captured native-event notification preserves app, sender and preview for the notch");
            assert(!received.Body.Contains("Hidden history", StringComparison.Ordinal),
                "Offscreen accessibility descendants cannot contribute hidden notification content");
            fixture.Hide();
            capture.ObserveWindowForTest(handle);
            await Task.Delay(250);
            assert(count == 1, "Offscreen notification trees are not captured after their native banner is hidden");
            fixture.Show();
            await Task.Delay(400);
            // Showing the fixture again is a new native visible notification.
            var shownCount = count;
            capture.ObserveWindowForTest(handle);
            await Task.Delay(500);
            assert(count == shownCount, "Repeated accessibility events do not replay the same visible notification");
            next = new TaskCompletionSource<NotchNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
            capture.Stop();
            assert(!capture.IsListening, "Notification preference stop removes native hooks without disposing the service");
            capture.Start();
            assert(capture.IsListening, "Notification capture restarts when its preference is enabled again");
            title.Text = "Sam"; body.Text = "A second notification.";
            fixture.Title = "Winisland private notification fixture updated";
            completed = await Task.WhenAny(next.Task, Task.Delay(2500));
            assert(completed == next.Task && next.Task.Result.Title == "Sam" && next.Task.Result.Body == "A second notification.",
                "A native name change reads updated notification content from the same shell window");
            capture.Dispose();
            var beforeDispose = count;
            capture.ObserveWindowForTest(handle);
            await Task.Delay(150);
            assert(!capture.IsListening && count == beforeDispose,
                "Disabling notification capture removes hooks and suppresses delayed delivery");
            using var productionFilter = new WindowsNotificationService(fixture.Dispatcher);
            var unrelatedCount = 0;
            productionFilter.Received += (_, _) => unrelatedCount++;
            productionFilter.Start();
            productionFilter.ObserveWindowForTest(handle);
            fixture.Title = "Winisland ordinary app window remains unrelated";
            await Task.Delay(250);
            assert(unrelatedCount == 0,
                "Production HWND class and process filtering rejects ordinary application windows even with toast-like content");
        }
        finally { capture.Dispose(); fixture.Close(); }
        await VerifyLargeTransparentHostAsync(assert);
        await VerifyExplicitActivationAsync(assert);
    }

    private static async Task VerifyLargeTransparentHostAsync(Action<bool, string> assert)
    {
        BackgroundVerification.RequirePrivateDesktop();
        var source = new TextBlock { Text = "Messages" };
        var title = new TextBlock { Text = "Large transparent shell host" };
        var body = new TextBlock { Text = "Only the small visible toast is notification content." };
        AutomationProperties.SetAutomationId(source, "AppNameText");
        AutomationProperties.SetAutomationId(title, "ToastTextField1");
        AutomationProperties.SetAutomationId(body, "ToastTextField2");
        var content = new StackPanel(); content.Children.Add(source); content.Children.Add(title); content.Children.Add(body);
        var toast = new GroupBox
        {
            Width = 320, Height = 120, Margin = new Thickness(40, 260, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.White, Content = content
        };
        AutomationProperties.SetAutomationId(toast, "ToastView");
        var host = new Grid(); host.Children.Add(toast);
        var fixture = new Window
        {
            Title = "Private monitor-sized transparent toast host", Width = 2400, Height = 1400,
            MinWidth = 2400, MinHeight = 1400,
            Left = 0, Top = 0, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, ShowActivated = false, Content = host
        };
        AutomationProperties.SetAutomationId(fixture, "ToastCenterPage");
        IntPtr handle = IntPtr.Zero;
        fixture.SourceInitialized += (_, _) => handle = new WindowInteropHelper(fixture).Handle;
        using var capture = new WindowsNotificationService(fixture.Dispatcher, candidate => candidate != IntPtr.Zero && candidate == handle);
        using var productionFilter = new WindowsNotificationService(fixture.Dispatcher);
        var received = new TaskCompletionSource<NotchNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ordinaryCount = 0;
        capture.Received += (_, notification) => received.TrySetResult(notification);
        productionFilter.Received += (_, _) => ordinaryCount++;
        try
        {
            capture.Start(); productionFilter.Start(); fixture.Show();
            // WPF constrains the initial window to the runner's display size.
            // Resize only this private HWND so the oversized-host regression
            // also exercises its real geometry on small CI monitors.
            var dpi = VisualTreeHelper.GetDpi(fixture);
            var resized = NativeMethods.SetWindowPos(handle, IntPtr.Zero, 0, 0,
                (int)Math.Ceiling(2400 * dpi.DpiScaleX), (int)Math.Ceiling(1400 * dpi.DpiScaleY),
                0x0010 | 0x0004); // SWP_NOACTIVATE | SWP_NOZORDER
            fixture.UpdateLayout();
            var completed = await Task.WhenAny(received.Task, Task.Delay(3000));
            // Window.ActualWidth/Height can still describe monitor-constrained
            // WPF layout. The capture service reads the real native UIA bounds.
            var bounds = AutomationElement.FromHandle(handle).Current.BoundingRectangle;
            assert(resized && bounds.Width > 1600 && bounds.Height > 1000 && toast.ActualWidth == 320 && toast.ActualHeight == 120,
                $"The private fixture uses a monitor-sized transparent host around a small visible toast card (native {bounds.Width}x{bounds.Height}, layout {fixture.ActualWidth}x{fixture.ActualHeight}, card {toast.ActualWidth}x{toast.ActualHeight})");
            assert(completed == received.Task && received.Task.Result.Title == title.Text && received.Task.Result.Body == body.Text,
                "A large transparent native shell host captures its small visible toast without applying card-size upper bounds to the host");
            assert(ordinaryCount == 0,
                "Large transparent app windows remain rejected by the production shell class and process classifier");
        }
        finally { capture.Dispose(); productionFilter.Dispose(); fixture.Close(); }
    }

    private static async Task VerifyExplicitActivationAsync(Action<bool, string> assert)
    {
        BackgroundVerification.RequirePrivateDesktop();
        var source = new TextBlock { Text = "Messages" };
        var title = new TextBlock { Text = "Alex" };
        var body = new TextBlock { Text = "Only this fixture counter may be invoked." };
        AutomationProperties.SetAutomationId(source, "AppNameText");
        AutomationProperties.SetAutomationId(title, "ToastTextField1");
        AutomationProperties.SetAutomationId(body, "ToastTextField2");
        var content = new StackPanel(); content.Children.Add(source); content.Children.Add(title); content.Children.Add(body);
        var toast = new Button { Content = content, Padding = new Thickness(8) };
        AutomationProperties.SetAutomationId(toast, "ToastView");
        var fixture = new Window
        {
            Title = "Private supported notification activation", Width = 360, Height = 160,
            Left = 40, Top = 400, ShowInTaskbar = false, ShowActivated = false, Content = toast
        };
        IntPtr handle = IntPtr.Zero;
        fixture.SourceInitialized += (_, _) => handle = new WindowInteropHelper(fixture).Handle;
        using var capture = new WindowsNotificationService(fixture.Dispatcher, candidate => candidate != IntPtr.Zero && candidate == handle);
        var first = new TaskCompletionSource<NotchNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacement = new TaskCompletionSource<NotchNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoked = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocationCount = 0;
        var capturedCount = 0;
        toast.Click += (_, _) => { invocationCount++; invoked.TrySetResult(invocationCount); };
        capture.Received += (_, notification) =>
        {
            if (++capturedCount == 1) first.TrySetResult(notification);
            else replacement.TrySetResult(notification);
        };
        try
        {
            capture.Start(); fixture.Show();
            var completed = await Task.WhenAny(first.Task, Task.Delay(3000));
            assert(completed == first.Task && first.Task.Result.Activate is not null,
                "A supported native-event toast exposes its real UIA InvokePattern as an explicit notification action");
            if (completed != first.Task || first.Task.Result.Activate is not { } originalAction) return;
            originalAction();
            completed = await Task.WhenAny(invoked.Task, Task.Delay(3000));
            await Task.Delay(150);
            assert(completed == invoked.Task && invocationCount == 1,
                "Explicit notification activation invokes the matching private toast exactly once without input injection or opening an app");

            var originalHandle = handle;
            title.Text = "Jordan"; body.Text = "Replacement notification on the same HWND.";
            fixture.Title = "Private replacement notification activation";
            completed = await Task.WhenAny(replacement.Task, Task.Delay(2500));
            assert(completed == replacement.Task && handle == originalHandle && replacement.Task.Result.Title == "Jordan",
                "The activation fixture replaces notification content while retaining the original native HWND");
            if (completed != replacement.Task) return;
            originalAction();
            await Task.Delay(500);
            assert(invocationCount == 1,
                "A stale notification action cannot invoke replacement content on a reused shell HWND");

            invoked = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            replacement.Task.Result.Activate?.Invoke();
            completed = await Task.WhenAny(invoked.Task, Task.Delay(2500));
            assert(completed == invoked.Task && invocationCount == 2,
                "The replacement notification's matching explicit action remains usable after stale action rejection");
            capture.Dispose();
            replacement.Task.Result.Activate?.Invoke();
            await Task.Delay(200);
            assert(invocationCount == 2,
                "Disposed notification capture suppresses retained explicit activation actions");
        }
        finally { capture.Dispose(); fixture.Close(); }
    }
}
