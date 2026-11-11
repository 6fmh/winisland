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
    private const double NotificationFooterHeight = 112;
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
            DismissNotification();
            try { activate?.Invoke(); }
            catch (Exception ex) { Log.Error("Open notification", ex); }
        };
        _notificationBanner.MouseEnter += (_, _) => _notificationTimer.Stop();
        _notificationBanner.MouseLeave += (_, _) => { if (_activeNotification is not null) _notificationTimer.Start(); };
        _notificationTimer.Tick += (_, _) => DismissNotification();
    }

    public void ShowNotification(NotchNotification notification)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => ShowNotification(notification)); return; }
        if (_closed || !_settings.Current.ShowNotifications || _fullscreenHidden) return;
        if (_activeNotification?.Id == notification.Id)
        {
            _activeNotification = notification;
            _notificationBanner.Present(notification);
            return;
        }
        if (_notificationQueue.Any(item => item.Id == notification.Id)) return;
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
        _notificationTimer.Stop();
        if (!_notificationBanner.IsMouseOver) _notificationTimer.Start();
    }

    private void UpdateNotificationPresentation(bool entering = false)
    {
        if (_activeNotification is null) return;
        CompactActivity.Visibility = Hud.Visibility = DeviceConnection.Visibility = Visibility.Collapsed;
        _notificationBanner.Margin = new Thickness(30, _expanded ? BaseExpandedPanelHeight + 8 : _settings.Current.NotchHeight + 8, 30, 16);
        _notificationBanner.Height = 88;
        Height = Math.Max(384, (_expanded ? ExpandedPanelHeight : _settings.Current.NotchHeight + NotificationFooterHeight) + NotchMetrics.ShadowPadding);
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
        AnimateSize(_expanded ? NotchMetrics.ExpandedWidth : Math.Max(420, _settings.Current.NotchWidth + 100),
            _expanded ? ExpandedPanelHeight : _settings.Current.NotchHeight + NotificationFooterHeight);
        UpdateVisualizer();
    }

    private void DismissNotification(bool clearQueue = false)
    {
        _notificationTimer.Stop();
        _activeNotification = null;
        ++_notificationTransitionVersion;
        _notificationBanner.BeginAnimation(OpacityProperty, null);
        _notificationBanner.Visibility = Visibility.Collapsed;
        Height = 384;
        if (clearQueue) _notificationQueue.Clear();
        // Discard old burst entries rather than replaying stale notifications.
        while (_notificationQueue.TryDequeue(out var next))
        {
            if (DateTimeOffset.UtcNow - next.Timestamp > TimeSpan.FromSeconds(30)) continue;
            ShowNotification(next);
            return;
        }
        if (_expanded) AnimateSize(NotchMetrics.ExpandedWidth, ExpandedPanelHeight);
        else UpdateCompact();
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
