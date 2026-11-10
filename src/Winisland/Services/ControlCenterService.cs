using System;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;

namespace Winisland.Services;

internal enum QuickRadio { Wifi, Bluetooth }
internal sealed record RadioStatus(bool Present, bool Enabled, bool CanToggle, string Detail);
internal sealed record SystemSound(bool Available, int Volume, bool Muted, string Device);
internal sealed record DisplayBrightness(bool Available, int Value);
internal sealed record ControlCenterSnapshot(RadioStatus Wifi, RadioStatus Bluetooth, SystemSound Sound, DisplayBrightness Brightness);
internal sealed record ControlResult(bool Success, string? Error = null);

internal interface IControlCenterBackend
{
    Task<ControlCenterSnapshot> ReadAsync();
    Task<ControlResult> SetRadioAsync(QuickRadio kind, bool enabled);
    Task<ControlResult> SetVolumeAsync(int value);
    Task<ControlResult> SetMutedAsync(bool muted);
    Task<ControlResult> SetBrightnessAsync(int value);
}

// Separate from the music player's volume: Control Center explicitly controls
// the Windows default output. Reads never request permission or change a device.
internal sealed class ControlCenterService : IControlCenterBackend
{
    public async Task<ControlCenterSnapshot> ReadAsync()
    {
        var wifi = new RadioStatus(false, false, false, "Open Wi-Fi settings");
        var bluetooth = new RadioStatus(false, false, false, "Open Bluetooth settings");
        try
        {
            var radios = await Radio.GetRadiosAsync();
            RadioStatus ReadRadio(RadioKind kind)
            {
                var devices = radios.Where(radio => radio.Kind == kind).ToArray();
                if (devices.Length == 0) return new(false, false, false, "Not available");
                var enabled = devices.Any(radio => radio.State == RadioState.On);
                var canToggle = devices.All(radio => radio.State is RadioState.On or RadioState.Off);
                return new(true, enabled, canToggle, canToggle ? enabled ? "On" : "Off" : "Managed by Windows");
            }
            wifi = ReadRadio(RadioKind.WiFi);
            bluetooth = ReadRadio(RadioKind.Bluetooth);
            if (wifi.Enabled)
            {
                try
                {
                    var profile = NetworkInformation.GetConnectionProfiles().FirstOrDefault(profile => profile.IsWlanConnectionProfile
                        && profile.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None);
                    var ssid = profile?.WlanConnectionProfileDetails?.GetConnectedSsid();
                    wifi = wifi with { Detail = string.IsNullOrWhiteSpace(ssid) ? "Not connected" : ssid };
                }
                catch (Exception) { wifi = wifi with { Detail = "On" }; } // Windows can restrict SSID access.
            }
        }
        catch (Exception) { /* Settings links remain usable when radio enumeration is restricted. */ }

        var sound = await Task.Run(ReadSound);
        var brightness = await Task.Run(ReadBrightness);
        return new(wifi, bluetooth, sound, brightness);
    }

    public async Task<ControlResult> SetRadioAsync(QuickRadio kind, bool enabled)
    {
        var label = kind == QuickRadio.Wifi ? "Wi-Fi" : "Bluetooth";
        try
        {
            // Called from the UI only after an explicit press. Desktop access can
            // be denied by policy or hardware, so the UI always reads back state.
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed)
                return new(false, $"Windows couldn't change {label}. Use its settings instead.");
            var radioKind = kind == QuickRadio.Wifi ? RadioKind.WiFi : RadioKind.Bluetooth;
            var radios = (await Radio.GetRadiosAsync()).Where(radio => radio.Kind == radioKind).ToArray();
            if (radios.Length == 0) return new(false, $"No {label} adapter is available.");
            foreach (var radio in radios)
            {
                if (radio.State is not (RadioState.On or RadioState.Off)
                    || await radio.SetStateAsync(enabled ? RadioState.On : RadioState.Off) != RadioAccessStatus.Allowed)
                    return new(false, $"Windows couldn't change {label}. Check its settings.");
            }
            return new(true);
        }
        catch (Exception) { return new(false, $"Couldn't change {label}. Check its Windows settings."); }
    }

    private static SystemSound ReadSound()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return new(true, (int)Math.Round(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100),
                device.AudioEndpointVolume.Mute, device.FriendlyName);
        }
        catch (Exception) { return new(false, 0, false, "No output device"); }
    }

    public Task<ControlResult> SetVolumeAsync(int value) => WriteSound(device =>
        device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0, 100) / 100f);
    public Task<ControlResult> SetMutedAsync(bool muted) => WriteSound(device => device.AudioEndpointVolume.Mute = muted);

    private static Task<ControlResult> WriteSound(Action<MMDevice> write) => Task.Run(() =>
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            write(device);
            return new ControlResult(true);
        }
        catch (Exception) { return new(false, "Couldn't change sound. Check your output device."); }
    });

    private static DisplayBrightness ReadBrightness()
    {
        try
        {
            using var query = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active = TRUE");
            using var monitors = query.Get();
            foreach (ManagementObject monitor in monitors)
            {
                using (monitor) return new(true, Convert.ToInt32(monitor["CurrentBrightness"]));
            }
        }
        catch (Exception) { /* External displays usually do not expose this interface. */ }
        return new(false, 0);
    }

    public Task<ControlResult> SetBrightnessAsync(int value) => Task.Run(() =>
    {
        try
        {
            using var query = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE");
            using var monitors = query.Get();
            var changed = false;
            foreach (ManagementObject monitor in monitors)
            {
                using (monitor) monitor.InvokeMethod("WmiSetBrightness", new object[] { (uint)0, (byte)Math.Clamp(value, 0, 100) });
                changed = true;
            }
            return new ControlResult(changed, changed ? null : "Use your display's brightness controls.");
        }
        catch (Exception) { return new(false, "Couldn't change brightness. Use your display's controls."); }
    });
}
