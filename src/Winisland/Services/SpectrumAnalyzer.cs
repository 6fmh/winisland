using System;
using System.Diagnostics;
using NAudio.Dsp;

namespace Winisland.Services;

// Fixed frequency bands preserve the relationship between a song and its visual.
// No scrolling history, oscillators, or automatic gain that flattens the beat.
internal sealed class SpectrumAnalyzer
{
    internal const int BandCount = 13;
    private const int Size = 2048;
    private const int Hop = 512;
    private static readonly double[] Edges = { 40, 80, 140, 230, 370, 580, 900, 1400, 2200, 3400, 5200, 8000, 12000, 18000 };
    private readonly float[] _left = new float[Size], _right = new float[Size], _window = new float[Size];
    private readonly Complex[] _fftLeft = new Complex[Size], _fftRight = new Complex[Size];
    private readonly float[] _bands = new float[BandCount];
    private readonly object _gate = new();
    private readonly int _sampleRate;
    private int _position, _pending;
    private long _updatedAt;

    internal bool HasRecentSamples
    {
        get { lock (_gate) return _updatedAt != 0 && Stopwatch.GetElapsedTime(_updatedAt).TotalMilliseconds <= 100; }
    }

    internal SpectrumAnalyzer(int sampleRate)
    {
        _sampleRate = sampleRate;
        for (var i = 0; i < Size; i++) _window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Size - 1)));
    }

    internal void Push(float left, float right)
    {
        _left[_position] = float.IsFinite(left) ? Math.Clamp(left, -1, 1) : 0;
        _right[_position] = float.IsFinite(right) ? Math.Clamp(right, -1, 1) : 0;
        _position = (_position + 1) % Size;
        if (++_pending < Hop) return;
        _pending = 0;
        for (var i = 0; i < Size; i++)
        {
            var source = (_position + i) % Size;
            _fftLeft[i] = new Complex { X = _left[source] * _window[i] };
            _fftRight[i] = new Complex { X = _right[source] * _window[i] };
        }
        FastFourierTransform.FFT(true, 11, _fftLeft);
        FastFourierTransform.FFT(true, 11, _fftRight);
        lock (_gate)
        {
            for (var band = 0; band < BandCount; band++)
            {
                var first = Math.Max(1, (int)Math.Ceiling(Edges[band] * Size / _sampleRate));
                var last = Math.Min(Size / 2, (int)Math.Ceiling(Edges[band + 1] * Size / _sampleRate));
                double power = 0;
                for (var bin = first; bin < last; bin++)
                {
                    var l = _fftLeft[bin]; var r = _fftRight[bin];
                    power += (l.X * l.X + l.Y * l.Y + r.X * r.X + r.Y * r.Y) / 2;
                }
                // A modest treble correction makes quieter high-frequency hits legible.
                var decibels = 20 * Math.Log10(Math.Max(1e-9, 2 * Math.Sqrt(2 * power))) + band * 0.7;
                _bands[band] = (float)Math.Pow(Math.Clamp((decibels + 60) / 50, 0, 1), 1.4);
            }
            _updatedAt = Stopwatch.GetTimestamp();
        }
    }

    internal void Read(Span<float> destination)
    {
        lock (_gate)
        {
            // WASAPI may stop sending packets during silence or device disconnection.
            if (_updatedAt == 0 || Stopwatch.GetElapsedTime(_updatedAt).TotalMilliseconds > 100) destination.Clear();
            else _bands.AsSpan().CopyTo(destination);
        }
    }
}
