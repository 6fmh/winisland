using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Threading;

namespace Winisland.Services;

public sealed class SpotifyPlayerVolume : IPlayerVolumeBackend, IPlayerVolumeNotifications, IDisposable
{
    private static readonly Regex VolumeName = new(@"volume|volym|lautst.rke|volumen|volum|громкост|音量|볼륨|음량", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly Func<AutomationElement?>? _rootProvider;
    private readonly AutomationDispatcher _automation = new();
    internal bool AutomationStartedForTest => _automation.Started;
    internal bool AutomationThreadAliveForTest => _automation.ThreadAlive;
    private readonly SemaphoreSlim _presentationLock = new(1, 1);
    private AutomationElement? _slider;
    private AutomationPropertyChangedEventHandler? _volumeChanged;
    private sealed record VolumeReport(double Volume, long ReportedAt);
    private VolumeReport? _reportedVolume;
    private VolumeReport? _consumedReport;
    private string _targetId = "";
    private IntPtr _windowHandle;
    private uint _windowProcess;
    private bool _disposed;
    public SpotifyAccount? Account { get; }
    public event EventHandler? Changed;
    public SpotifyPlayerVolume()
    {
        Account = new SpotifyAccount();
        Account.Changed += OnAccountChanged;
    }
    internal SpotifyPlayerVolume(Func<AutomationElement?> rootProvider) => _rootProvider = rootProvider;
    private void OnAccountChanged(object? sender, EventArgs args) => Changed?.Invoke(this, EventArgs.Empty);
    public static bool IsSpotifySource(string source) => source.Equals("Spotify", StringComparison.OrdinalIgnoreCase)
        || source.Equals("Spotify.exe", StringComparison.OrdinalIgnoreCase)
        || source.StartsWith("SpotifyAB.SpotifyMusic_", StringComparison.OrdinalIgnoreCase)
        || source.StartsWith("SpotifyAB.SpotifyMusic!", StringComparison.OrdinalIgnoreCase);
    public bool Supports(MediaSnapshot media) => media.HasSession && IsSpotifySource(media.Source);

    public async Task<PlayerVolumeSnapshot> ReadAsync(MediaSnapshot media, CancellationToken cancellationToken)
    {
        var local = await ReadLocalAsync(media, cancellationToken);
        if (Account?.IsConnected != true) return local;
        var report = Volatile.Read(ref _reportedVolume);
        if (report is not null && report != _consumedReport)
        {
            Account.ObserveLocalVolume(report.Volume, report.ReportedAt);
            _consumedReport = report;
        }
        return await Account.ReadAsync(media, cancellationToken);
    }

    private Task<PlayerVolumeSnapshot> ReadLocalAsync(MediaSnapshot media, CancellationToken cancellationToken) => _automation.InvokeAsync(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = new PlayerVolumeSnapshot { Source = media.Source, Player = "Spotify", ConnectionHint = "Open Player controls in Settings and choose Connect Spotify." };
        if (_disposed || !Supports(media)) return snapshot;
        try
        {
            if (_slider is null && !FindSlider()) return snapshot;
            var volume = ReadSlider(_slider!);
            return volume is null ? snapshot : snapshot with { Available = true, Volume = Math.Round(volume.Value, 4), Step = ReadStep(_slider!), Muted = volume <= .001, TargetId = _targetId, ConnectionHint = "Spotify player volume" };
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException)
        { ForgetSlider(); return snapshot; }
    }, cancellationToken);

    public async Task<bool> WriteAsync(PlayerVolumeSnapshot desired, CancellationToken cancellationToken)
    {
        if (desired.TargetId.StartsWith("spotify-api:", StringComparison.Ordinal))
            return Account is not null && await Account.WriteAsync(desired, cancellationToken);
        await _presentationLock.WaitAsync(cancellationToken);
        try { return await WriteWithPresentationAsync(desired, cancellationToken); }
        finally { _presentationLock.Release(); }
    }

    private async Task<bool> WriteWithPresentationAsync(PlayerVolumeSnapshot desired, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed) return false;
        using var presentation = _rootProvider is null ? BackgroundPresentation.Open(_windowHandle, _windowProcess) : null;
        if (presentation is not null) await Task.Delay(100, cancellationToken);
        var foreground = CaptureActionForeground(presentation);
        var expected = await _automation.InvokeAsync(() => WriteSlider(desired, cancellationToken, presentation), cancellationToken);
        if (expected is null) return false;
        // Chromium acknowledges the accessibility action before publishing its
        // updated tree. Give its renderer one quiet update interval first.
        await WaitForPlayerUpdateAsync(foreground, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var started = Environment.TickCount64;
        while (!_disposed && Environment.TickCount64 - started < 1600)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var readback = await _automation.InvokeAsync(() =>
            {
                try { return _slider is null ? null : ReadSlider(_slider); }
                catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException) { ForgetSlider(); return null; }
            }, cancellationToken);
            if (readback is not null && Math.Abs(readback.Value - expected.Value) < .051) return true;
            if (readback is null) return false;
            await Task.Delay(100, cancellationToken);
        }
        return false;
    }

    public async Task<bool> PrepareAccessibilityAsync(CancellationToken cancellationToken = default)
    {
        await _presentationLock.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed) return false;
            var found = await _automation.InvokeAsync(FindSpotifyWindow, cancellationToken);
            if (!found) return false;
            using var presentation = BackgroundPresentation.Open(_windowHandle, _windowProcess);
            await Task.Delay(650, cancellationToken);
            return await _automation.InvokeAsync(FindSlider, cancellationToken);
        }
        finally { _presentationLock.Release(); }
    }

    private double? WriteSlider(PlayerVolumeSnapshot desired, CancellationToken cancellationToken, BackgroundPresentation? presentation)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed || _slider is null || desired.TargetId != _targetId || !double.IsFinite(desired.Volume)) return null;
        try
        {
            var level = desired.Muted ? 0 : Math.Clamp(desired.Volume, 0, 100);
            if (_slider.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rangeObject))
            {
                var pattern = (RangeValuePattern)rangeObject;
                var range = pattern.Current;
                if (range.IsReadOnly || range.Maximum <= range.Minimum) return null;
                var step = ReadStep(_slider);
                if (step > .1) level = Math.Clamp(Math.Round(level / step, MidpointRounding.AwayFromZero) * step, 0, 100);
                // Always send the requested target: Chromium can report an old
                // range snapshot immediately after the preceding volume action.
                var nativeLevel = range.Minimum + level / 100 * (range.Maximum - range.Minimum);
                InvokePlayerAction(presentation, () => pattern.SetValue(nativeLevel));
                return level;
            }
            if (_slider.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject))
            {
                var pattern = (ValuePattern)valueObject;
                if (pattern.Current.IsReadOnly) return null;
                var nativeLevel = level.ToString("0.###", CultureInfo.InvariantCulture);
                InvokePlayerAction(presentation, () => pattern.SetValue(nativeLevel));
                return level;
            }
            return null;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException)
        { ForgetSlider(); return null; }
    }

    private void InvokePlayerAction(BackgroundPresentation? presentation, Action action)
    {
        if (_rootProvider is not null) { action(); return; }
        BackgroundPresentation.InvokePlayerAction(_windowHandle, presentation?.ForegroundBefore ?? IntPtr.Zero, action);
    }

    private sealed record ActionForeground(IntPtr Window, IntPtr InputWindow, uint InputTime, bool InputCaptured);

    private ActionForeground? CaptureActionForeground(BackgroundPresentation? presentation)
    {
        if (_rootProvider is not null) return null;
        var window = NativeWindow.GetForegroundWindow();
        if (window == _windowHandle) window = presentation?.ForegroundBefore ?? IntPtr.Zero;
        if (window == IntPtr.Zero || window == _windowHandle) return null;
        var input = new NativeWindow.LastInput { Size = (uint)Marshal.SizeOf<NativeWindow.LastInput>() };
        var captured = NativeWindow.GetLastInputInfo(ref input);
        var inputWindow = NativeWindow.GetCursorPos(out var pointer)
            ? NativeWindow.GetAncestor(NativeWindow.WindowFromPoint(pointer), 2) : IntPtr.Zero;
        return new ActionForeground(window, inputWindow, input.Time, captured);
    }

    private async Task WaitForPlayerUpdateAsync(ActionForeground? foreground, CancellationToken cancellationToken)
    {
        if (foreground is null) { await Task.Delay(400, cancellationToken); return; }
        var clock = Stopwatch.StartNew();
        var protect = true;
        var protectedWindow = foreground.Window;
        // Chromium can post activation after SetValue has already returned.
        // Keep the initiating window through the normal readback delay. Once
        // submitted, an action needs this cleanup even if its caller cancels.
        while (clock.ElapsedMilliseconds < 400)
        {
            if (protect)
            {
                var current = NativeWindow.GetForegroundWindow();
                if (current != IntPtr.Zero && current == foreground.InputWindow && current != _windowHandle)
                    protectedWindow = current;
                if (_disposed || !NativeWindow.IsWindow(protectedWindow) || !NativeWindow.IsWindowVisible(protectedWindow)
                    || (NativeWindow.GetWindowLong(protectedWindow, -20) & 0x08000000) != 0
                    || NativeWindow.IsSwitchingApplications()
                    || current != protectedWindow && current != _windowHandle && current != IntPtr.Zero)
                    protect = false;
                else if (current == _windowHandle)
                {
                    var input = new NativeWindow.LastInput { Size = (uint)Marshal.SizeOf<NativeWindow.LastInput>() };
                    var unchanged = foreground.InputCaptured && NativeWindow.GetLastInputInfo(ref input) && input.Time == foreground.InputTime;
                    var pointerAtOrigin = foreground.InputWindow != IntPtr.Zero && NativeWindow.GetCursorPos(out var pointer)
                        && NativeWindow.GetAncestor(NativeWindow.WindowFromPoint(pointer), 2) == foreground.InputWindow;
                    if (unchanged || pointerAtOrigin)
                        BackgroundPresentation.RestoreForeground(protectedWindow);
                    else protect = false;
                }
            }
            await Task.Delay((int)Math.Min(12, Math.Max(1, 400 - clock.ElapsedMilliseconds)));
        }
    }

    private static double ReadStep(AutomationElement element)
    {
        // Spotify's Chromium input sanitizes values to its declared range step.
        // WPF/native sliders can accept fractions even when keyboard steps are
        // larger, so only apply that Chromium constraint to Chromium providers.
        if (element.Current.FrameworkId == "Chrome" && element.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rangeObject))
        {
            var range = ((RangeValuePattern)rangeObject).Current;
            if (range.Maximum > range.Minimum && double.IsFinite(range.SmallChange) && range.SmallChange > 0)
                return Math.Clamp(Math.Round(100 * range.SmallChange / (range.Maximum - range.Minimum), 4), .1, 100);
        }
        return .1;
    }

    private bool FindSlider()
    {
        ForgetSlider();
        AutomationElement? root = _rootProvider?.Invoke();
        if (root is null)
        {
            if (FindSpotifyWindow())
            {
                    // Requesting the accessibility client is local to Spotify;
                    // screen-reader settings and Spotify files stay untouched.
                    var id = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
                    if (AccessibleObjectFromWindow(_windowHandle, 0xFFFFFFFC, ref id, out var client) >= 0 && client is not null && Marshal.IsComObject(client)) Marshal.ReleaseComObject(client);
                    root = AutomationElement.FromHandle(_windowHandle);
            }
        }
        if (root is null) return false;
        // Some Chromium builds expose ranges as Custom controls, so do not
        // restrict this query to the Slider control type.
        var ranges = new OrCondition(new PropertyCondition(AutomationElement.IsRangeValuePatternAvailableProperty, true),
            new PropertyCondition(AutomationElement.IsValuePatternAvailableProperty, true));
        var candidates = root.FindAll(TreeScope.Descendants, ranges);
        foreach (AutomationElement element in candidates)
        {
            var current = element.Current;
            if (!current.IsEnabled || !VolumeName.IsMatch(current.Name + " " + current.AutomationId)) continue;
            if (ReadSlider(element) is null) continue;
            _slider = element;
            _targetId = "spotify:" + string.Join(".", element.GetRuntimeId());
            var target = _targetId;
            var minimum = 0.0; var maximum = 100.0;
            if (element.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rangeObject))
            {
                var range = ((RangeValuePattern)rangeObject).Current;
                minimum = range.Minimum; maximum = range.Maximum;
            }
            _volumeChanged = (_, args) =>
            {
                if (_disposed || target != _targetId) return;
                double level;
                if (args.Property == RangeValuePattern.ValueProperty && args.NewValue is double value && maximum > minimum)
                    level = (value - minimum) / (maximum - minimum) * 100;
                else if (args.Property == ValuePattern.ValueProperty && args.NewValue is string text
                    && double.TryParse(text.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)) level = percent;
                else return;
                if (!double.IsFinite(level)) return;
                Volatile.Write(ref _reportedVolume, new VolumeReport(Math.Clamp(level, 0, 100), Environment.TickCount64));
                Changed?.Invoke(this, EventArgs.Empty);
            };
            try { Automation.AddAutomationPropertyChangedEventHandler(element, TreeScope.Element, _volumeChanged, RangeValuePattern.ValueProperty, ValuePattern.ValueProperty); }
            catch (Exception ex) when (ex is InvalidOperationException or COMException) { }
            return true;
        }
        return false;
    }

    private bool FindSpotifyWindow()
    {
        foreach (var process in Process.GetProcessesByName("Spotify"))
        {
            using (process)
            {
                var handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero)
                {
                    NativeWindow.EnumWindows((candidate, _) =>
                    {
                        NativeWindow.GetWindowThreadProcessId(candidate, out var owner);
                        if (owner != process.Id) return true;
                        var name = new System.Text.StringBuilder(100);
                        NativeWindow.GetClassNameW(candidate, name, name.Capacity);
                        if (name.ToString() != "Chrome_WidgetWin_1") return true;
                        handle = candidate; return false;
                    }, IntPtr.Zero);
                }
                if (handle == IntPtr.Zero) continue;
                _windowHandle = handle; _windowProcess = (uint)process.Id;
                return true;
            }
        }
        return false;
    }

    private static double? ReadSlider(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rangeObject))
        {
            var range = ((RangeValuePattern)rangeObject).Current;
            if (!range.IsReadOnly && double.IsFinite(range.Value) && range.Maximum > range.Minimum)
                return Math.Clamp((range.Value - range.Minimum) / (range.Maximum - range.Minimum) * 100, 0, 100);
        }
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject))
        {
            var value = ((ValuePattern)valueObject).Current;
            if (!value.IsReadOnly && double.TryParse(value.Value.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
                && double.IsFinite(percent) && percent is >= 0 and <= 100) return percent;
        }
        return null;
    }

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint objectId, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object? accessible);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Account is not null) { Account.Changed -= OnAccountChanged; Account.Dispose(); }
        _automation.Dispose(ForgetSlider);
    }

    private void ForgetSlider()
    {
        var slider = _slider; var handler = _volumeChanged;
        _slider = null; _volumeChanged = null; _targetId = "";
        if (slider is null || handler is null) return;
        try { Automation.RemoveAutomationPropertyChangedEventHandler(slider, handler); }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { }
    }

    private sealed class AutomationDispatcher : IDisposable
    {
        private readonly TaskCompletionSource<Dispatcher> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _gate = new();
        private Thread? _thread;
        private bool _stopping;
        internal bool Started { get { lock (_gate) return _thread is not null; } }
        internal bool ThreadAlive { get { lock (_gate) return _thread?.IsAlive == true; } }
        private void EnsureStarted()
        {
            lock (_gate)
            {
                if (_stopping) throw new ObjectDisposedException(nameof(AutomationDispatcher));
                if (_thread is not null) return;
                // Browser playback and opening Settings do not need a Spotify
                // accessibility thread, dispatcher, or its reserved stack.
                _thread = new Thread(() =>
                {
                    var dispatcher = Dispatcher.CurrentDispatcher;
                    _ready.TrySetResult(dispatcher);
                    Dispatcher.Run();
                }) { IsBackground = true, Name = "Winisland player accessibility" };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
        }
        public async Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureStarted();
            var dispatcher = await _ready.Task.WaitAsync(cancellationToken);
            return await dispatcher.InvokeAsync(action, DispatcherPriority.Background, cancellationToken).Task;
        }
        public void Dispose() => Dispose(null);
        internal void Dispose(Action? cleanup)
        {
            lock (_gate)
            {
                if (_stopping) return;
                _stopping = true;
                if (_thread is null) return;
            }
            _ = StopAsync(cleanup);
        }
        private async Task StopAsync(Action? cleanup)
        {
            var dispatcher = await _ready.Task.ConfigureAwait(false);
            try
            {
                if (cleanup is not null)
                    await dispatcher.InvokeAsync(cleanup, DispatcherPriority.Background).Task.ConfigureAwait(false);
            }
            finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
        }
    }

    private sealed class BackgroundPresentation : IDisposable
    {
        private readonly IntPtr _handle;
        private readonly uint _process;
        private NativeWindow.Placement _original;
        private readonly bool _wasVisible;
        private readonly int _transitions;
        private readonly IntPtr _foregroundBefore;
        private bool _transitionsChanged, _closed;
        private BackgroundPresentation(IntPtr handle, uint process, NativeWindow.Placement original, bool wasVisible, int transitions)
        {
            _handle = handle; _process = process;
            _original = original; _wasVisible = wasVisible; _transitions = transitions;
            _foregroundBefore = NativeWindow.GetForegroundWindow();
        }
        private bool ShowInBackground(bool capturedTransitions)
        {
            var disable = 1;
            _transitionsChanged = capturedTransitions && NativeWindow.DwmSetWindowAttribute(_handle, 3, ref disable, sizeof(int)) == 0;
            // The app remains behind the user's foreground window. Only an
            // explicit volume operation wakes its frozen minimized renderer;
            // ordinary polling never changes another application's placement.
            if (!NativeWindow.SetWindowPos(_handle, new IntPtr(1), 0, 0, 0, 0, 0x13)) return false;
            var temporary = _original; temporary.Show = 4;
            if (!NativeWindow.SetWindowPlacement(_handle, ref temporary)) return false;
            return NativeWindow.SetWindowPos(_handle, new IntPtr(1), 0, 0, 0, 0, 0x13);
        }
        public static BackgroundPresentation? Open(IntPtr handle, uint process)
        {
            if (handle == IntPtr.Zero || process == 0) return null;
            NativeWindow.GetWindowThreadProcessId(handle, out var owner);
            if (owner != process || !NativeWindow.IsIconic(handle) && NativeWindow.IsWindowVisible(handle)) return null;
            var original = new NativeWindow.Placement { Length = Marshal.SizeOf<NativeWindow.Placement>() };
            // Never modify a window if its original placement cannot be saved.
            if (!NativeWindow.GetWindowPlacement(handle, ref original)) return null;
            var capturedTransitions = NativeWindow.DwmGetWindowAttribute(handle, 3, out var transitions, sizeof(int)) == 0;
            var presentation = new BackgroundPresentation(handle, process, original, NativeWindow.IsWindowVisible(handle), transitions);
            var opened = false;
            try { opened = presentation.ShowInBackground(capturedTransitions); return opened ? presentation : null; }
            finally { if (!opened) presentation.Dispose(); }
        }
        public IntPtr ForegroundBefore => _foregroundBefore;
        public static void InvokePlayerAction(IntPtr spotifyWindow, IntPtr fallbackForeground, Action action)
        {
            var previous = NativeWindow.GetForegroundWindow();
            if (previous == spotifyWindow) previous = fallbackForeground;
            try { action(); }
            finally
            {
                // Spotify's Chromium range action can itself focus its window.
                // This guard ends with the synchronous provider action, leaving
                // the later readback interval free for deliberate user focus.
                if (previous != IntPtr.Zero && previous != spotifyWindow && NativeWindow.GetForegroundWindow() == spotifyWindow)
                    RestoreForeground(previous);
            }
        }
        public static void RestoreForeground(IntPtr handle)
        {
            if (handle == IntPtr.Zero || NativeWindow.SetForegroundWindow(handle)) return;
            var foregroundThread = NativeWindow.GetWindowThreadProcessId(NativeWindow.GetForegroundWindow(), out _);
            var callerThread = NativeWindow.GetCurrentThreadId();
            if (foregroundThread == 0 || foregroundThread == callerThread
                || !NativeWindow.AttachThreadInput(callerThread, foregroundThread, true)) return;
            try { NativeWindow.SetForegroundWindow(handle); }
            finally { NativeWindow.AttachThreadInput(callerThread, foregroundThread, false); }
        }
        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            NativeWindow.GetWindowThreadProcessId(_handle, out var owner);
            if (owner != _process) return;
            // If the user deliberately opened Spotify during the operation,
            // keep their current window rather than minimizing it underneath them.
            try
            {
                if (NativeWindow.GetForegroundWindow() != _handle)
                {
                    var restore = _original;
                    // Canonical GetWindowPlacement states 1/2 use activating
                    // ShowWindow commands. Their nonactivating equivalents
                    // preserve the same normal/minimized placement.
                    if (restore.Show == 2) restore.Show = 7;
                    else if (restore.Show == 1) restore.Show = 4;
                    NativeWindow.SetWindowPlacement(_handle, ref restore);
                    if (!_wasVisible) NativeWindow.ShowWindow(_handle, 0);
                }
            }
            finally
            {
                if (_transitionsChanged)
                {
                    var restore = _transitions;
                    NativeWindow.DwmSetWindowAttribute(_handle, 3, ref restore, sizeof(int));
                }
            }
        }
    }

    private static class NativeWindow
    {
        public delegate bool EnumCallback(IntPtr handle, IntPtr parameter);
        [StructLayout(LayoutKind.Sequential)] public struct Point { public int X,Y; }
        [StructLayout(LayoutKind.Sequential)] public struct Rect { public int L,T,R,B; }
        [StructLayout(LayoutKind.Sequential)] public struct Placement { public int Length,Flags,Show; public Point Minimum,Maximum; public Rect Bounds; }
        [StructLayout(LayoutKind.Sequential)] public struct LastInput { public uint Size,Time; }
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumCallback callback, IntPtr parameter);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr handle, System.Text.StringBuilder name, int size);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr handle, out uint process);
        [DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr handle, ref Placement placement);
        [DllImport("user32.dll")] public static extern bool SetWindowPlacement(IntPtr handle, ref Placement placement);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x,int y,int width,int height,uint flags);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr handle);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr handle);
        [DllImport("user32.dll", EntryPoint="GetWindowLongW")] public static extern int GetWindowLong(IntPtr handle, int index);
        [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LastInput input);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr handle, uint flags);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
        public static bool IsSwitchingApplications() => GetAsyncKeyState(0x12) < 0 || GetAsyncKeyState(0x5B) < 0 || GetAsyncKeyState(0x5C) < 0;
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr handle,int command);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr handle);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint first, uint second, bool attach);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr handle,int attribute,out int value,int size);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr handle,int attribute,ref int value,int size);
    }
}
