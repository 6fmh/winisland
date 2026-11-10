using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Winisland.Services;

internal static class StartupRegistration
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static readonly object Gate = new();
    private static string UserSid
    {
        get { using var identity = WindowsIdentity.GetCurrent(); return identity.User?.Value ?? throw new InvalidOperationException("Cannot determine the Windows account."); }
    }
    internal static string TaskName => "Winisland-" + UserSid;

    internal static string BuildTaskXml(string application, string sid)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement Element(string name, object? value) => new(ns + name, value);
        return new XDocument(new XElement(ns + "Task", new XAttribute("version", "1.2"),
            Element("RegistrationInfo", Element("Description", "Start Winisland immediately when this Windows account signs in.")),
            Element("Triggers", new XElement(ns + "LogonTrigger", Element("Enabled", true), Element("UserId", sid), Element("Delay", "PT0S"))),
            Element("Principals", new XElement(ns + "Principal", new XAttribute("id", "User"), Element("UserId", sid),
                Element("LogonType", "InteractiveToken"), Element("RunLevel", "LeastPrivilege"))),
            Element("Settings", new[] { Element("MultipleInstancesPolicy", "IgnoreNew"), Element("DisallowStartIfOnBatteries", false),
                Element("StopIfGoingOnBatteries", false), Element("StartWhenAvailable", true), Element("RunOnlyIfNetworkAvailable", false),
                Element("Enabled", true), Element("ExecutionTimeLimit", "PT0S") }),
            new XElement(ns + "Actions", new XAttribute("Context", "User"), new XElement(ns + "Exec",
                Element("Command", application), Element("Arguments", "--background"), Element("WorkingDirectory", Path.GetDirectoryName(application)))))).ToString();
    }

    internal static Change BeginChange(bool enabled) => new(enabled);
    internal static void EnsureEnabled()
    {
        using var change = BeginChange(true);
        if (!new SettingsStore().Current.StartWithWindows) return;
        change.Apply(); change.Commit();
    }
    internal static int Configure(string value)
    {
        try
        {
            if (BackgroundVerification.IsWorker) return 1;
            var settings = new SettingsStore();
            var enabled = value switch { "true" => true, "false" => false, "preserve" => settings.Current.StartWithWindows, _ => throw new ArgumentException("Invalid startup preference.") };
            // Installer paths must be refreshed even if the preference did not
            // change, including moves from a portable copy and legacy Run keys.
            using var change = BeginChange(enabled);
            change.Apply();
            settings.Update(settings.Current with { StartWithWindows = enabled });
            change.Commit();
            return 0;
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Security.SecurityException)
        { Log.Error("Configure sign-in startup", ex); return 1; }
    }

    internal sealed class Change : IDisposable
    {
        private readonly bool _enabled;
        private readonly string? _previousTask;
        private readonly RegistryKey _run;
        private readonly object? _previousRun;
        private readonly RegistryValueKind _previousKind;
        private bool _applied, _committed, _disposed;
        internal Change(bool enabled)
        {
            Monitor.Enter(Gate);
            try
            {
                _enabled = enabled;
                using (var scheduler = new Scheduler()) _previousTask = scheduler.Read();
                _run = Registry.CurrentUser.CreateSubKey(RunKey);
                _previousRun = _run.GetValue("Winisland", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                _previousKind = _previousRun is null ? RegistryValueKind.None : _run.GetValueKind("Winisland");
            }
            catch { Monitor.Exit(Gate); throw; }
        }
        internal void Apply()
        {
            if (_applied) return;
            _applied = true;
            using var scheduler = new Scheduler();
            if (_enabled) scheduler.Write(BuildTaskXml(Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the app location."), UserSid));
            else scheduler.Delete();
            _run.DeleteValue("Winisland", false);
        }
        internal void Commit() => _committed = true;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_applied && !_committed)
                {
                    using var scheduler = new Scheduler();
                    if (_previousTask is null) scheduler.Delete(); else scheduler.Write(_previousTask);
                    if (_previousRun is null) _run.DeleteValue("Winisland", false); else _run.SetValue("Winisland", _previousRun, _previousKind);
                }
            }
            catch (Exception ex) { Log.Error("Restore sign-in registration after a failed settings save", ex); }
            finally { _run.Dispose(); Monitor.Exit(Gate); }
        }
    }

    private sealed class Scheduler : IDisposable
    {
        private readonly dynamic _service;
        private readonly dynamic _folder;
        internal Scheduler()
        {
            _service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)! )!;
            _service.Connect(); _folder = _service.GetFolder(@"\");
        }
        internal string? Read()
        {
            object? task = null;
            try { task = _folder.GetTask(TaskName); return (string)((dynamic)task).Xml; }
            catch (COMException ex) when ((uint)ex.HResult == 0x80070002) { return null; }
            finally { if (task is not null) Marshal.FinalReleaseComObject(task); }
        }
        internal void Write(string xml)
        {
            object task = _folder.RegisterTask(TaskName, xml, 6, UserSid, null, 3, null);
            Marshal.FinalReleaseComObject(task);
        }
        internal void Delete()
        {
            try { _folder.DeleteTask(TaskName, 0); }
            catch (COMException ex) when ((uint)ex.HResult == 0x80070002) { }
        }
        public void Dispose() { Marshal.FinalReleaseComObject(_folder); Marshal.FinalReleaseComObject(_service); }
    }
}
