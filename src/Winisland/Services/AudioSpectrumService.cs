using System;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Winisland.Services;

// Capture only the output device, in memory. No microphone, files, or retained audio.
internal sealed class AudioSpectrumService : IDisposable
{
    private LowLatencyLoopbackCapture? _capture;
    private MMDevice? _device;
    private SpectrumAnalyzer? _analyzer;
    private string? _deviceId;
    private long _nextDeviceCheck;
    private volatile bool _failed;

    internal bool IsCapturing => !_failed && _capture?.CaptureState is CaptureState.Starting or CaptureState.Capturing;
    internal bool HasRecentSamples => !_failed && _analyzer?.HasRecentSamples == true;

    internal void EnsureStarted(string? requestedDeviceId)
    {
        if (Environment.TickCount64 < _nextDeviceCheck) return;
        _nextDeviceCheck = Environment.TickCount64 + 1000;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            MMDevice device;
            MMDevice? requested = null;
            try
            {
                if (requestedDeviceId is not null) requested = enumerator.GetDevice(requestedDeviceId);
                if (requested?.State == DeviceState.Active) { device = requested; requested = null; }
                else device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            catch (Exception ex) when (requestedDeviceId is not null && ex is COMException or ArgumentException or InvalidOperationException)
            {
                // A routed endpoint may disappear between session enumeration
                // and capture. Continue on the default output until it returns.
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            finally { requested?.Dispose(); }
            if (_capture is not null && !_failed && device.ID == _deviceId) { device.Dispose(); return; }
            Stop();
            _nextDeviceCheck = Environment.TickCount64 + 1000;
            _device = device;
            _deviceId = device.ID;
            _failed = false;
            _capture = new LowLatencyLoopbackCapture(device);
            // WASAPI shared-mode conversion handles the endpoint's native format/channels.
            _capture.WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
            var analyzer = new SpectrumAnalyzer(48000);
            _analyzer = analyzer;
            _capture.DataAvailable += (_, e) =>
            {
                for (var offset = 0; offset + 8 <= e.BytesRecorded; offset += 8)
                    analyzer.Push(BitConverter.ToSingle(e.Buffer, offset), BitConverter.ToSingle(e.Buffer, offset + 4));
            };
            _capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception is null) return;
                _failed = true;
                Log.Error("Audio spectrum capture", e.Exception);
            };
            _capture.StartRecording();
        }
        catch (Exception ex)
        {
            Stop();
            _nextDeviceCheck = Environment.TickCount64 + 3000;
            Log.Error("Start audio spectrum", ex);
        }
    }

    internal void Read(Span<float> bands)
    {
        if (_analyzer is null || _failed) bands.Clear();
        else _analyzer.Read(bands);
    }

    internal void Stop()
    {
        // Dispose waits for the worker before releasing the device and sample buffers.
        _capture?.Dispose();
        _capture = null;
        _device?.Dispose();
        _device = null;
        _analyzer = null;
        _deviceId = null;
        _failed = false;
        _nextDeviceCheck = 0;
    }

    public void Dispose() => Stop();

    private sealed class LowLatencyLoopbackCapture : WasapiCapture
    {
        internal LowLatencyLoopbackCapture(MMDevice device) : base(device, false, 20) { }
        protected override AudioClientStreamFlags GetAudioClientStreamFlags() => base.GetAudioClientStreamFlags() | AudioClientStreamFlags.Loopback;
    }
}
