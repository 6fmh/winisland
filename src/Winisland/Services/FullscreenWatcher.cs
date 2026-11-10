using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Winisland.Services;

// Out-of-context WinEvents are delivered on the thread that installs the hooks.
// Keep the delegate alive until every hook is removed on that same UI thread.
internal sealed class FullscreenWatcher : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventSystemMinimizeStart = 0x0016;
    private const uint EventSystemMinimizeEnd = 0x0017;
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectHide = 0x8003;
    private const uint EventObjectLocationChange = 0x800B;
    private readonly Dispatcher _dispatcher;
    private readonly Action _changed;
    private readonly IntPtr _ownHandle;
    private readonly WinEventProc _callback;
    private readonly Func<IntPtr> _getForeground;
    private readonly IntPtr[] _hooks = new IntPtr[4];
    private IntPtr _foreground;
    private bool _queued, _disposed;

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint threadId, uint eventTime);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    internal FullscreenWatcher(Dispatcher dispatcher, IntPtr ownHandle, Action changed, Func<IntPtr>? getForeground = null)
    {
        _dispatcher = dispatcher;
        _ownHandle = ownHandle;
        _changed = changed;
        _callback = OnWinEvent;
        _getForeground = getForeground ?? NativeMethods.GetForegroundWindow;
        _foreground = _getForeground();
        // Zero flags select WINEVENT_OUTOFCONTEXT; include our process so a
        // foreground window in the same process also receives resize updates.
        _hooks[0] = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _callback, 0, 0, 0);
        _hooks[1] = SetWinEventHook(EventSystemMinimizeStart, EventSystemMinimizeEnd, IntPtr.Zero, _callback, 0, 0, 0);
        _hooks[2] = SetWinEventHook(EventObjectDestroy, EventObjectHide, IntPtr.Zero, _callback, 0, 0, 0);
        _hooks[3] = SetWinEventHook(EventObjectLocationChange, EventObjectLocationChange, IntPtr.Zero, _callback, 0, 0, 0);
        foreach (var hook in _hooks)
            if (hook == IntPtr.Zero)
            {
                // The regular pulse remains a fallback if Windows denies a hook.
                Log.Error("Watch fullscreen window changes", new Win32Exception(Marshal.GetLastWin32Error()));
                break;
            }
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint threadId, uint eventTime)
    {
        if (_disposed) return;
        if (eventType != EventSystemForeground)
        {
            // Ignore controls, background windows and our own size animations.
            if (objectId != 0 || childId != 0 || window == IntPtr.Zero || window == _ownHandle) return;
            if (window != _foreground && window != _getForeground()) return;
        }
        _foreground = _getForeground();
        if (_queued || _dispatcher.HasShutdownStarted) return;
        _queued = true;
        // Coalesce event bursts and leave the native callback before changing WPF
        // visibility. Send priority processes this before the next render, with
        // no debounce or polling delay.
        _dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
        {
            _queued = false;
            if (!_disposed) _changed();
        }));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var hook in _hooks)
            if (hook != IntPtr.Zero) UnhookWinEvent(hook);
        GC.KeepAlive(_callback);
    }

    internal void NotifyForegroundChangedForTest() =>
        OnWinEvent(IntPtr.Zero, EventSystemForeground, _getForeground(), 0, 0, 0, 0);
}
