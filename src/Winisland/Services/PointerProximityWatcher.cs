using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Winisland.Services;

// Raw input observes movement without intercepting legacy mouse input, changing
// the cursor or polling when it is stationary. Only this process registers it.
internal sealed class PointerProximityWatcher : IDisposable
{
    private const int WmInput = 0x00FF;
    private readonly HwndSource _source;
    private readonly Action<Point> _moved;
    private readonly Action _readPointer;
    private bool _queued, _disposed;
    private long _lastRead;
    internal bool Registered { get; }

    internal PointerProximityWatcher(HwndSource source, Action<Point> moved)
    {
        _source = source; _moved = moved;
        _readPointer = ReadPointer;
        var device = new RawInputDevice { UsagePage = 1, Usage = 2, Flags = 0x100, Target = source.Handle }; // INPUTSINK
        Registered = RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<RawInputDevice>());
        if (Registered) _source.AddHook(OnMessage);
    }

    private IntPtr OnMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmInput && !_disposed && !_queued && Environment.TickCount64 - _lastRead >= 8)
        {
            _queued = true;
            _source.Dispatcher.BeginInvoke(_readPointer, DispatcherPriority.Input);
        }
        // Leave native cleanup and ordinary WPF mouse delivery intact.
        return IntPtr.Zero;
    }

    private void ReadPointer()
    {
        _queued = false;
        if (_disposed) return;
        _lastRead = Environment.TickCount64;
        if (GetCursorPos(out var point)) _moved(new Point(point.X, point.Y));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!Registered) return;
        _source.RemoveHook(OnMessage);
        var device = new RawInputDevice { UsagePage = 1, Usage = 2, Flags = 1 }; // REMOVE
        RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<RawInputDevice>());
    }

    [StructLayout(LayoutKind.Sequential)] private struct RawInputDevice
    { internal ushort UsagePage, Usage; internal uint Flags; internal IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
}
