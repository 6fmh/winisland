using System;
using System.Diagnostics;
using System.Windows.Threading;

namespace Winisland.Services;

public sealed class FocusTimer : IDisposable
{
    private readonly DispatcherTimer _pulse = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch _elapsed = new();
    private TimeSpan _remaining = TimeSpan.FromMinutes(25);
    private bool _disposed;
    public bool IsRunning => _elapsed.IsRunning;
    public bool IsStarted { get; private set; }
    public TimeSpan Duration { get; private set; } = TimeSpan.FromMinutes(25);
    public TimeSpan Remaining => TimeSpan.FromTicks(Math.Max(0, (_remaining - _elapsed.Elapsed).Ticks));
    public event EventHandler? Changed;
    public event EventHandler? Completed;

    public FocusTimer()
    {
        _pulse.Tick += OnTick;
    }

    public void Start(int minutes) => Start(TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 1440)));

    public void Start(TimeSpan duration)
    {
        Validate(duration);
        _remaining = Duration = duration;
        IsStarted = true;
        _elapsed.Restart();
        _pulse.Start();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void TogglePause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsStarted) return;
        if (Remaining == TimeSpan.Zero) { Complete(); return; }
        if (_elapsed.IsRunning) { _elapsed.Stop(); _pulse.Stop(); }
        else { _elapsed.Start(); _pulse.Start(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Reset(int minutes) => Reset(TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 1440)));

    public void Reset(TimeSpan duration)
    {
        Validate(duration);
        _elapsed.Reset();
        _pulse.Stop();
        IsStarted = false;
        _remaining = Duration = duration;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetDuration(TimeSpan duration)
    {
        Validate(duration);
        var running = _elapsed.IsRunning && IsStarted;
        _remaining = Duration = duration;
        _elapsed.Reset();
        if (running) { _elapsed.Start(); _pulse.Start(); }
        else _pulse.Stop();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Validate(TimeSpan duration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (duration < FocusDurationParser.Minimum || duration > FocusDurationParser.Maximum)
            throw new ArgumentOutOfRangeException(nameof(duration), "Focus duration must be between one second and 24 hours.");
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!IsStarted) return;
        if (Remaining == TimeSpan.Zero)
        {
            Complete();
            return;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Complete()
    {
        if (!IsStarted) return;
        _elapsed.Stop();
        _pulse.Stop();
        IsStarted = false;
        Completed?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pulse.Stop();
        _pulse.Tick -= OnTick;
        _elapsed.Stop();
        IsStarted = false;
    }
}
