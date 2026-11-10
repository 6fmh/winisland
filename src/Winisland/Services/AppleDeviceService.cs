using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace Winisland.Services;

// Event-driven, read-only Windows discovery. No pairing, radio scanning, or audio routing.
public sealed class AppleDeviceService : IDisposable
{
    private const string ConnectedProperty = "System.Devices.Aep.IsConnected";
    private const string ContainerProperty = "System.Devices.ContainerId";
    private const string AepContainerProperty = "System.Devices.Aep.ContainerId";
    private static readonly string[] Properties = { ConnectedProperty, ContainerProperty, AepContainerProperty, "System.Devices.HardwareIds" };
    private readonly Dispatcher _dispatcher;
    private readonly List<DeviceWatcher> _watchers = new();
    private readonly HashSet<DeviceWatcher> _ready = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly AppleConnectionGroups _connections = new();
    private bool _disposed;
    public event EventHandler<AppleDeviceConnection>? Connected;

    private sealed class Entry
    {
        internal required DeviceInformation Info;
        internal required bool Wired;
        internal bool IsConnected;
        internal string Group = "";
        internal int Revision;
    }

    public AppleDeviceService(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public void Start()
    {
        if (_disposed || _watchers.Count != 0) return;
        try
        {
            AddWatcher(DeviceInformation.CreateWatcher(BluetoothDevice.GetDeviceSelectorFromPairingState(true), Properties, DeviceInformationKind.AssociationEndpoint), false);
            // All present USB device interfaces; vendor filtering happens using PnP hardware IDs.
            AddWatcher(DeviceInformation.CreateWatcher("System.Devices.InterfaceClassGuid:=\"{A5DCBF10-6530-11D2-901F-00C04FB951ED}\" AND System.Devices.InterfaceEnabled:=System.StructuredQueryType.Boolean#True", Properties, DeviceInformationKind.DeviceInterface), true);
        }
        catch (Exception ex) { Log.Error("Start Apple device discovery", ex); }
    }

    private void AddWatcher(DeviceWatcher watcher, bool wired)
    {
        _watchers.Add(watcher);
        watcher.Added += (_, info) => Dispatch(() =>
        {
            var entry = new Entry { Info = info, Wired = wired };
            _entries[info.Id] = entry;
            SetConnection(entry, wired || IsConnected(info), _ready.Contains(watcher));
        });
        watcher.Updated += (_, update) => Dispatch(() =>
        {
            if (!_entries.TryGetValue(update.Id, out var entry)) return;
            entry.Info.Update(update);
            SetConnection(entry, entry.Wired || IsConnected(entry.Info), _ready.Contains(watcher));
        });
        watcher.Removed += (_, update) => Dispatch(() =>
        {
            if (_entries.Remove(update.Id, out var entry)) SetConnection(entry, false, false);
        });
        // Devices already connected at launch establish the baseline, without a toast storm.
        watcher.EnumerationCompleted += (_, _) => Dispatch(() => _ready.Add(watcher));
        watcher.Start();
    }

    private void Dispatch(Action action)
    {
        if (_dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() => { if (!_disposed) action(); });
    }

    private static bool IsConnected(DeviceInformation info) => info.Properties.TryGetValue(ConnectedProperty, out var value) && value is true;

    private void SetConnection(Entry entry, bool connected, bool announce)
    {
        var group = Container(entry.Info) is { } container ? container.ToString("B") : entry.Info.Id;
        if (entry.IsConnected == connected && (!connected || string.Equals(entry.Group, group, StringComparison.OrdinalIgnoreCase))) return;
        var alreadyAnnounced = entry.IsConnected && _connections.IsAnnounced(entry.Group);
        if (entry.IsConnected) _connections.Disconnect(entry.Group, entry.Info.Id);
        entry.IsConnected = connected;
        var revision = ++entry.Revision;
        if (!connected) return;
        entry.Group = group;
        if (_connections.Connect(entry.Group, entry.Info.Id, announce && !alreadyAnnounced)) _ = RecognizeConnectionAsync(entry, revision);
    }

    private async Task RecognizeConnectionAsync(Entry entry, int revision)
    {
        try
        {
            // Windows sometimes publishes the connection before its PnP profile metadata.
            foreach (var delay in new[] { 0, 300, 900 })
            {
                if (delay != 0) await Task.Delay(delay);
                if (_disposed || !entry.IsConnected || entry.Revision != revision) return;
                var identity = await ReadIdentityAsync(entry.Info, entry.Wired);
                if (_disposed || !entry.IsConnected || entry.Revision != revision) return;
                if (identity is null) continue;
                if (_connections.TryAnnounce(entry.Group, entry.Info.Id)) Connected?.Invoke(this, identity);
                return;
            }
        }
        catch (Exception ex) { Log.Error("Read Apple device identity", ex); }
    }

    internal static Guid? Container(DeviceInformation info)
    {
        foreach (var key in new[] { AepContainerProperty, ContainerProperty })
            if (info.Properties.TryGetValue(key, out var value) && Guid.TryParse(value?.ToString(), out var id) && id != Guid.Empty) return id;
        return null;
    }

    internal static async Task<AppleDeviceConnection?> ReadIdentityAsync(DeviceInformation info, bool wired)
    {
        var hardwareIds = HardwareIds(info).ToList();
        var name = info.Name;
        if (Container(info) is { } container)
        {
            var devices = await DeviceInformation.FindAllAsync($"System.Devices.ContainerId:=\"{container:B}\"", new[] { "System.Devices.HardwareIds" }, DeviceInformationKind.Device);
            foreach (var device in devices)
            {
                hardwareIds.AddRange(HardwareIds(device));
                if (wired && (device.Name.Contains("iPhone", StringComparison.OrdinalIgnoreCase) || device.Name.Contains("iPad", StringComparison.OrdinalIgnoreCase))) name = device.Name;
            }
        }
        return AppleDeviceIdentity.Recognize(Container(info)?.ToString("B") ?? info.Id, name, hardwareIds, wired);
    }

    private static IEnumerable<string> HardwareIds(DeviceInformation info)
    {
        yield return info.Id;
        if (!info.Properties.TryGetValue("System.Devices.HardwareIds", out var value)) yield break;
        if (value is string[] ids) foreach (var id in ids) yield return id;
        else if (value is string id) yield return id;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var watcher in _watchers)
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop();
        _watchers.Clear(); _ready.Clear(); _entries.Clear(); _connections.Clear();
    }
}

// Windows exposes several profiles for one physical device. Any connected
// profile may supply the identity, but only the first recognized one announces.
internal sealed class AppleConnectionGroups
{
    private readonly Dictionary<string, HashSet<string>> _members = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _announced = new(StringComparer.OrdinalIgnoreCase);
    internal bool IsAnnounced(string group) => _announced.Contains(group);
    internal bool Connect(string group, string member, bool announce)
    {
        if (!_members.TryGetValue(group, out var members)) _members[group] = members = new(StringComparer.OrdinalIgnoreCase);
        members.Add(member);
        if (!announce) _announced.Add(group);
        return announce && !_announced.Contains(group);
    }
    internal bool TryAnnounce(string group, string member) => _members.TryGetValue(group, out var members)
        && members.Contains(member) && _announced.Add(group);
    internal void Disconnect(string group, string member)
    {
        if (!_members.TryGetValue(group, out var members)) return;
        members.Remove(member);
        if (members.Count == 0) { _members.Remove(group); _announced.Remove(group); }
    }
    internal void Clear() { _members.Clear(); _announced.Clear(); }
}
