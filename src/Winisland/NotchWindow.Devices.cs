using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Winisland.Services;

namespace Winisland;

public partial class NotchWindow
{
    private AppleDeviceService? _appleDevices;
    private AppleDeviceConnection? _deviceConnection;
    private readonly Queue<AppleDeviceConnection> _deviceQueue = new();
    private readonly DispatcherTimer _deviceTimer = new() { Interval = TimeSpan.FromSeconds(4.5) };
    private int _deviceTransitionVersion;

    private void InitializeAppleDevices()
    {
        _deviceTimer.Tick += (_, _) => DismissDeviceConnection();
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
        if (_deviceConnection?.Id == connection.Id || _deviceQueue.Any(item => item.Id == connection.Id)) return;
        if (_deviceConnection is not null)
        {
            if (_deviceQueue.Count < 6) _deviceQueue.Enqueue(connection);
            return;
        }
        _deviceConnection = connection;
        DeviceName.Text = connection.Name;
        DeviceIllustration.Kind = DeviceHeaderIllustration.Kind = connection.Kind;
        var announcement = $"{connection.Name} connected";
        AutomationProperties.SetName(DeviceConnection, announcement);
        AutomationProperties.SetName(DeviceHeaderStatus, announcement);
        _hudTimer.Stop(); _showingHud = false;
        Hud.Visibility = Visibility.Collapsed;
        UpdateDevicePresentation(true);
        var peer = UIElementAutomationPeer.FromElement(_expanded ? DeviceHeaderStatus : DeviceConnection);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        _deviceTimer.Stop(); _deviceTimer.Start();
    }

    private void UpdateDevicePresentation(bool entering = false)
    {
        var active = _deviceConnection is not null;
        DeviceHeaderStatus.Visibility = active && _expanded ? Visibility.Visible : Visibility.Collapsed;
        BatteryStatus.Visibility = _lastBattery is { HasBattery: true } && !(active && _expanded) ? Visibility.Visible : Visibility.Collapsed;
        if (!active || _expanded)
        {
            ++_deviceTransitionVersion;
            DeviceConnection.BeginAnimation(OpacityProperty, null);
            DeviceConnection.Visibility = Visibility.Collapsed;
            return;
        }
        ++_deviceTransitionVersion;
        CompactActivity.Visibility = Hud.Visibility = Visibility.Collapsed;
        DeviceConnection.Margin = new Thickness(30, _settings.Current.NotchHeight + 8, 30, 20);
        var wasHidden = DeviceConnection.Visibility != Visibility.Visible;
        DeviceConnection.Visibility = Visibility.Visible;
        if (entering || wasHidden)
        {
            var duration = DurationFor(260);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            DeviceConnection.BeginAnimation(OpacityProperty, new DoubleAnimation(wasHidden ? 0 : DeviceConnection.Opacity, 1, duration) { EasingFunction = ease });
            DeviceConnectionOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(wasHidden ? -4 : DeviceConnectionOffset.Y, 0, duration) { EasingFunction = ease });
            var scale = new DoubleAnimation(wasHidden ? .96 : DeviceConnectionScale.ScaleX, 1, duration) { EasingFunction = ease };
            DeviceConnectionScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
            DeviceConnectionScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        }
        AnimateSize(Math.Max(360, _settings.Current.NotchWidth + 64), _settings.Current.NotchHeight + 76);
        UpdateVisualizer();
    }

    private void DismissDeviceConnection(bool immediate = false)
    {
        _deviceTimer.Stop();
        _deviceConnection = null;
        if (immediate) _deviceQueue.Clear();
        var version = ++_deviceTransitionVersion;
        DeviceHeaderStatus.Visibility = Visibility.Collapsed;
        BatteryStatus.Visibility = _lastBattery is { HasBattery: true } ? Visibility.Visible : Visibility.Collapsed;
        if (immediate)
        {
            DeviceConnection.BeginAnimation(OpacityProperty, null);
            DeviceConnection.Visibility = Visibility.Collapsed;
        }
        else if (DeviceConnection.Visibility == Visibility.Visible)
        {
            var fade = new DoubleAnimation(DeviceConnection.Opacity, 0, DurationFor(120));
            fade.Completed += (_, _) => { if (version == _deviceTransitionVersion) DeviceConnection.Visibility = Visibility.Collapsed; };
            DeviceConnection.BeginAnimation(OpacityProperty, fade);
        }
        if (!immediate && _deviceQueue.TryDequeue(out var next)) OnAppleDeviceConnected(this, next);
        else if (!_expanded) UpdateCompact();
    }

    private void FinishDeviceAnimations()
    {
        DeviceConnection.BeginAnimation(OpacityProperty, null); DeviceConnection.Opacity = _deviceConnection is not null ? 1 : 0;
        DeviceConnectionOffset.BeginAnimation(TranslateTransform.YProperty, null); DeviceConnectionOffset.Y = 0;
        DeviceConnectionScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); DeviceConnectionScale.ScaleX = 1;
        DeviceConnectionScale.BeginAnimation(ScaleTransform.ScaleYProperty, null); DeviceConnectionScale.ScaleY = 1;
        UpdateDevicePresentation();
    }

    internal void ShowAppleDeviceForTest(AppleDeviceConnection connection) => OnAppleDeviceConnected(this, connection);
    internal void DismissAppleDeviceForTest() => DismissDeviceConnection(true);
}
