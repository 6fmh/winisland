using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Winisland.Services;

namespace Winisland.Controls;

public partial class ControlCenterPanel : UserControl, IDisposable
{
    private IControlCenterBackend _backend = new ControlCenterService();
    private ControlCenterSnapshot? _snapshot;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _apply = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private bool _updating, _active, _disposed, _radioBusy, _muteBusy, _applying;
    private int? _pendingVolume, _pendingBrightness;
    private string? _feedback;
    public bool IsInteracting => SystemVolumeSlider.IsAdjusting || BrightnessSlider.IsAdjusting || IsMouseCaptureWithin || _radioBusy || _muteBusy;
    public event EventHandler? InteractionEnded;
    public event EventHandler? FocusRequested;
    public event EventHandler<string>? ShortcutRequested;

    public ControlCenterPanel()
    {
        InitializeComponent();
        _refresh.Tick += async (_, _) => await RefreshAsync();
        _apply.Tick += async (_, _) => { _apply.Stop(); await ApplyPendingAsync(); };
        IsVisibleChanged += (_, _) => SynchronizeRefresh();
        foreach (var slider in new[] { SystemVolumeSlider, BrightnessSlider })
            slider.AdjustmentCompleted += (_, _) => InteractionEnded?.Invoke(this, EventArgs.Empty);
    }

    internal void SetActive(bool active) { _active = active; SynchronizeRefresh(); }
    private void SynchronizeRefresh()
    {
        _refresh.Stop();
        if (_disposed || !_active || !IsVisible) return;
        _refresh.Start();
        _ = RefreshAsync();
    }
    internal async Task RefreshAsync()
    {
        if (_disposed || !_active || !IsVisible || !await _operation.WaitAsync(0)) return;
        try { ApplySnapshot(await _backend.ReadAsync()); }
        catch (Exception) { ControlStatus.Text = "Couldn't refresh controls. Try Windows settings."; }
        finally { _operation.Release(); }
    }
    private void ApplySnapshot(ControlCenterSnapshot snapshot)
    {
        if (_disposed) return;
        _snapshot = snapshot;
        _updating = true;
        try
        {
            UpdateRadio(WifiPower, WifiDetail, snapshot.Wifi, "Wi-Fi");
            UpdateRadio(BluetoothPower, BluetoothDetail, snapshot.Bluetooth, "Bluetooth");
            var sound = snapshot.Sound;
            ControlStatus.Text = _feedback ?? sound.Device;
            SystemVolumeSlider.IsEnabled = sound.Available;
            SystemMuteButton.IsEnabled = sound.Available && !_muteBusy;
            if (_pendingVolume is null && !SystemVolumeSlider.IsAdjusting) SystemVolumeSlider.Value = sound.Volume;
            SystemVolumeSlider.ToolTip = sound.Available ? $"{sound.Device} · Windows volume" : sound.Device;
            SystemSoundGlyph.Kind = sound.Muted ? NotchIconKind.MutedSpeaker : NotchIconKind.Speaker;
            SystemSoundGlyph.SoundLevel = sound.Volume;
            SystemMuteButton.ToolTip = sound.Muted ? "Unmute system sound" : "Mute system sound";
            AutomationProperties.SetName(SystemMuteButton, sound.Muted ? "Unmute system sound" : "Mute system sound");
            BrightnessSlider.IsEnabled = snapshot.Brightness.Available;
            BrightnessSlider.Visibility = snapshot.Brightness.Available ? Visibility.Visible : Visibility.Hidden;
            BrightnessUnavailable.Visibility = snapshot.Brightness.Available ? Visibility.Collapsed : Visibility.Visible;
            if (_pendingBrightness is null && !BrightnessSlider.IsAdjusting) BrightnessSlider.Value = snapshot.Brightness.Value;
            UpdateValues();
            var battery = NativeMethods.GetBattery();
            PowerGlyph.Kind = battery.HasBattery ? battery.Charging ? NotchIconKind.ChargingBattery : NotchIconKind.Battery : NotchIconKind.Power;
            PowerGlyph.BatteryLevel = battery.Percent ?? 0;
            PowerDetail.Text = battery.HasBattery && battery.Percent is { } percent ? $"{percent}%" : "Power";
        }
        finally { _updating = false; }
    }
    private void UpdateRadio(ToggleButton button, TextBlock detail, RadioStatus state, string label)
    {
        button.IsChecked = state.Enabled;
        button.IsEnabled = state.CanToggle && !_radioBusy;
        detail.Text = state.Detail;
        button.ToolTip = state.CanToggle ? $"Turn {label} {(state.Enabled ? "off" : "on")}"
            : $"{state.Detail}. Use the arrow to open Windows settings.";
        AutomationProperties.SetHelpText(button, state.Detail);
    }
    private void UpdateValues()
    {
        SystemSoundGlyph.SoundLevel = SystemVolumeSlider.Value;
        SoundValue.Text = _snapshot?.Sound.Available == true
            ? _snapshot.Sound.Muted ? "Muted" : $"{SystemVolumeSlider.Value:0}%" : "—";
        BrightnessValue.Text = _snapshot?.Brightness.Available == true ? $"{BrightnessSlider.Value:0}%" : "—";
    }
    private async void OnWifiToggle(object sender, RoutedEventArgs e) => await ToggleRadioAsync(QuickRadio.Wifi);
    private async void OnBluetoothToggle(object sender, RoutedEventArgs e) => await ToggleRadioAsync(QuickRadio.Bluetooth);
    private async Task ToggleRadioAsync(QuickRadio kind)
    {
        if (_snapshot is null || _radioBusy || _disposed) return;
        var previous = kind == QuickRadio.Wifi ? _snapshot.Wifi : _snapshot.Bluetooth;
        if (!previous.CanToggle) return;
        _radioBusy = true;
        ApplySnapshot(_snapshot); // Show confirmed state until Windows acknowledges the command.
        ControlStatus.Text = _feedback = $"Changing {(kind == QuickRadio.Wifi ? "Wi-Fi" : "Bluetooth")}…";
        await _operation.WaitAsync();
        try
        {
            if (_disposed) return;
            ShowResult(await _backend.SetRadioAsync(kind, !previous.Enabled));
            ApplySnapshot(await _backend.ReadAsync());
        }
        catch (Exception) { ControlStatus.Text = _feedback = "Couldn't change the adapter. Use Windows settings."; }
        finally
        {
            _operation.Release(); _radioBusy = false;
            if (_snapshot is not null) ApplySnapshot(_snapshot);
            if (!_disposed) InteractionEnded?.Invoke(this, EventArgs.Empty);
        }
    }
    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || _disposed || !SystemVolumeSlider.IsEnabled) return;
        _pendingVolume = (int)Math.Round(e.NewValue); UpdateValues();
        _apply.Start();
    }
    private void OnBrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || _disposed || !BrightnessSlider.IsEnabled) return;
        _pendingBrightness = (int)Math.Round(e.NewValue); UpdateValues();
        _apply.Start();
    }
    private async Task ApplyPendingAsync()
    {
        if (_applying || _disposed) return;
        _applying = true;
        await _operation.WaitAsync();
        try
        {
            if (_disposed) return;
            var volume = _pendingVolume; var brightness = _pendingBrightness;
            _pendingVolume = _pendingBrightness = null;
            if (volume is { } value) ShowResult(await _backend.SetVolumeAsync(value));
            if (brightness is { } level) ShowResult(await _backend.SetBrightnessAsync(level));
            ApplySnapshot(await _backend.ReadAsync());
        }
        catch (Exception) { ControlStatus.Text = _feedback = "Couldn't apply this change. Check Windows settings."; }
        finally
        {
            _operation.Release();
            _applying = false;
            if (!_disposed && (_pendingVolume is not null || _pendingBrightness is not null)) _apply.Start();
        }
    }
    private async void OnMuteClick(object sender, RoutedEventArgs e)
    {
        if (_snapshot?.Sound.Available != true || _disposed || _muteBusy) return;
        var muted = !_snapshot.Sound.Muted; _muteBusy = true; SystemMuteButton.IsEnabled = false;
        await _operation.WaitAsync();
        try
        {
            if (_disposed) return;
            ShowResult(await _backend.SetMutedAsync(muted));
            ApplySnapshot(await _backend.ReadAsync());
        }
        catch (Exception) { ControlStatus.Text = _feedback = "Couldn't change sound. Check your output device."; }
        finally
        {
            _operation.Release(); _muteBusy = false;
            if (_snapshot is not null) ApplySnapshot(_snapshot);
            if (!_disposed) InteractionEnded?.Invoke(this, EventArgs.Empty);
        }
    }
    private void ShowResult(ControlResult result)
    {
        _feedback = result.Success ? null : result.Error ?? "Couldn't apply this change.";
        ControlStatus.Text = _feedback ?? _snapshot?.Sound.Device ?? "System controls";
    }
    private void OnFocusClick(object sender, RoutedEventArgs e) => FocusRequested?.Invoke(this, EventArgs.Empty);
    private async void OnSettingsShortcut(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not string uri) return;
        ShortcutRequested?.Invoke(this, uri);
        if (uri == "ms-screenclip:") await Task.Delay(180);
        if (_disposed) return;
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        { ControlStatus.Text = "That Windows shortcut is unavailable on this device."; }
    }
    internal void CancelAdjustment() { SystemVolumeSlider.CancelAdjustment(); BrightnessSlider.CancelAdjustment(); }
    internal bool RefreshTimerRunningForTest => _refresh.IsEnabled;
    internal void SetBackendForTest(IControlCenterBackend backend) => _backend = backend;
    public void Dispose()
    {
        _disposed = true; _refresh.Stop(); _apply.Stop();
        _pendingVolume = _pendingBrightness = null; CancelAdjustment();
    }
}
