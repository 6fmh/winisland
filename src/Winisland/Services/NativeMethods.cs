using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace Winisland.Services;

internal static class NativeMethods
{
    internal const int WmHotkey = 0x0312;
    internal const int WmDpiChanged = 0x02E0;
    internal const int WmDisplayChange = 0x007E;
    internal const int WmMouseActivate = 0x0021;
    internal const int WmDwmCompositionChanged = 0x031E;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x08000000;
    private const int DwmwaExcludedFromPeek = 12;
    private const int DwmwaCloaked = 14;
    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ShellExperienceHost", "ShellHost", "StartMenuExperienceHost", "SearchHost", "SearchApp", "Widgets",
        // The text/touch input flyouts also use monitor-sized transparent hosts.
        "TextInputHost", "InputApp"
    };

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr handle, int index, int value);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr handle);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(PointNative point, uint flags);
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr handle, int id);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] internal static extern IntPtr SetActiveWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out RectNative rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out RectNative rect, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder name, int count);
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out PowerStatus status);

    [StructLayout(LayoutKind.Sequential)] private struct PointNative { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RectNative { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct PowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    internal static bool ConfigureOverlay(IntPtr handle)
    {
        SetWindowLong(handle, GwlExStyle, GetWindowLong(handle, GwlExStyle) | WsExToolWindow | WsExNoActivate);
        // Taskbar thumbnail/desktop previews should leave the notch visible.
        var excludedFromPeek = 1;
        return DwmSetWindowAttribute(handle, DwmwaExcludedFromPeek, ref excludedFromPeek, sizeof(int)) == 0;
    }
    internal static void SetActivatable(IntPtr handle, bool activate)
    {
        var style = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, activate ? style & ~WsExNoActivate : style | WsExNoActivate);
    }

    internal static void RestoreOverlay(IntPtr handle)
    {
        if (IsIconic(handle)) ShowWindow(handle, 4); // SW_SHOWNOACTIVATE
        SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0,
            0x0001 | 0x0002 | 0x0010 | 0x0040); // No size/move/activation; show and restore topmost.
    }

    internal static Forms.Screen ResolveScreen(string device)
    {
        foreach (var screen in Forms.Screen.AllScreens)
            if (screen.DeviceName == device) return screen;
        return Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];
    }

    internal static void PositionOverlay(Window window, string device)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var screen = ResolveScreen(device);
        var monitor = MonitorFromPoint(new PointNative { X = screen.Bounds.Left + screen.Bounds.Width / 2, Y = screen.Bounds.Top + 1 }, 2);
        var scale = 1.0;
        if (GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0) scale = dpi / 96.0;
        else scale = GetDpiForWindow(handle) / 96.0;
        var width = (int)Math.Round(window.Width * scale);
        var height = (int)Math.Round(window.Height * scale);
        SetWindowPos(handle, new IntPtr(-1), screen.Bounds.Left + (screen.Bounds.Width - width) / 2,
            screen.Bounds.Top, width, height, 0x0010 | 0x0040);
    }

    internal static bool IsFullscreenOn(string device, IntPtr ownHandle)
    {
        var foreground = GetForegroundWindow();
        // Opening Quick Settings can change focus while the previous app's
        // bounds/process are being inspected. Do not hide for that stale app.
        return IsFullscreenWindowOn(foreground, device, ownHandle) && foreground == GetForegroundWindow();
    }

    internal static bool IsFullscreenWindowOn(IntPtr foreground, string device, IntPtr ownHandle)
    {
        if (foreground == IntPtr.Zero || foreground == ownHandle) return false;
        // Foreground events can arrive while a shell flyout is being dismissed.
        // Hidden/cloaked hosts retain their fullscreen bounds during that handoff.
        if (!IsWindowVisible(foreground) || IsIconic(foreground)) return false;
        if (DwmGetWindowAttribute(foreground, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        var name = new StringBuilder(128);
        if (GetClassName(foreground, name, name.Capacity) == 0) return false;
        if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "XamlExplorerHostIslandWindow" or "TopLevelWindowForOverflowXamlIsland" or "ControlCenterWindow") return false;
        if (DwmGetWindowAttribute(foreground, 9, out RectNative rect, Marshal.SizeOf<RectNative>()) != 0 && !GetWindowRect(foreground, out rect)) return false;
        var screen = ResolveScreen(device).Bounds;
        if (!(rect.Left <= screen.Left + 1 && rect.Top <= screen.Top + 1 && rect.Right >= screen.Right - 1 && rect.Bottom >= screen.Bottom - 1)) return false;
        // Start, Search and notification panels can have monitor-sized transparent
        // hosts. Identify their processes; CoreWindow also belongs to real apps.
        GetWindowThreadProcessId(foreground, out var processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            if (IsShellProcess(process.ProcessName)) return false;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // An exited or uninspectable host is not evidence of a fullscreen app.
            // The next foreground event/pulse will inspect its replacement.
            return false;
        }
        return true;
    }

    internal static bool IsShellProcess(string processName) => ShellProcesses.Contains(processName);

    internal static BatterySnapshot GetBattery()
    {
        if (!GetSystemPowerStatus(out var status)) return new(false, null, false, false);
        var hasBattery = status.BatteryFlag != 255 && (status.BatteryFlag & 128) == 0;
        return new(hasBattery, hasBattery && status.BatteryLifePercent != 255 ? status.BatteryLifePercent : null,
            status.ACLineStatus == 1, (status.BatteryFlag & 8) != 0 && status.BatteryFlag != 255);
    }
}

public sealed record BatterySnapshot(bool HasBattery, int? Percent, bool PluggedIn, bool Charging);
