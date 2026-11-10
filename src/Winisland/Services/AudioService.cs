using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Winisland.Services;

public sealed record AudioSnapshot(bool Available, int Volume, bool Muted, string Source = "");

// Volume belongs to the app shown in Now Playing. The visualizer follows that
// app's output device; endpoint volume is never used for volume writes.
public sealed class AudioService : IDisposable
{
    private static readonly Guid EndpointId = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _endpoint;
    private IAudioMeterInformation? _meter;
    private float[] _peaks = Array.Empty<float>();
    private DeviceNotifications? _notifications;
    private volatile bool _endpointDirty = true;
    private bool _notificationsRegistered;
    private long _lastDeviceCheck;
    private readonly List<ApplicationSession> _sessions = new();
    private string _source = "";
    private long _lastSessionCheck;
    private volatile bool _sessionsDirty = true;

    public void SetSource(string source)
    {
        if (string.Equals(_source, source, StringComparison.Ordinal)) return;
        _source = source;
        _sessionsDirty = true;
    }

    public AudioSnapshot Read()
    {
        try
        {
            var sessions = GetApplicationSessions();
            if (sessions.Count == 0) return new(false, 0, false, _source);
            var volumes = new List<float>();
            var allMuted = true;
            foreach (var session in sessions)
            {
                Marshal.ThrowExceptionForHR(session.Volume.GetMasterVolume(out var volume));
                Marshal.ThrowExceptionForHR(session.Volume.GetMute(out var muted));
                volumes.Add(volume);
                allMuted &= muted;
            }
            return new(true, (int)Math.Round(volumes.Max() * 100), allMuted, _source);
        }
        catch (COMException) { _sessionsDirty = true; return new(false, 0, false, _source); }
    }

    public bool SetVolume(int percent)
    {
        return ApplyToApplication(volume => volume.SetMasterVolume(Math.Clamp(percent, 0, 100) / 100f, Guid.Empty));
    }

    public bool SetMuted(bool muted)
    {
        return ApplyToApplication(volume => volume.SetMute(muted, Guid.Empty));
    }

    // Verification observes the endpoint independently of the app sessions.
    internal AudioSnapshot ReadOutputForTest()
    {
        try
        {
            return WithEndpoint(endpoint =>
            {
                Marshal.ThrowExceptionForHR(endpoint.GetMasterVolumeLevelScalar(out var volume));
                Marshal.ThrowExceptionForHR(endpoint.GetMute(out var muted));
                return new AudioSnapshot(true, (int)Math.Round(volume * 100), muted);
            });
        }
        catch (COMException ex) when ((uint)ex.HResult == 0x80070490)
        {
            // GetDefaultAudioEndpoint returns ERROR_NOT_FOUND on hardware-free
            // Windows VMs. Other COM failures must still fail verification.
            _endpointDirty = true;
            return new(false, 0, false);
        }
    }

    private bool ApplyToApplication(Func<ISimpleAudioVolume, int> action)
    {
        try
        {
            var sessions = GetApplicationSessions();
            if (sessions.Count == 0) return false;
            var success = true;
            foreach (var session in sessions) success &= action(session.Volume) >= 0;
            if (!success) _sessionsDirty = true;
            return success;
        }
        catch (COMException) { _sessionsDirty = true; return false; }
    }

    private List<ApplicationSession> GetApplicationSessions()
    {
        if (string.IsNullOrWhiteSpace(_source)) return new();
        if (_sessionsDirty || Environment.TickCount64 - _lastSessionCheck >= 1000) RefreshSessions();
        return _sessions.Where(session => MatchesSource(_source, session.ProcessName, session.AppId)).ToList();
    }

    internal string? ReadVisualizerDeviceId()
    {
        try { return GetApplicationSessions().OrderByDescending(session => session.Active).FirstOrDefault()?.DeviceId; }
        catch (COMException) { _sessionsDirty = true; return null; }
    }

    internal static bool MatchesSource(string source, string processName, string appId)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        if (!string.IsNullOrEmpty(appId) &&
            (string.Equals(source, appId, StringComparison.OrdinalIgnoreCase) ||
             (source.Contains('!') && string.Equals(source.Split('!')[0], appId.Split('!')[0], StringComparison.OrdinalIgnoreCase)))) return true;
        var name = source[(Math.Max(source.LastIndexOf('\\'), source.LastIndexOf('/')) + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)) return true;
        // Browser media IDs can identify an installed website or a profile;
        // Windows still exposes their audio under the browser process.
        foreach (var browser in new[] { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi" })
            if (string.Equals(processName, browser, StringComparison.OrdinalIgnoreCase) && name.StartsWith(browser + ".", StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(processName, "firefox", StringComparison.OrdinalIgnoreCase) && name.StartsWith("org.mozilla.firefox", StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshSessions()
    {
        EnsureEnumerator();
        ReleaseSessions();
        _sessionsDirty = false;
        _lastSessionCheck = Environment.TickCount64;
        IMMDeviceCollection? devices = null;
        var identities = new Dictionary<uint, (string Name, string AppId)>();
        try
        {
            // Include apps explicitly routed to headphones or another output.
            Marshal.ThrowExceptionForHR(_enumerator!.EnumAudioEndpoints(0, 1, out devices));
            Marshal.ThrowExceptionForHR(devices.GetCount(out var deviceCount));
            for (uint deviceIndex = 0; deviceIndex < deviceCount; deviceIndex++)
            {
                IMMDevice? device = null;
                IAudioSessionManager2? manager = null;
                IAudioSessionEnumerator? sessions = null;
                try
                {
                    Marshal.ThrowExceptionForHR(devices.Item(deviceIndex, out device));
                    Marshal.ThrowExceptionForHR(device.GetId(out var deviceId));
                    Marshal.ThrowExceptionForHR(device.Activate(typeof(IAudioSessionManager2).GUID, 23, IntPtr.Zero, out var managerObject));
                    manager = (IAudioSessionManager2)managerObject;
                    Marshal.ThrowExceptionForHR(manager.GetSessionEnumerator(out sessions));
                    Marshal.ThrowExceptionForHR(sessions.GetCount(out var count));
                    for (var index = 0; index < count; index++)
                    {
                        IAudioSessionControl2? control = null;
                        try
                        {
                            Marshal.ThrowExceptionForHR(sessions.GetSession(index, out control));
                            if (control.GetState(out var state) < 0 || state == 2 || control.GetProcessId(out var pid) < 0 || pid == 0) continue;
                            if (!identities.TryGetValue(pid, out var identity)) identities[pid] = identity = ReadProcessIdentity(pid);
                            if (string.IsNullOrEmpty(identity.Name)) continue;
                            _sessions.Add(new(control, (ISimpleAudioVolume)control, identity.Name, identity.AppId, deviceId, state == 1));
                            control = null; // The cache now owns this COM reference.
                        }
                        catch (COMException) { _sessionsDirty = true; }
                        finally { if (control is not null) Marshal.ReleaseComObject(control); }
                    }
                }
                catch (COMException) { _sessionsDirty = true; }
                finally
                {
                    if (sessions is not null) Marshal.ReleaseComObject(sessions);
                    if (manager is not null) Marshal.ReleaseComObject(manager);
                    if (device is not null) Marshal.ReleaseComObject(device);
                }
            }
        }
        catch { _sessionsDirty = true; throw; }
        finally { if (devices is not null) Marshal.ReleaseComObject(devices); }
    }

    private static (string Name, string AppId) ReadProcessIdentity(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            using var handle = OpenProcess(0x1000, false, pid); // Query limited information.
            if (handle.IsInvalid) return (name, "");
            uint length = 0;
            if (GetApplicationUserModelId(handle, ref length, null) != 122 || length is 0 or > 32768) return (name, "");
            var appId = new StringBuilder((int)length);
            return (name, GetApplicationUserModelId(handle, ref length, appId) == 0 ? appId.ToString() : "");
        }
        catch (ArgumentException) { return ("", ""); }
        catch (InvalidOperationException) { return ("", ""); }
        catch (Win32Exception) { return ("", ""); }
    }

    private void ReleaseSessions()
    {
        // Control and Volume are interfaces on the same RCW; release it once.
        foreach (var session in _sessions) Marshal.ReleaseComObject(session.Control);
        _sessions.Clear();
    }

    private sealed record ApplicationSession(IAudioSessionControl2 Control, ISimpleAudioVolume Volume, string ProcessName, string AppId, string DeviceId, bool Active);

    [DllImport("kernel32.dll")] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(SafeProcessHandle process, ref uint length, StringBuilder? appId);

    public (float Left, float Right) ReadPeaks()
    {
        try
        {
            return WithEndpoint(_ =>
            {
                if (_meter is null) return (0f, 0f);
                Marshal.ThrowExceptionForHR(_meter.GetChannelsPeakValues((uint)_peaks.Length, _peaks));
                return (_peaks[0], _peaks.Length > 1 ? _peaks[1] : _peaks[0]);
            });
        }
        catch (COMException) { _endpointDirty = true; return (0, 0); }
    }

    private void EnsureEnumerator()
    {
        if (_enumerator is null)
        {
            // Windows can return the same enumerator identity to both clients. A
            // cached RCW typed as our old COM coclass breaks NAudio's coclass cast.
            // Own an isolated interface wrapper so either client can initialize first.
            var classId = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
            var interfaceId = typeof(IMMDeviceEnumerator).GUID;
            Marshal.ThrowExceptionForHR(CoCreateInstance(in classId, IntPtr.Zero, 23, in interfaceId, out var pointer));
            try { _enumerator = (IMMDeviceEnumerator)Marshal.GetUniqueObjectForIUnknown(pointer); }
            finally { Marshal.Release(pointer); }
            _notifications = new DeviceNotifications(() => { _endpointDirty = true; _sessionsDirty = true; });
            _notificationsRegistered = _enumerator.RegisterEndpointNotificationCallback(_notifications) >= 0;
        }
    }

    private T WithEndpoint<T>(Func<IAudioEndpointVolume, T> action)
    {
        EnsureEnumerator();
        // If notification registration fails, check the default device every five seconds.
        if (!_notificationsRegistered && Environment.TickCount64 - _lastDeviceCheck >= 5000)
        { _endpointDirty = true; _lastDeviceCheck = Environment.TickCount64; }
        if (_endpointDirty || _endpoint is null)
        {
            if (_meter is not null) { Marshal.ReleaseComObject(_meter); _meter = null; }
            if (_endpoint is not null) { Marshal.ReleaseComObject(_endpoint); _endpoint = null; }
            _endpointDirty = false;
            IMMDevice? device = null;
            try
            {
                Marshal.ThrowExceptionForHR(_enumerator!.GetDefaultAudioEndpoint(0, 1, out device));
                Marshal.ThrowExceptionForHR(device.Activate(EndpointId, 23, IntPtr.Zero, out var endpointObject));
                _endpoint = (IAudioEndpointVolume)endpointObject;
                if (device.Activate(typeof(IAudioMeterInformation).GUID, 23, IntPtr.Zero, out var meterObject) >= 0)
                {
                    _meter = (IAudioMeterInformation)meterObject;
                    if (_meter.GetMeteringChannelCount(out var channels) >= 0 && channels is > 0 and <= 32)
                        _peaks = new float[channels];
                    else { Marshal.ReleaseComObject(_meter); _meter = null; }
                }
            }
            catch { _endpointDirty = true; throw; }
            finally { if (device is not null) Marshal.ReleaseComObject(device); }
        }
        return action(_endpoint);
    }

    public void Dispose()
    {
        ReleaseSessions();
        if (_enumerator is not null && _notificationsRegistered && _notifications is not null)
            _enumerator.UnregisterEndpointNotificationCallback(_notifications);
        if (_meter is not null) { Marshal.ReleaseComObject(_meter); _meter = null; }
        if (_endpoint is not null) { Marshal.ReleaseComObject(_endpoint); _endpoint = null; }
        if (_enumerator is not null) { Marshal.ReleaseComObject(_enumerator); _enumerator = null; }
        _notifications = null;
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(in Guid classId, IntPtr outer, uint context, in Guid interfaceId, out IntPtr pointer);
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback([MarshalAs(UnmanagedType.Interface)] IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback([MarshalAs(UnmanagedType.Interface)] IMMNotificationClient client);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(in Guid session, uint flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(in Guid session, uint flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        [PreserveSig] int RegisterSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterSessionNotification(IntPtr notification);
        [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr notification);
        [PreserveSig] int UnregisterDuckNotification(IntPtr notification);
    }
    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 control);
    }
    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, in Guid context);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, in Guid context);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(in Guid grouping, in Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }
    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float volume, in Guid context);
        [PreserveSig] int GetMasterVolume(out float volume);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, in Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
    [ComVisible(true), Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string device, uint state);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string device);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string device);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? device);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string device, PropertyKey key);
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class DeviceNotifications : IMMNotificationClient
    {
        private readonly Action _changed;
        public DeviceNotifications(Action changed) => _changed = changed;
        public int OnDeviceStateChanged(string device, uint state) { _changed(); return 0; }
        public int OnDeviceAdded(string device) { _changed(); return 0; }
        public int OnDeviceRemoved(string device) { _changed(); return 0; }
        public int OnDefaultDeviceChanged(int flow, int role, string? device) { if (flow == 0 && role == 1) _changed(); return 0; }
        public int OnPropertyValueChanged(string device, PropertyKey key) => 0;
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(in Guid id, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        [PreserveSig] int OpenPropertyStore(uint mode, out IntPtr store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, in Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, in Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, in Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, in Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, in Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint count);
        [PreserveSig] int VolumeStepUp(in Guid context);
        [PreserveSig] int VolumeStepDown(in Guid context);
        [PreserveSig] int QueryHardwareSupport(out uint mask);
        [PreserveSig] int GetVolumeRange(out float min, out float max, out float increment);
    }
    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
        [PreserveSig] int GetMeteringChannelCount(out uint count);
        [PreserveSig] int GetChannelsPeakValues(uint count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] float[] peaks);
        [PreserveSig] int QueryHardwareSupport(out uint mask);
    }
}
