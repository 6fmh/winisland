using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Winisland.Services;

// A private, never switched-to Windows desktop owns all verification HWNDs.
// Failure to isolate is a test failure; there is no visible-desktop fallback.
internal static class BackgroundVerification
{
    internal static bool IsWorker => Environment.GetCommandLineArgs().Contains("--verification-worker");
    internal static bool IsInvocation(string[] args) => args.Contains("--self-test") || args.Contains("--control-center-test") || args.Contains("--startup-test");

    internal static void SuppressSystemPrompts() => SetErrorMode(0x0001 | 0x0002 | 0x8000);

    internal static int Run(string[] args)
    {
        var desktopName = "Winisland.Verification." + Guid.NewGuid().ToString("N");
        var desktop = CreateDesktop(desktopName, IntPtr.Zero, IntPtr.Zero, 0, 0x01FF, IntPtr.Zero);
        if (desktop == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the background verification desktop.");
        IntPtr job = IntPtr.Zero;
        var process = new ProcessInformation();
        try
        {
            job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits = new JobLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable path.");
            var entrypoint = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? Quote(executable) + " " + Quote(Path.Combine(AppContext.BaseDirectory, "Winisland.dll"))
                : Quote(executable);
            var command = new StringBuilder(entrypoint + " " + string.Join(" ", args.Append("--verification-worker").Select(Quote)));
            var startup = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfo>(), Desktop = "WinSta0\\" + desktopName };
            // Create suspended so the job owns the worker before it can spawn fixtures.
            if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false,
                0x00000004 | 0x08000000 | 0x00004000, IntPtr.Zero, null, ref startup, out process))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not launch background verification.");
            if (!AssignProcessToJobObject(job, process.Process))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not contain verification fixtures.");
            if (ResumeThread(process.Thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (WaitForSingleObject(process.Process, 180000) != 0)
                throw new TimeoutException("Background verification exceeded three minutes. Its worker and fixtures were stopped.");
            if (!GetExitCodeProcess(process.Process, out var code)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return unchecked((int)code);
        }
        finally
        {
            if (process.Process != IntPtr.Zero)
            {
                // Also handles assignment failure while the worker is suspended.
                TerminateProcess(process.Process, 1);
                WaitForSingleObject(process.Process, 5000);
            }
            if (job != IntPtr.Zero) CloseHandle(job);
            if (process.Thread != IntPtr.Zero) CloseHandle(process.Thread);
            if (process.Process != IntPtr.Zero) CloseHandle(process.Process);
            CloseDesktop(desktop);
        }
    }

    internal static void RequirePrivateDesktop()
    {
        var current = DesktopName(GetThreadDesktop(GetCurrentThreadId()));
        var input = OpenInputDesktop(0, false, 0x0001);
        if (input == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not verify desktop isolation.");
        try
        {
            if (!current.StartsWith("Winisland.Verification.", StringComparison.Ordinal)
                || current.Equals(DesktopName(input), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Verification must run on its private background desktop.");
        }
        finally { CloseDesktop(input); }
    }

    internal static string CurrentDesktop => DesktopName(GetThreadDesktop(GetCurrentThreadId()));

    internal static void WriteFailure(string[] args, Exception error)
    {
        var index = Array.IndexOf(args, "--output");
        var directory = Path.GetFullPath(index >= 0 && index + 1 < args.Length ? args[index + 1] : "artifacts/verification");
        Directory.CreateDirectory(directory);
        var reportName = args.Contains("--control-center-test") ? "control-center-results.json" : "results.json";
        File.WriteAllText(Path.Combine(directory, reportName), JsonSerializer.Serialize(new { passed = false, mode = "background", error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string DesktopName(IntPtr desktop)
    {
        var name = new StringBuilder(256);
        if (desktop == IntPtr.Zero || !GetUserObjectInformation(desktop, 2, name, (uint)(name.Capacity * 2), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return name.ToString();
    }

    private static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Size;
        public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct JobLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr security);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, uint length, out uint needed);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string? directory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobLimits limits, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
