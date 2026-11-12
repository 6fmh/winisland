using System;
using System.Diagnostics;

namespace Winisland.Services;

// Bound dispatcher work at the byte producer, before Progress<T> posts it.
// Transfers retain their first sample and immediate final 100% notification.
internal sealed class LatestProgress : IProgress<double>
{
    internal const int MinimumIntervalMilliseconds = 100;
    private readonly IProgress<double> _target;
    private long _lastTimestamp;
    private double _lastValue = -1;

    internal LatestProgress(IProgress<double> target) => _target = target;

    public void Report(double value)
    {
        if (value <= _lastValue) return;
        var now = Stopwatch.GetTimestamp();
        if (_lastValue >= 0 && value < 100
            && Stopwatch.GetElapsedTime(_lastTimestamp, now).TotalMilliseconds < MinimumIntervalMilliseconds) return;
        _lastTimestamp = now;
        _lastValue = value;
        _target.Report(value);
    }
}
