using System;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Winisland.Services;

namespace Winisland;

public partial class NotchWindow
{
    private int _overlayRecoveryQueued;

    private void WatchOverlayVisibility()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void StopWatchingOverlayVisibility()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) QueueOverlayRecovery();
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect
            or SessionSwitchReason.RemoteConnect) QueueOverlayRecovery();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => QueueOverlayRecovery();

    private void QueueOverlayRecovery()
    {
        if (_closed || Dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _overlayRecoveryQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _overlayRecoveryQueued, 0);
            if (_closed) return;
            UpdateFullscreenVisibility();
            MaintainOverlayVisibility(refreshDisplay: true);
        }, DispatcherPriority.Background);
    }

    private void MaintainOverlayVisibility(bool refreshDisplay = false)
    {
        if (_closed || _fullscreenHidden) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        // Windows can hide/minimize or reorder a tool window across desktop,
        // display-power and compositor transitions. Restore without taking focus.
        if (!IsVisible) Show();
        NativeMethods.RestoreOverlay(handle);
        if (refreshDisplay)
        {
            NativeMethods.ConfigureOverlay(handle);
            NativeMethods.SetActivatable(handle, _pinned);
            NativeMethods.PositionOverlay(this, _settings.Current.MonitorDevice);
            Island.InvalidateVisual();
        }
    }

    internal void RecoverOverlayForTest()
    {
        BackgroundVerification.RequirePrivateDesktop();
        QueueOverlayRecovery();
    }

    internal void StartOverlayPulseForTest()
    {
        BackgroundVerification.RequirePrivateDesktop();
        _pulse.Start();
    }
}
