using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Winisland.Controls;
using Winisland.Models;
using Winisland.Services;

namespace Winisland;

public partial class NotchWindow
{
    private const double PopupFooterPadding = 16;
    private double ClosedPopupPadding => ClosedNotchHeight + 8;
    private double NotificationFooterHeight => _notificationBanner.ContentHeight + 2 * PopupFooterPadding;
    private double ClosedNotificationHeight => _notificationBanner.ContentHeight + 2 * ClosedPopupPadding;
    private readonly Queue<NotchNotification> _notificationQueue = new();
    private readonly DispatcherTimer _notificationTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private NotificationBanner _notificationBanner = null!;
    private NotchNotification? _activeNotification;
    private int _notificationTransitionVersion;

    private void InitializeNotifications()
    {
        _notificationBanner = new NotificationBanner { Name = "NotificationBanner", Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Top };
        Island.Children.Add(_notificationBanner);
        RegisterName("NotificationBanner", _notificationBanner);
        _notificationBanner.DismissRequested += (_, _) => DismissNotification();
        _notificationBanner.OpenRequested += (_, _) =>
        {
            var activate = _activeNotification?.Activate;
            DismissNotification(restoreKeyboardFocus: false);
            try { activate?.Invoke(); }
            catch (Exception ex) { Log.Error("Open notification", ex); }
        };
        _notificationBanner.MouseEnter += (_, _) => _notificationTimer.Stop();
        _notificationBanner.MouseLeave += (_, _) => ResumeNotificationTimer();
        _notificationBanner.IsKeyboardFocusWithinChanged += (_, _) => ResumeNotificationTimer();
        _notificationTimer.Tick += (_, _) =>
        {
            if (_pointerInside || _notificationBanner.IsMouseOver || _notificationBanner.IsKeyboardFocusWithin) ResumeNotificationTimer();
            else DismissNotification();
        };
    }

    private void ResumeNotificationTimer()
    {
        _notificationTimer.Stop();
        if (_activeNotification is not null && !_pointerInside && !_notificationBanner.IsMouseOver && !_notificationBanner.IsKeyboardFocusWithin)
            _notificationTimer.Start();
    }

    public void ShowNotification(NotchNotification notification)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => ShowNotification(notification)); return; }
        if (_closed || !_settings.Current.ShowNotifications || _fullscreenHidden) return;
        if (_activeNotification?.Id == notification.Id)
        {
            _activeNotification = notification;
            _notificationBanner.Present(notification);
            UpdateNotificationPresentation();
            ResumeNotificationTimer();
            return;
        }
        if (_notificationQueue.Any(item => item.Id == notification.Id))
        {
            var pending = _notificationQueue.Select(item => item.Id == notification.Id ? notification : item).ToArray();
            _notificationQueue.Clear();
            foreach (var item in pending) _notificationQueue.Enqueue(item);
            return;
        }
        if (_activeNotification is not null)
        {
            // Keep a small burst in memory; never save notification contents.
            if (_notificationQueue.Count == 5) _notificationQueue.Dequeue();
            _notificationQueue.Enqueue(notification);
            return;
        }
        _activeNotification = notification;
        _hudTimer.Stop(); _showingHud = false;
        _notificationBanner.Present(notification);
        UpdateNotificationPresentation(true);
        var peer = UIElementAutomationPeer.FromElement(_notificationBanner) ?? UIElementAutomationPeer.CreatePeerForElement(_notificationBanner);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        ResumeNotificationTimer();
    }

    private void UpdateNotificationPresentation(bool entering = false)
    {
        if (_activeNotification is null) return;
        SetMinimalActivity(false);
        _deviceTimer.Stop();
        DeviceHeaderStatus.Visibility = Visibility.Collapsed;
        CompactActivity.Visibility = Hud.Visibility = DeviceConnection.Visibility = Visibility.Collapsed;
        var inset = IsDynamicIsland ? DynamicIslandMetrics.ContentInset : 30;
        var width = _expanded ? ExpandedPanelWidth : IsDynamicIsland ? DynamicIslandMetrics.ExpandedWidth : Math.Max(420, _settings.Current.NotchWidth + 100);
        _notificationBanner.MeasureContent(width - 2 * inset);
        var padding = _expanded ? PopupFooterPadding : ClosedPopupPadding;
        var closedHeight = ClosedNotificationHeight;
        _notificationBanner.Margin = new Thickness(inset, (_expanded ? BaseExpandedPanelHeight : 0) + padding, inset, padding);
        Height = Math.Max(384, (_expanded ? ExpandedPanelHeight : closedHeight) + AppearanceTopInset + NotchMetrics.ShadowPadding);
        _notificationBanner.Visibility = Visibility.Visible;
        var version = ++_notificationTransitionVersion;
        if (entering && MotionEnabled)
        {
            var fade = new DoubleAnimation(0, 1, DurationFor(220)) { FillBehavior = FillBehavior.Stop };
            fade.Completed += (_, _) =>
            {
                if (version != _notificationTransitionVersion) return;
                _notificationBanner.BeginAnimation(OpacityProperty, null);
                _notificationBanner.Opacity = 1;
            };
            _notificationBanner.BeginAnimation(OpacityProperty, fade);
        }
        else { _notificationBanner.BeginAnimation(OpacityProperty, null); _notificationBanner.Opacity = 1; }
        AnimateSize(width,
            _expanded ? ExpandedPanelHeight : closedHeight);
        UpdateVisualizer();
    }

    private void DismissNotification(bool clearQueue = false, bool restoreKeyboardFocus = true)
    {
        var restoreFocus = restoreKeyboardFocus && _expanded && !_fullscreenHidden && IsActive
            && _notificationBanner.IsKeyboardFocusWithin;
        _notificationTimer.Stop();
        _activeNotification = null;
        ++_notificationTransitionVersion;
        _notificationBanner.BeginAnimation(OpacityProperty, null);
        _notificationBanner.Visibility = Visibility.Collapsed;
        UpdateExpandedLayout();
        if (clearQueue) _notificationQueue.Clear();
        // Discard old burst entries rather than replaying stale notifications.
        while (_notificationQueue.TryDequeue(out var next))
        {
            if (DateTimeOffset.UtcNow - next.Timestamp > TimeSpan.FromSeconds(30)) continue;
            ShowNotification(next);
            if (restoreFocus && _expanded && !_fullscreenHidden && IsActive)
            {
                var open = (Button)_notificationBanner.FindName("OpenButton");
                var dismiss = (Button)_notificationBanner.FindName("DismissButton");
                (_notificationBanner.IsVisible ? open.IsEnabled ? open : dismiss : SelectedTabControl).Focus();
            }
            return;
        }
        if (_expanded) AnimateSize(ExpandedPanelWidth, ExpandedPanelHeight);
        else UpdateCompact();
        if (_deviceConnection is not null) UpdateDevicePresentation();
        if (restoreFocus && _expanded && !_fullscreenHidden && IsActive) SelectedTabControl.Focus();
    }

    private void OnNotificationKeyDown(KeyEventArgs args)
    {
        if (args.Key != Key.Escape || _activeNotification is null) return;
        DismissNotification(); args.Handled = true;
    }

    internal NotchNotification? ActiveNotificationForTest => _activeNotification;
    internal int QueuedNotificationsForTest => _notificationQueue.Count;
    internal void DismissNotificationForTest(bool clear = false) => DismissNotification(clear);
    internal void ExpireNotificationForTest() => DismissNotification();
}
