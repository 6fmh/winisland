using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Winisland.Controls;
using Winisland.Services;

namespace Winisland;

internal static class AudioVerification
{
    internal static async Task<int> RunHostAsync(string readyFile)
    {
        try
        {
            using var output = new SilentOutput();
            File.WriteAllText(readyFile + ".tmp", "ready");
            File.Move(readyFile + ".tmp", readyFile, true);
            await Task.Delay(TimeSpan.FromSeconds(60));
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(readyFile, ex.ToString()); return 1; }
    }

    internal static async Task RunAsync(string directory, NotchWindow notch, MediaService media, AudioService audio,
        PlayerVolumeService playerVolume, PlayerVolumeVerification.FixturePlayerVolumeBackend playerFixture, Action<bool, string> assert)
    {
        assert(AudioService.MatchesSource("Spotify.exe", "Spotify", ""), "Desktop media IDs match executable names");
        assert(AudioService.MatchesSource("SpotifyAB.SpotifyMusic_test!Spotify", "Spotify", "SpotifyAB.SpotifyMusic_test!App"), "Packaged media IDs match package identity");
        assert(AudioService.MatchesSource("Chrome.WEBAPP.test", "chrome", "") && AudioService.MatchesSource("MSEdge", "msedge", ""), "Browser and installed website IDs match browser audio");
        assert(!AudioService.MatchesSource("", "chrome", "") && !AudioService.MatchesSource("Spotify.exe", "chrome", "") && !AudioService.MatchesSource("ChromeOther", "chrome", ""), "Unknown sources cannot target another app");

        if (!audio.ReadOutputForTest().Available)
        {
            // Hosted Windows VMs can have no audio device at all. Do not start
            // WASAPI hosts there or report physical mixer cases as passing.
            media.SetSnapshotForTest(new MediaSnapshot());
            await playerVolume.WhenIdleAsync();
            audio.SetSource("WinislandMissingAudioFixture.exe");
            assert(!audio.Read().Available && !audio.SetVolume(37) && !audio.SetMuted(true)
                && !((Slider)notch.FindName("VolumeSlider")).IsEnabled && !((Button)notch.FindName("MuteButton")).IsEnabled,
                "With no Windows audio endpoint, unavailable media controls cannot write mixer volume");
            File.WriteAllText(Path.Combine(directory, "audio-hardware.json"), System.Text.Json.JsonSerializer.Serialize(new
            {
                available = false, skipped = "Physical WASAPI capture and independent mixer-session preservation",
                reason = "This machine has no Windows default output endpoint. Run the normal suite on an audio-equipped Windows machine for those cases."
            }));
            return;
        }

        var fixtureDirectory = Path.Combine(directory, "audio-hosts");
        Directory.CreateDirectory(fixtureDirectory);
        var executable = Environment.ProcessPath!;
        // Also support self-test from a regular build with adjacent dependencies.
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "Winisland.dll")))
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(executable)!))
                if (Path.GetExtension(file) is ".dll" or ".json") File.Copy(file, Path.Combine(fixtureDirectory, Path.GetFileName(file)), true);
        var primaryName = $"WinislandAudioPrimary{Environment.ProcessId}";
        var otherName = $"WinislandAudioOther{Environment.ProcessId}";
        using var primary = StartHost(primaryName);
        using var other = StartHost(otherName);
        // The notch keeps changing the shared service's source to select the
        // visualizer device. Independent observers keep preservation checks
        // attached to these two real Windows mixer sessions throughout the UI test.
        using var primaryObserver = new AudioService();
        using var observer = new AudioService();
        AudioSnapshot? primaryBefore = null, otherBefore = null;
        try
        {
            await WaitForHost(primary, primaryName);
            await WaitForHost(other, otherName);
            audio.SetSource(primaryName + ".exe");
            primaryObserver.SetSource(primaryName + ".exe");
            observer.SetSource(otherName + ".exe");
            primaryBefore = primaryObserver.Read(); otherBefore = observer.Read();
            assert(primaryBefore.Available && otherBefore.Available, "Independent silent app sessions are available through Core Audio");
            var masterBefore = audio.ReadOutputForTest();
            await SpectrumVerification.RunCaptureAsync(audio, assert);
            assert(audio.SetVolume(37) && audio.Read().Volume == 37, "App volume writes and reads the selected Windows mixer session");
            assert(audio.SetMuted(true) && audio.Read().Muted, "App mute affects the selected session");
            assert(observer.Read() == otherBefore && audio.ReadOutputForTest() == masterBefore, "Changing app volume and mute preserves another app and Windows master volume");
            var primaryAfterCoreChecks = primaryObserver.Read();

            media.SetSnapshotForTest(new MediaSnapshot { HasSession = true, Source = primaryName + ".exe", Title = "Audio verification" });
            await playerVolume.WhenIdleAsync();
            notch.SelectTabForTest("music");
            notch.Expand(true);
            var slider = (Slider)notch.FindName("VolumeSlider");
            var mute = (Button)notch.FindName("MuteButton");
            assert(primaryObserver.Read().Available && !slider.IsEnabled && !mute.IsEnabled && !playerVolume.Current.Available,
                "A real Windows mixer session does not enable notch volume without an actual player backend");
            var unavailableWrites = playerFixture.Writes.Count;
            slider.Value = 42.35;
            mute.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            assert(!playerVolume.SetVolume(20) && !playerVolume.SetMuted(false)
                && playerFixture.Writes.Count == unavailableWrites && MixerSettingsUnchanged(),
                "Unsupported player controls refuse writes and never fall back to the app mixer");

            media.SetSnapshotForTest(new MediaSnapshot { HasSession = true, Source = "fixture", Title = "Player volume verification" });
            await playerVolume.WhenIdleAsync();
            assert(slider.IsEnabled && mute.IsEnabled && Math.Abs(slider.Value - playerFixture.Volume) < .001,
                "The notch displays and enables the connected player's own volume controls");
            var beforeFractionalWrite = playerFixture.Writes.Count;
            slider.Value = 42.35;
            await playerVolume.WhenIdleAsync();
            assert(Math.Abs(playerFixture.Volume - 42.35) < .001 && Math.Abs(playerVolume.Current.Volume - 42.35) < .001
                && playerFixture.Writes.Count == beforeFractionalWrite + 1,
                "The notch sends a precise fractional slider value once to the actual player backend");
            mute.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await playerVolume.WhenIdleAsync();
            assert(playerFixture.Muted && playerVolume.Current.Muted && Math.Abs(playerFixture.Volume - 42.35) < .001,
                "The notch mute button changes the player's mute state while preserving its volume level");
            mute.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await playerVolume.WhenIdleAsync();
            assert(!playerFixture.Muted && !playerVolume.Current.Muted,
                "The notch unmutes through the player backend");
            assert(MixerSettingsUnchanged(), "Player slider and mute interactions preserve both app mixer sessions and Windows master volume");

            await VerifySliderPointerAsync(notch, slider, playerVolume, playerFixture, assert);
            assert(MixerSettingsUnchanged(), "Control-local pointer volume dragging changes the player without changing any Windows mixer volume");
            notch.Expand(true);
            slider.Value = 42;
            await playerVolume.WhenIdleAsync();
            var beforeSliderWheel = playerFixture.Writes.Count;
            slider.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = UIElement.MouseWheelEvent });
            await playerVolume.WhenIdleAsync();
            assert(Math.Abs(playerFixture.Volume - 44) < .001 && playerFixture.Writes.Count == beforeSliderWheel + 1,
                "Scrolling over the expanded slider adjusts the player's volume once");
            slider.Value = 42;
            await playerVolume.WhenIdleAsync();
            var beforeNotchWheel = playerFixture.Writes.Count;
            notch.Collapse();
            ((UIElement)notch.FindName("Island")).RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = UIElement.MouseWheelEvent });
            await playerVolume.WhenIdleAsync();
            assert(Math.Abs(playerFixture.Volume - 44) < .001 && playerFixture.Writes.Count == beforeNotchWheel + 1,
                "Scrolling the closed notch changes the player's own volume by two percent");

            var beforeSourceChange = playerFixture.Writes.Count;
            media.SetSnapshotForTest(new MediaSnapshot { HasSession = true, Source = otherName + ".exe" });
            await playerVolume.WhenIdleAsync();
            assert(observer.Read().Available && !slider.IsEnabled && !mute.IsEnabled && !playerVolume.Current.Available
                && !playerVolume.SetVolume(20) && !playerVolume.SetMuted(true),
                "Switching to an unsupported player disables volume controls even when its Windows mixer exists");
            media.SetSnapshotForTest(new MediaSnapshot { HasSession = true, Source = "Winisland.MissingAudioFixture" });
            await playerVolume.WhenIdleAsync();
            assert(!slider.IsEnabled && !mute.IsEnabled && !playerVolume.SetVolume(20) && !playerVolume.SetMuted(true),
                "A missing player backend disables controls and refuses all player writes");
            media.SetSnapshotForTest(new MediaSnapshot());
            await playerVolume.WhenIdleAsync();
            assert(!slider.IsEnabled && !mute.IsEnabled && !playerVolume.Current.Available
                && !playerVolume.SetVolume(20) && !playerVolume.SetMuted(true) && playerFixture.Writes.Count == beforeSourceChange,
                "No media session leaves player volume unavailable and cannot write to the previous player");
            assert(MixerSettingsUnchanged(), "All notch volume interactions preserve primary and unrelated app mixer settings and Windows master volume");

            bool MixerSettingsUnchanged() => primaryObserver.Read() == primaryAfterCoreChecks
                && observer.Read() == otherBefore && audio.ReadOutputForTest() == masterBefore;
        }
        finally
        {
            if (primaryBefore is { Available: true }) { primaryObserver.SetVolume(primaryBefore.Volume); primaryObserver.SetMuted(primaryBefore.Muted); }
            if (otherBefore is { Available: true }) { observer.SetVolume(otherBefore.Volume); observer.SetMuted(otherBefore.Muted); }
            media.SetSnapshotForTest(new MediaSnapshot());
            await playerVolume.WhenIdleAsync();
            StopHost(primary); StopHost(other);
        }

        Process StartHost(string name)
        {
            var path = Path.Combine(fixtureDirectory, name + ".exe");
            File.Copy(executable, path, true);
            var ready = Path.Combine(fixtureDirectory, name + ".ready");
            if (File.Exists(ready)) File.Delete(ready);
            var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--audio-test-host"); start.ArgumentList.Add(ready);
            return Process.Start(start) ?? throw new InvalidOperationException("Could not start silent audio fixture.");
        }

        async Task WaitForHost(Process host, string name)
        {
            var ready = Path.Combine(fixtureDirectory, name + ".ready");
            var clock = Stopwatch.StartNew();
            while (!File.Exists(ready) && !host.HasExited && clock.ElapsedMilliseconds < 10000) await Task.Delay(50);
            if (!File.Exists(ready) || File.ReadAllText(ready) != "ready")
                throw new InvalidOperationException("Silent audio fixture failed: " + (File.Exists(ready) ? File.ReadAllText(ready) : name));
        }
    }

    private static async Task VerifySliderPointerAsync(NotchWindow notch, Slider control, PlayerVolumeService playerVolume,
        PlayerVolumeVerification.FixturePlayerVolumeBackend playerFixture, Action<bool, string> assert)
    {
        var slider = (ScrubSlider)control;
        try
        {
            notch.UpdateLayout();
            ((Button)notch.FindName("PinButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(80);
            notch.UpdateLayout();
            // Coordinates are local to the real control, above its visible rail.
            assert(slider.BeginPointerAdjustment(new Point(50, 10), captureMouse: false), "The player slider captures control-local pointer input");
            await playerVolume.WhenIdleAsync();
            assert(slider.HasPointerCapture && playerFixture.Volume is >= 15 and <= 25,
                "The full slider hit area starts a drag and sets the clicked player volume");
            for (var x = 51; x <= 170; x++)
            {
                slider.MovePointerAdjustment(new Point(x, 10));
                await Task.Delay(2);
            }
            await playerVolume.WhenIdleAsync();
            assert(playerFixture.Volume is >= 75 and <= 85
                && Math.Abs(slider.Value - playerFixture.Volume) < .01
                && Math.Abs(SmoothValue.GetDisplayValue(slider) - slider.Value) < .01,
                "Slow pointer movements keep the player volume and presented rail precisely under the pointer");
            slider.MovePointerAdjustment(new Point(260, 100));
            await Task.Delay(300);
            await playerVolume.WhenIdleAsync();
            assert(slider.HasPointerCapture && playerFixture.Volume == 100 && notch.IsExpanded,
                "Dragging beyond the notch clamps player volume and keeps the panel open");
            slider.EndPointerAdjustment(new Point(260, 100));
            await Task.Delay(40);
            await playerVolume.WhenIdleAsync();
            assert(!slider.HasPointerCapture && !slider.IsAdjusting,
                "Releasing a player volume drag releases pointer capture and adjustment state");
        }
        finally { slider.CancelAdjustment(); }
    }

    private static void StopHost(Process host)
    {
        if (!host.HasExited) { host.Kill(); host.WaitForExit(5000); }
    }

    // A looping buffer of zero-valued PCM creates a real app mixer session
    // without playing a tone or modifying any of the user's app volumes.
    private sealed class SilentOutput : IDisposable
    {
        private IntPtr _device, _data, _header;
        private bool _prepared;
        private static readonly uint HeaderSize = (uint)Marshal.SizeOf<WaveHeader>();

        internal SilentOutput()
        {
            try
            {
                var format = new WaveFormat { Format = 1, Channels = 2, SamplesPerSecond = 48000, BytesPerSecond = 192000, BlockAlign = 4, BitsPerSample = 16 };
                Check(waveOutOpen(out _device, uint.MaxValue, ref format, IntPtr.Zero, IntPtr.Zero, 0));
                var silence = new byte[192000];
                _data = Marshal.AllocHGlobal(silence.Length); Marshal.Copy(silence, 0, _data, silence.Length);
                _header = Marshal.AllocHGlobal((int)HeaderSize);
                Marshal.StructureToPtr(new WaveHeader { Data = _data, Length = (uint)silence.Length, Flags = 4 | 8, Loops = uint.MaxValue }, _header, false);
                Check(waveOutPrepareHeader(_device, _header, HeaderSize)); _prepared = true;
                Check(waveOutWrite(_device, _header, HeaderSize));
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            if (_device != IntPtr.Zero)
            {
                waveOutReset(_device);
                if (_prepared) waveOutUnprepareHeader(_device, _header, HeaderSize);
                waveOutClose(_device); _device = IntPtr.Zero;
            }
            if (_header != IntPtr.Zero) { Marshal.FreeHGlobal(_header); _header = IntPtr.Zero; }
            if (_data != IntPtr.Zero) { Marshal.FreeHGlobal(_data); _data = IntPtr.Zero; }
        }
        private static void Check(uint result) { if (result != 0) throw new InvalidOperationException($"Wave output failed: {result}"); }
        [StructLayout(LayoutKind.Sequential, Pack = 2)] private struct WaveFormat
        {
            public ushort Format, Channels;
            public uint SamplesPerSecond, BytesPerSecond;
            public ushort BlockAlign, BitsPerSample, ExtraSize;
        }
        [StructLayout(LayoutKind.Sequential)] private struct WaveHeader
        {
            public IntPtr Data;
            public uint Length, Recorded;
            public UIntPtr User;
            public uint Flags, Loops;
            public IntPtr Next;
            public UIntPtr Reserved;
        }
        [DllImport("winmm.dll")] private static extern uint waveOutOpen(out IntPtr device, uint id, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(IntPtr device, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveOutWrite(IntPtr device, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveOutReset(IntPtr device);
        [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(IntPtr device, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveOutClose(IntPtr device);
    }
}
