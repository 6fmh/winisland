using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Winisland.Controls;
using Winisland.Services;

namespace Winisland;

public partial class NotchWindow
{
    internal const double AppleConnectionMinimumWidth = 420;
    private const double AppleConnectionRowHeight = 64;
    internal const double AppleConnectionContentHeight = AppleConnectionRowHeight + 2 * PopupFooterPadding;
    private AppleDeviceService? _appleDevices;
    private AppleDeviceConnection? _deviceConnection;
    private readonly Queue<AppleDeviceConnection> _deviceQueue = new();
    private readonly DispatcherTimer _deviceTimer = new() { Interval = TimeSpan.FromSeconds(4.5) };
    private int _deviceTransitionVersion;

    private void InitializeAppleDevices()
    {
        _deviceTimer.Tick += (_, _) => DismissDeviceConnection();
        DeviceConnection.MouseEnter += (_, _) => _deviceTimer.Stop();
        DeviceConnection.MouseLeave += (_, _) => ResumeDeviceTimer();
        DeviceHeaderStatus.MouseEnter += (_, _) => _deviceTimer.Stop();
        DeviceHeaderStatus.MouseLeave += (_, _) => ResumeDeviceTimer();
        DeviceConnection.IsKeyboardFocusWithinChanged += (_, _) => ResumeDeviceTimer();
        PreviewKeyDown += (_, args) =>
        {
            if (args.Handled || args.Key != Key.Escape || _activeNotification is not null || _deviceConnection is null) return;
            DismissDeviceConnection(); args.Handled = true;
        };
    }

    private void ResumeDeviceTimer()
    {
        _deviceTimer.Stop();
        if (_deviceConnection is not null && _activeNotification is null && !DeviceConnection.IsMouseOver
            && !DeviceConnection.IsKeyboardFocusWithin && !DeviceHeaderStatus.IsMouseOver
            && (DeviceConnection.IsVisible || DeviceHeaderStatus.IsVisible)) _deviceTimer.Start();
    }

    private void OnDeviceDismiss(object sender, RoutedEventArgs args)
    {
        args.Handled = true;
        DismissDeviceConnection();
    }

    private void StartAppleDevices()
    {
        _appleDevices = new AppleDeviceService(Dispatcher);
        _appleDevices.Connected += OnAppleDeviceConnected;
        _appleDevices.Start();
    }

    private void OnAppleDeviceConnected(object? sender, AppleDeviceConnection connection)
    {
        if (_fullscreenHidden) return;
        if (_deviceConnection?.Id == connection.Id)
        {
            _deviceConnection = connection;
            PresentDeviceConnection(connection);
            ResumeDeviceTimer();
            return;
        }
        if (_deviceQueue.Any(item => item.Id == connection.Id))
        {
            var pending = _deviceQueue.Select(item => item.Id == connection.Id ? connection : item).ToArray();
            _deviceQueue.Clear();
            foreach (var item in pending) _deviceQueue.Enqueue(item);
            return;
        }
        if (_deviceConnection is not null)
        {
            if (_deviceQueue.Count < 6) _deviceQueue.Enqueue(connection);
            return;
        }
        _deviceConnection = connection;
        PresentDeviceConnection(connection);
        _hudTimer.Stop(); _showingHud = false;
        Hud.Visibility = Visibility.Collapsed;
        UpdateDevicePresentation(true);
        FrameworkElement element = _expanded && !UsesIslandPlayer ? DeviceHeaderStatus : DeviceConnection;
        var peer = UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        ResumeDeviceTimer();
    }

    private void PresentDeviceConnection(AppleDeviceConnection connection)
    {
        DeviceName.Text = connection.Name;
        DeviceStatus.Text = DeviceHeaderLabel.Text = connection.ConnectionStatus;
        DeviceIllustration.Kind = DeviceHeaderIllustration.Kind = connection.Kind;
        var announcement = connection.Announcement;
        AutomationProperties.SetName(DeviceConnection, announcement);
        AutomationProperties.SetName(DeviceHeaderStatus, announcement);
    }

    private void UpdateDevicePresentation(bool entering = false)
    {
        var active = _deviceConnection is not null;
        var footer = active && _expanded && UsesIslandPlayer && _activeNotification is null;
        DeviceHeaderStatus.Visibility = active && _expanded && !footer && _activeNotification is null ? Visibility.Visible : Visibility.Collapsed;
        BatteryStatus.Visibility = _lastBattery is { HasBattery: true } && !(active && _expanded) ? Visibility.Visible : Visibility.Collapsed;
        if (!active || _expanded && !footer || _activeNotification is not null)
        {
            ++_deviceTransitionVersion;
            ResetDevicePresentation();
            DeviceConnection.Visibility = Visibility.Collapsed;
            ResumeDeviceTimer();
            return;
        }
        CompactActivity.Visibility = Hud.Visibility = Visibility.Collapsed;
        SetMinimalActivity(false);
        var inset = IsDynamicIsland ? DynamicIslandMetrics.ContentInset : 30;
        var padding = footer ? PopupFooterPadding : ClosedPopupPadding;
        DeviceConnection.Margin = new Thickness(inset, (footer ? BaseExpandedPanelHeight : 0) + padding, inset, padding);
        DeviceConnection.Height = AppleConnectionRowHeight;
        if (footer) UpdateExpandedLayout();
        var wasHidden = DeviceConnection.Visibility != Visibility.Visible;
        DeviceConnection.Visibility = Visibility.Visible;
        if (wasHidden) ResumeDeviceTimer();
        if (entering || wasHidden)
        {
            ++_deviceTransitionVersion;
            var opacity = wasHidden ? 0 : DeviceConnection.Opacity;
            var offset = wasHidden ? -3 : DeviceConnectionOffset.Y;
            ResetDevicePresentation();
            // Keep text at native scale and preserve the presented values when
            // a queued connection interrupts an unfinished fade.
            DeviceConnection.Opacity = opacity;
            DeviceConnectionOffset.Y = offset;
            var version = _deviceTransitionVersion;
            var duration = DurationFor(220);
            if (duration.TimeSpan == TimeSpan.Zero)
            {
                DeviceConnection.Opacity = 1;
                DeviceConnectionOffset.Y = 0;
            }
            else
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                var fade = new DoubleAnimation(opacity, 1, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
                fade.Completed += (_, _) =>
                {
                    if (version != _deviceTransitionVersion) return;
                    DeviceConnection.BeginAnimation(OpacityProperty, null);
                    DeviceConnectionOffset.BeginAnimation(TranslateTransform.YProperty, null);
                    DeviceConnection.Opacity = 1;
                    DeviceConnectionOffset.Y = 0;
                };
                DeviceConnection.BeginAnimation(OpacityProperty, fade);
                DeviceConnectionOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offset, 0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
            }
        }
        AnimateSize(footer ? ExpandedPanelWidth : IsDynamicIsland ? DynamicIslandMetrics.ExpandedWidth : Math.Max(AppleConnectionMinimumWidth, _settings.Current.NotchWidth + 64),
            footer ? ExpandedPanelHeight : AppleConnectionRowHeight + 2 * ClosedPopupPadding);
        UpdateVisualizer();
    }

    private void DismissDeviceConnection(bool immediate = false)
    {
        var restoreFocus = _expanded && !_fullscreenHidden && IsActive && DeviceConnection.IsKeyboardFocusWithin;
        _deviceTimer.Stop();
        _deviceConnection = null;
        if (immediate) _deviceQueue.Clear();
        var version = ++_deviceTransitionVersion;
        DeviceHeaderStatus.Visibility = Visibility.Collapsed;
        BatteryStatus.Visibility = _lastBattery is { HasBattery: true } ? Visibility.Visible : Visibility.Collapsed;
        if (immediate)
        {
            ResetDevicePresentation();
            DeviceConnection.Visibility = Visibility.Collapsed;
        }
        else if (DeviceConnection.Visibility == Visibility.Visible)
        {
            var opacity = DeviceConnection.Opacity;
            var offset = DeviceConnectionOffset.Y;
            ResetDevicePresentation();
            DeviceConnection.Opacity = opacity;
            DeviceConnectionOffset.Y = offset;
            var fade = new DoubleAnimation(opacity, 0, DurationFor(120));
            fade.Completed += (_, _) =>
            {
                if (version != _deviceTransitionVersion) return;
                ResetDevicePresentation();
                DeviceConnection.Visibility = Visibility.Collapsed;
            };
            DeviceConnection.BeginAnimation(OpacityProperty, fade);
            DeviceConnectionOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offset, -3, DurationFor(120)));
        }
        if (!immediate && _deviceQueue.TryDequeue(out var next)) OnAppleDeviceConnected(this, next);
        else if (!_expanded) UpdateCompact();
        else if (UsesIslandPlayer)
        {
            UpdateExpandedLayout();
            AnimateSize(ExpandedPanelWidth, ExpandedPanelHeight);
            UpdateVisualizer();
        }
        if (restoreFocus && _expanded && !_fullscreenHidden && IsActive)
            (_deviceConnection is not null && DeviceConnection.IsVisible ? DeviceDismissButton : SelectedTabControl).Focus();
    }

    private void FinishDeviceAnimations()
    {
        ResetDevicePresentation();
        DeviceConnection.Opacity = _deviceConnection is not null ? 1 : 0;
        UpdateDevicePresentation();
    }

    private void ResetDevicePresentation()
    {
        DeviceConnection.BeginAnimation(OpacityProperty, null); DeviceConnection.Opacity = 0;
        DeviceConnectionOffset.BeginAnimation(TranslateTransform.YProperty, null); DeviceConnectionOffset.Y = 0;
        DeviceConnectionScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); DeviceConnectionScale.ScaleX = 1;
        DeviceConnectionScale.BeginAnimation(ScaleTransform.ScaleYProperty, null); DeviceConnectionScale.ScaleY = 1;
    }

    internal void ShowAppleDeviceForTest(AppleDeviceConnection connection) => OnAppleDeviceConnected(this, connection);
    internal void DismissAppleDeviceForTest() => DismissDeviceConnection(true);
}
