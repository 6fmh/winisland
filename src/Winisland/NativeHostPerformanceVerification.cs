using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Winisland.Services;

namespace Winisland;

internal static class NativeHostPerformanceVerification
{
    private sealed record ProcessSample(long PrivateBytes, long WorkingSetBytes, int Handles, double CpuMs);

    internal static async Task<object> RunAsync(string output, Action<bool, string> check)
    {
        BackgroundVerification.RequirePrivateDesktop();
        var directory = Path.Combine(Path.GetFullPath(output), "native-host-performance");
        Directory.CreateDirectory(directory);
        var previousProfile = Environment.GetEnvironmentVariable("WINISLAND_DATA_DIR");
        Environment.SetEnvironmentVariable("WINISLAND_DATA_DIR", Path.Combine(directory, "profile"));
        try
        {
            var samples = new List<object>();
            for (var index = 0; index < 5; index++)
                samples.Add(await MeasureHostAsync(directory, index, check));
            var bridge = await MeasureBridgeAsync(check);
            var result = new
            {
                samples, bridge,
                note = "Five sequential exact-candidate native hosts; process-to-framed-handshake latency and natural process memory/CPU/handles. One-second idle samples have no forced GC. Parent/children remain on the private desktop; no real browser, registration, user profile or network. Bridge samples include a fixed producer and notification readback consumer."
            };
            await File.WriteAllTextAsync(Path.Combine(directory, "samples.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            return result;
        }
        finally { Environment.SetEnvironmentVariable("WINISLAND_DATA_DIR", previousProfile); }
    }

    private static async Task<object> MeasureHostAsync(string directory, int index, Action<bool, string> check)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pipe = new NamedPipeServerStream(BrowserVolumeBridge.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, typeof(NativeHostPerformanceVerification).Assembly.GetName().Name + ".dll"));
        start.ArgumentList.Add("--browser-host");
        var wall = Stopwatch.StartNew();
        using var host = Process.Start(start) ?? throw new InvalidOperationException("Could not start the owned native-host fixture.");
        var stderr = host.StandardError.ReadToEndAsync();
        Task<string>? stdout = null;
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token);
            await NativeMessagingProtocol.WriteAsync(host.StandardInput.BaseStream, "{\"type\":\"hello\",\"browser\":\"chrome\"}", timeout.Token);
            var hello = await NativeMessagingProtocol.ReadAsync(pipe, timeout.Token);
            using (var helloDocument = JsonDocument.Parse(hello ?? "null"))
                check(helloDocument.RootElement.GetProperty("type").GetString() == "hello", $"Native-host performance sample {index + 1} relays the real browser hello");
            await NativeMessagingProtocol.WriteAsync(pipe, "{\"type\":\"ready\",\"protocol\":1}", timeout.Token);
            var ready = await NativeMessagingProtocol.ReadAsync(host.StandardOutput.BaseStream, timeout.Token);
            var handshakeMs = wall.Elapsed.TotalMilliseconds;
            using (var readyDocument = JsonDocument.Parse(ready ?? "null"))
                check(readyDocument.RootElement.GetProperty("type").GetString() == "ready"
                    && readyDocument.RootElement.GetProperty("protocol").GetInt32() == 1,
                    $"Native-host performance sample {index + 1} returns the framed ready acknowledgement");
            stdout = host.StandardOutput.ReadToEndAsync();
            var handshake = Measure(host);
            var idleClock = Stopwatch.StartNew();
            await Task.Delay(1000, timeout.Token);
            var idle = Measure(host);
            var idleElapsedMs = idleClock.Elapsed.TotalMilliseconds;
            const int relayFrames = 128;
            const string state = "{\"type\":\"state\",\"id\":\"fixture:0:1\",\"title\":\"Native relay fixture\",\"volume\":51.5,\"muted\":false,\"playing\":true}";
            var relayClock = Stopwatch.StartNew();
            for (var frame = 0; frame < relayFrames; frame++)
            {
                await NativeMessagingProtocol.WriteAsync(host.StandardInput.BaseStream, state, timeout.Token);
                var actual = await NativeMessagingProtocol.ReadAsync(pipe, timeout.Token);
                if (actual != state) throw new InvalidOperationException("Native-host performance relay changed the fixture frame.");
            }
            var afterRelay = Measure(host);
            var relayElapsedMs = relayClock.Elapsed.TotalMilliseconds;
            check(true, $"Native-host performance sample {index + 1} preserves all {relayFrames} framed state packets");
            var disconnect = Stopwatch.StartNew();
            pipe.Dispose();
            await host.WaitForExitAsync(timeout.Token).WaitAsync(TimeSpan.FromSeconds(3), timeout.Token);
            var exitCode = host.ExitCode;
            check(exitCode == 0 && disconnect.Elapsed.TotalMilliseconds < 3000,
                $"Native-host performance sample {index + 1} exits after fixture disconnection with browser stdin still open");
            var remainingOutput = await stdout.WaitAsync(TimeSpan.FromSeconds(2));
            var remainingError = await stderr.WaitAsync(TimeSpan.FromSeconds(2));
            check(remainingOutput.Length == 0 && remainingError.Length == 0,
                $"Native-host performance sample {index + 1} emits no unframed stdout or errors");
            return new
            {
                run = index + 1, handshakeMs, handshake, idle, idleElapsedMs,
                idleCpuMs = idle.CpuMs - handshake.CpuMs,
                relayFrames, relayElapsedMs, relayCpuMs = afterRelay.CpuMs - idle.CpuMs,
                afterRelay, disconnectMs = disconnect.Elapsed.TotalMilliseconds, exitCode
            };
        }
        finally
        {
            pipe.Dispose();
            if (!host.HasExited)
            {
                host.Kill();
                await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            stdout ??= host.StandardOutput.ReadToEndAsync();
            await File.WriteAllTextAsync(Path.Combine(directory, $"host-{index + 1}-stdout.log"), await stdout.WaitAsync(TimeSpan.FromSeconds(2)));
            await File.WriteAllTextAsync(Path.Combine(directory, $"host-{index + 1}-stderr.log"), await stderr.WaitAsync(TimeSpan.FromSeconds(2)));
        }
    }

    private static async Task<object> MeasureBridgeAsync(Action<bool, string> check)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var bridge = new BrowserVolumeBridge();
        using var connection = new NamedPipeClientStream(".", BrowserVolumeBridge.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var media = new MediaSnapshot { HasSession = true, Source = "chrome", Title = "Duplicate state fixture", IsPlaying = true };
        var notifications = 0;
        var expectedVolume = 51.5;
        var barrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.Changed += (_, _) =>
        {
            Interlocked.Increment(ref notifications);
            var current = bridge.ReadAsync(media, CancellationToken.None).GetAwaiter().GetResult();
            if (current.Available && Math.Abs(current.Volume - Volatile.Read(ref expectedVolume)) < .000001)
                Volatile.Read(ref barrier).TrySetResult(true);
        };
        await connection.ConnectAsync(timeout.Token);
        await NativeMessagingProtocol.WriteAsync(connection, "{\"type\":\"hello\",\"browser\":\"chrome\"}", timeout.Token);
        var ready = await NativeMessagingProtocol.ReadAsync(connection, timeout.Token);
        check(ready?.Contains("\"ready\"") == true, "Measured duplicate-state bridge completes the real local handshake");
        await SendState(expectedVolume);
        await barrier.Task.WaitAsync(timeout.Token);
        await SendBatch(128, expectedVolume, 51.6);
        var samples = new List<object>();
        const int packets = 4096;
        for (var run = 0; run < 3; run++)
        {
            var previousVolume = expectedVolume;
            var marker = 51.7 + run * .1;
            var before = MeasureCurrent();
            var allocations = GC.GetTotalAllocatedBytes(true);
            var wall = Stopwatch.StartNew();
            await SendBatch(packets, previousVolume, marker);
            var elapsedMs = wall.Elapsed.TotalMilliseconds;
            var allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocations;
            var after = MeasureCurrent();
            var count = Volatile.Read(ref notifications);
            var current = await bridge.ReadAsync(media, timeout.Token);
            check(current.Available && Math.Abs(current.Volume - marker) < .000001 && bridge.PlayerCount == 1,
                $"Duplicate-state performance sample {run + 1} preserves liveness and final volume after {packets} identical states");
            check(count is >= 1 and <= packets + 1,
                $"Duplicate-state performance sample {run + 1} retains meaningful state notification delivery");
            samples.Add(new { run = run + 1, packets, notifications = count, elapsedMs, allocatedBytes,
                cpuMs = after.CpuMs - before.CpuMs, before, after, finalVolume = current.Volume });
        }
        connection.Dispose();
        while (bridge.IsConnected) await Task.Delay(5, timeout.Token);
        check(bridge.PlayerCount == 0, "Measured duplicate-state bridge discards targets after owned client disconnection");
        return new { samples, warmupPackets = 128, packetsPerSample = packets,
            note = "Actual NativeMessagingProtocol and BrowserVolumeBridge over a private current-user pipe; repeated unchanged states plus one distinct volume barrier per warmed sample. Allocation/CPU includes fixed fixture producer and notification readback consumer; no forced GC or personal browser." };

        Task SendState(double volume) => NativeMessagingProtocol.WriteAsync(connection,
            JsonSerializer.Serialize(new { type = "state", id = "fixture:0:1", title = media.Title, artist = "Fixture",
                volume, muted = false, playing = true }), timeout.Token);
        async Task SendBatch(int count, double volume, double marker)
        {
            Volatile.Write(ref expectedVolume, marker);
            var nextBarrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref barrier, nextBarrier);
            Interlocked.Exchange(ref notifications, 0);
            var state = JsonSerializer.Serialize(new { type = "state", id = "fixture:0:1", title = media.Title, artist = "Fixture",
                volume, muted = false, playing = true });
            for (var packet = 0; packet < count; packet++)
                await NativeMessagingProtocol.WriteAsync(connection, state, timeout.Token);
            await SendState(marker);
            await nextBarrier.Task.WaitAsync(timeout.Token);
        }
    }

    private static ProcessSample MeasureCurrent()
    {
        using var process = Process.GetCurrentProcess();
        return Measure(process);
    }
    private static ProcessSample Measure(Process process)
    {
        process.Refresh();
        return new ProcessSample(process.PrivateMemorySize64, process.WorkingSet64, process.HandleCount,
            process.TotalProcessorTime.TotalMilliseconds);
    }
}
