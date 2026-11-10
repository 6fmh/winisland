using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Winisland.Services;

namespace Winisland;

internal static class SpectrumVerification
{
    // Silent fixture output still delivers actual PCM packets. Initialize the mixer
    // first, exactly as production does, so COM wrapper collisions cannot be missed.
    internal static async Task RunCaptureAsync(AudioService audio, Action<bool, string> assert)
    {
        var deviceId = audio.ReadVisualizerDeviceId();
        assert(deviceId is not null, "Spectrum capture finds the selected app's actual output device");
        var endpointBefore = audio.ReadOutputForTest();
        using var capture = new AudioSpectrumService();
        capture.EnsureStarted(deviceId);
        await WaitForSamples();
        assert(capture.IsCapturing && capture.HasRecentSamples,
            "Production startup order opens WASAPI capture and receives real PCM after mixer initialization");
        using var secondMixer = new AudioService();
        assert(secondMixer.ReadOutputForTest().Available && audio.ReadOutputForTest() == endpointBefore,
            "Initializing another mixer while capture runs preserves endpoint access and volume");
        capture.Stop();
        assert(!capture.IsCapturing && !capture.HasRecentSamples, "Stopping spectrum capture releases audio and clears stale sample status");
        capture.EnsureStarted(deviceId);
        await WaitForSamples();
        assert(capture.IsCapturing && capture.HasRecentSamples,
            "Reopening the compact notch restarts real audio capture after a stop");
        capture.Stop();
        capture.EnsureStarted("winisland-verification-missing-output-device");
        await WaitForSamples();
        assert(capture.IsCapturing && capture.HasRecentSamples,
            "A disappeared routed output falls back to the default audio endpoint");

        async Task WaitForSamples()
        {
            var clock = Stopwatch.StartNew();
            while (!capture.HasRecentSamples && clock.ElapsedMilliseconds < 2000) await Task.Delay(20);
        }
    }

    internal static void Run(Action<bool, string> assert)
    {
        foreach (var tone in new[] { (Frequency: 110d, Band: 1), (Frequency: 1100d, Band: 6), (Frequency: 9000d, Band: 11) })
        {
            var bands = Analyze(tone.Frequency, 0.25f);
            var strongest = 0;
            for (var i = 1; i < bands.Length; i++) if (bands[i] > bands[strongest]) strongest = i;
            assert(strongest == tone.Band && bands[strongest] > 0.5,
                $"A {tone.Frequency:0} Hz tone lights its actual frequency band");
            assert(bands[Math.Abs(tone.Band - 1) > 2 ? 1 : 11] < 0.05,
                $"A {tone.Frequency:0} Hz tone leaves unrelated frequencies quiet");
        }
        assert(Analyze(110, 0.3f)[1] > Analyze(110, 0.025f)[1] + 0.3,
            "Louder hits produce visibly taller bars without automatic gain flattening dynamics");
        assert(Math.Abs(Analyze(110, 0.25f, true)[1] - Analyze(110, 0.25f)[1]) < 0.001,
            "Out-of-phase stereo audio remains visible instead of cancelling in a mono mix");
        var analyzer = new SpectrumAnalyzer(48000);
        for (var i = 0; i < 4096; i++) analyzer.Push((float)Math.Sin(2 * Math.PI * 110 * i / 48000) * 0.3f, 0);
        for (var i = 0; i < 4096; i++) analyzer.Push(0, 0);
        var silent = new float[SpectrumAnalyzer.BandCount];
        analyzer.Read(silent);
        assert(Array.TrueForAll(silent, level => level == 0), "Silence clears every frequency band without a rolling tail");
        for (var i = 0; i < 4096; i++) analyzer.Push(float.NaN, float.PositiveInfinity);
        analyzer.Read(silent);
        assert(Array.TrueForAll(silent, level => float.IsFinite(level) && level == 0),
            "Invalid audio samples cannot produce invalid spectrum levels");
    }

    private static float[] Analyze(double frequency, float amplitude, bool invertedRight = false)
    {
        var analyzer = new SpectrumAnalyzer(48000);
        for (var i = 0; i < 8192; i++)
        {
            var sample = (float)Math.Sin(2 * Math.PI * frequency * i / 48000) * amplitude;
            analyzer.Push(sample, invertedRight ? -sample : sample);
        }
        var bands = new float[SpectrumAnalyzer.BandCount];
        analyzer.Read(bands);
        return bands;
    }
}
