using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Threading;
using Winisland.Models;

namespace Winisland.Services;

/// <summary>
/// Observes visible Windows shell toast banners, without opening Notification
/// Center or reading its history. Unpackaged desktop apps cannot declare the
/// userNotificationListener package capability. Shell accessibility preserves
/// Windows' own banner, privacy, sound and Do not disturb preferences.
/// </summary>
public sealed class WindowsNotificationService : IDisposable
{
    private const uint ObjectShow = 0x8002, ObjectHide = 0x8003, NameChange = 0x800C;
    private static readonly string[] HistoryMarkers =
    {
        "NotificationCenter", "ActionCenterPage", "NotificationList", "NotificationHistory", "QuickSettings", "ControlCenter"
    };
    private readonly Dispatcher _dispatcher;
    private readonly Func<IntPtr, bool>? _testClassifier;
    private readonly BlockingCollection<IntPtr> _pending = new(4);
    private readonly ConcurrentDictionary<IntPtr, byte> _queued = new();
    private readonly Dictionary<IntPtr, (string Digest, long At)> _recent = new();
    private readonly object _recentLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly WinEventCallback _callback;
    private IntPtr _visibilityHook, _nameHook;
    private Thread? _worker;
    private volatile bool _disposed;
    private volatile bool _enabled;
    private long _generation;
    private long _sequence;
    public event EventHandler<NotchNotification>? Received;
    public bool IsListening => _visibilityHook != IntPtr.Zero;
    public string Status { get; private set; } = "Not started";

    public WindowsNotificationService(Dispatcher dispatcher) : this(dispatcher, null) { }
    internal WindowsNotificationService(Dispatcher dispatcher, Func<IntPtr, bool>? classifier)
    {
        _dispatcher = dispatcher; _testClassifier = classifier; _callback = OnNativeEvent;
    }

    public void Start()
    {
        _dispatcher.VerifyAccess();
        if (_disposed || IsListening) return;
        // Out-of-context hooks run on this dispatcher's message loop. Their
        // callbacks only inspect HWND metadata; UIA work stays off the UI thread.
        _visibilityHook = SetWinEventHook(ObjectShow, ObjectHide, IntPtr.Zero, _callback, 0, 0, 0);
        _nameHook = SetWinEventHook(NameChange, NameChange, IntPtr.Zero, _callback, 0, 0, 0);
        if (_visibilityHook == IntPtr.Zero || _nameHook == IntPtr.Zero)
        {
            if (_visibilityHook != IntPtr.Zero) UnhookWinEvent(_visibilityHook);
            if (_nameHook != IntPtr.Zero) UnhookWinEvent(_nameHook);
            _visibilityHook = _nameHook = IntPtr.Zero;
            Status = "Windows notification banners unavailable"; return;
        }
        _enabled = true;
        Interlocked.Increment(ref _generation);
        if (_worker is null)
        {
            _worker = new Thread(Watch) { IsBackground = true, Name = "Winisland notification accessibility" };
            _worker.SetApartmentState(ApartmentState.MTA);
            _worker.Start();
        }
        Status = "Listening for Windows notification banners";
    }

    private void OnNativeEvent(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        if (_disposed || !_enabled || window == IntPtr.Zero) return;
        var root = GetAncestor(window, 2);
        if (root == IntPtr.Zero) root = window;
        if (!IsCandidate(root)) return;
        if (eventType == ObjectHide)
        {
            if (window == root && objectId == 0 && childId == 0)
                lock (_recentLock) _recent.Remove(root);
            return;
        }
        Enqueue(root);
    }

    private bool IsCandidate(IntPtr window)
    {
        if (_testClassifier is not null) return _testClassifier(window);
        var name = new StringBuilder(128);
        GetClassName(window, name, name.Capacity);
        if (name.ToString() is not ("Windows.UI.Core.CoreWindow" or "XamlExplorerHostIslandWindow"
            or "Windows.UI.Composition.DesktopWindowContentBridge" or "ToastWndClass")) return false;
        GetWindowThreadProcessId(window, out var id);
        try
        {
            using var process = Process.GetProcessById((int)id);
            return process.ProcessName is "ShellExperienceHost" or "ShellHost" or "explorer" or "NotificationUxBroker";
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    private void Enqueue(IntPtr window)
    {
        if (!_queued.TryAdd(window, 0)) return;
        if (!_pending.TryAdd(window)) _queued.TryRemove(window, out _);
    }

    // An injected HWND classifier exercises the real native hook and UIA tree
    // in a fixture on the guarded verification desktop, never the user's shell.
    internal void ObserveWindowForTest(IntPtr window) => OnNativeEvent(IntPtr.Zero, ObjectShow, window, 0, 0, 0, 0);

    private void Watch()
    {
        try
        {
            foreach (var window in _pending.GetConsumingEnumerable(_lifetime.Token))
            {
                var generation = Interlocked.Read(ref _generation);
                try
                {
                    // Shell UI can announce its HWND before populating text.
                    // Retry only this event's window, for less than one second.
                    for (var attempt = 0; attempt < 3 && !_disposed && _enabled && generation == Interlocked.Read(ref _generation); attempt++)
                    {
                        if (_lifetime.Token.WaitHandle.WaitOne(attempt == 0 ? 100 : 250)) break;
                        if (!IsWindowVisible(window) || !IsCandidate(window)) break;
                        var notification = ReadToast(window);
                        if (notification is null) continue;
                        if (Admit(window, notification))
                            _dispatcher.BeginInvoke(() =>
                            {
                                if (!_disposed && _enabled && generation == Interlocked.Read(ref _generation)) Received?.Invoke(this, notification);
                            }, DispatcherPriority.Background);
                        break;
                    }
                }
                catch (Exception error) when (error is ElementNotAvailableException or COMException or InvalidOperationException or ArgumentException)
                { /* A dismissed or unavailable shell banner is normal. No content is logged. */ }
                finally { _queued.TryRemove(window, out _); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private bool Admit(IntPtr window, NotchNotification notification)
    {
        var digest = Digest(notification);
        var now = Environment.TickCount64;
        lock (_recentLock)
        {
            if (_recent.TryGetValue(window, out var previous) && previous.Digest == digest && now - previous.At < 15000) return false;
            foreach (var key in _recent.Where(pair => now - pair.Value.At >= 15000).Select(pair => pair.Key).ToArray()) _recent.Remove(key);
            if (_recent.Count >= 16) _recent.Remove(_recent.Keys.First());
            _recent[window] = (digest, now); return true;
        }
    }

    private NotchNotification? ReadToast(IntPtr window)
    {
        var root = AutomationElement.FromHandle(window);
        if (root is null) return null;
        var rootState = root.Current;
        var bounds = rootState.BoundingRectangle;
        // ToastCenter may reserve a monitor-sized transparent host around a
        // small visible toast (also handled by NativeMethods' fullscreen test).
        // Host bounds are not card bounds. Trust is bounded by shell identity,
        // live-toast markers, visibility and the traversal/text limits below.
        if (rootState.IsOffscreen || bounds.IsEmpty || bounds.Width < 160 || bounds.Height < 40) return null;
        var nodes = new List<NotificationText>();
        var stack = new Stack<(AutomationElement Node, int Depth)>();
        stack.Push((root, 0));
        var visited = 0;
        AutomationElement? invocation = null;
        while (stack.Count > 0 && visited++ < 160)
        {
            var (node, depth) = stack.Pop();
            var current = node.Current;
            if (depth > 0 && current.IsOffscreen) continue;
            nodes.Add(new NotificationText(current.AutomationId, current.ClassName, current.Name,
                current.ControlType == ControlType.Text, current.ControlType == ControlType.Image));
            if (invocation is null && (IsToastMarker(current.AutomationId) || IsToastMarker(current.ClassName))
                && node.TryGetCurrentPattern(InvokePattern.Pattern, out _)) invocation = node;
            if (depth >= 8) continue;
            var children = new List<AutomationElement>();
            for (var child = TreeWalker.ControlViewWalker.GetFirstChild(node); child is not null && children.Count < 32;
                child = TreeWalker.ControlViewWalker.GetNextSibling(child)) children.Add(child);
            for (var i = children.Count - 1; i >= 0; i--) stack.Push((children[i], depth + 1));
        }
        var notification = Parse(nodes, $"windows:{window.ToInt64():x}:{Interlocked.Increment(ref _sequence)}");
        if (notification is not null) notification = notification with { Icon = NotificationIconResolver.Resolve(notification.AppName) };
        if (notification is not null && invocation is not null)
        {
            var expected = Digest(notification);
            var target = invocation;
            var generation = Interlocked.Read(ref _generation);
            notification = notification with
            {
                Activate = () => _ = Task.Run(() =>
                {
                    // Explicit user action only. A shell HWND can be reused by
                    // the next toast; never invoke it using an old preview.
                    try
                    {
                        if (_disposed || !_enabled || generation != Interlocked.Read(ref _generation)
                            || !IsWindowVisible(window) || !IsCandidate(window)) return;
                        var current = ReadToast(window);
                        if (current is null || Digest(current) != expected) return;
                        if (target.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)) ((InvokePattern)pattern).Invoke();
                    }
                    catch (Exception error) when (error is ElementNotAvailableException or COMException or InvalidOperationException or ArgumentException)
                    { }
                })
            };
        }
        return notification;
    }

    private static string Digest(NotchNotification notification) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(notification.AppName + "\0" + notification.Title + "\0" + notification.Body)));

    internal sealed record NotificationText(string Id, string Class, string Name, bool IsText, bool IsImage = false);
    internal static NotchNotification? Parse(IReadOnlyList<NotificationText> nodes, string id)
    {
        // Toast-specific IDs/classes prevent unrelated shell flyouts (Quick
        // Settings, Start, Notification Center) becoming notification content.
        if (nodes.Any(node => IsHistoryMarker(node.Id) || IsHistoryMarker(node.Class)
                || node.Name.Equals("Notification Center", StringComparison.OrdinalIgnoreCase))
            || !nodes.Any(node => IsToastMarker(node.Id) || IsToastMarker(node.Class))) return null;
        var texts = nodes.Where(node => node.IsText && !string.IsNullOrWhiteSpace(node.Name)).Take(12).ToArray();
        if (texts.Length == 0) return null;
        static bool Contains(string value, string part) => value.Contains(part, StringComparison.OrdinalIgnoreCase);
        var source = nodes.FirstOrDefault(node => Contains(node.Id, "AppName") || Contains(node.Id, "AppLogo") || Contains(node.Id, "SourceName"));
        var sourceName = Clean(source?.Name, 80);
        var titleNode = texts.FirstOrDefault(node => node.Id == "1" || Contains(node.Id, "TextField1") || Contains(node.Id, "Title"));
        var bodyNodes = texts.Where(node => node.Id is "2" or "3" || Contains(node.Id, "TextField2") || Contains(node.Id, "TextField3") || Contains(node.Id, "Body")).ToArray();
        if (sourceName.Length == 0 && titleNode is null && texts.Length >= 3) sourceName = Clean(texts[0].Name, 80);
        var content = texts.Where(node => node != source && node.Name != sourceName).ToArray();
        var title = Clean(titleNode?.Name ?? content.FirstOrDefault()?.Name, 180);
        var body = Clean(string.Join("\n", (bodyNodes.Length > 0 ? bodyNodes : content.Where(node => node != titleNode && node.Name != title)).Select(node => node.Name)), 600);
        if (title.Length == 0 && body.Length == 0) return null;
        return new NotchNotification { Id = id, AppName = sourceName.Length == 0 ? "Notification" : sourceName, Title = title, Body = body };
    }

    private static bool IsToastMarker(string value)
    {
        // Current Windows.UI.ActionCenter.dll (26100.9818) embeds these XAML
        // classes and the ToastView/FlexibleToastView accessibility peers.
        var name = value[(value.LastIndexOf('.') + 1)..];
        return name is "ToastView" or "ToastContentView" or "ToastCenterPage" or "FlexibleToastView"
            or "hNormalToastView" or "PriorityToastView" or "FlexibleNormalToastView" or "FlexiblePriorityToastView"
            or "FlexiblePriorityToastView2" or "FlexiblePriorityToastView3"
            or "NotificationBanner" or "ToastTextField1" or "ToastTextField2" or "ToastTextField3";
    }
    private static bool IsHistoryMarker(string value) => value.Equals("ActionCenter", StringComparison.OrdinalIgnoreCase)
        || HistoryMarkers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
    private static string Clean(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var clean = new string(value.Where(character => !char.IsControl(character) || character is '\n' or '\t').ToArray()).Trim();
        return clean.Length <= maximum ? clean : clean[..maximum];
    }

    public void Stop()
    {
        _dispatcher.VerifyAccess();
        _enabled = false;
        Interlocked.Increment(ref _generation);
        if (_visibilityHook != IntPtr.Zero) UnhookWinEvent(_visibilityHook);
        if (_nameHook != IntPtr.Zero) UnhookWinEvent(_nameHook);
        _visibilityHook = _nameHook = IntPtr.Zero;
        while (_pending.TryTake(out _)) { }
        _queued.Clear();
        lock (_recentLock) _recent.Clear();
        Status = "Stopped";
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        Stop(); _disposed = true;
        _lifetime.Cancel(); _pending.CompleteAdding();
        // Do not join an out-of-process UIA call on WPF's dispatcher. The worker
        // is a background thread and drops results immediately after disposal.
    }

    private delegate void WinEventCallback(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint minimum, uint maximum, IntPtr module, WinEventCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
}
