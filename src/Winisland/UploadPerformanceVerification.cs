using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Winisland.Services;

namespace Winisland;

internal static class UploadPerformanceVerification
{
    private const int FileBytes = 24 * 1024 * 1024;
    private const int Batches = 3, TransfersPerBatch = 2;
    private const string Endpoint = "https://upload-performance.example.test/upload";
    private const string Share = "https://upload-performance.example.test/files/fixture.bin";
    private sealed record Sample(int Batch, int Transfers, double WallMs, double CpuMs,
        long AllocatedBytes, long PrivateBytes, long WorkingSetBytes, int Changed,
        int PropertyChanged, int BindingReads, int Requests, long MultipartBytes);

    internal static async Task<object> RunAsync(string output, Action<bool, string> check)
    {
        BackgroundVerification.RequirePrivateDesktop();
        Application.Current.Dispatcher.VerifyAccess();
        var profile = Path.Combine(Path.GetFullPath(output), "upload-performance-profile");
        Directory.CreateDirectory(profile);
        var previousProfile = Environment.GetEnvironmentVariable("WINISLAND_DATA_DIR");
        Environment.SetEnvironmentVariable("WINISLAND_DATA_DIR", profile);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var file = Path.Combine(profile, "fixture.bin");
            var block = new byte[65536];
            Array.Fill(block, (byte)0x5A);
            using (var fixture = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None))
                for (var written = 0; written < FileBytes; written += block.Length) fixture.Write(block);
            byte[] originalHash;
            using (var fixture = File.OpenRead(file)) originalHash = SHA256.HashData(fixture);
            var credentials = new UploadCredentialStore(profile);
            credentials.Save(Endpoint, "performance-fixture-token");
            check(new UploadCredentialStore(profile).IsConnected,
                "Upload performance uses actual Windows-protected fixture credentials");

            var samples = new List<Sample>();
            using var process = Process.GetCurrentProcess();
            // Warm the same service/serializer/stream and binding paths before sampling.
            await RunBatchAsync(-1, 1, file, credentials, process, check, deadline.Token);
            for (var batch = 0; batch < Batches; batch++)
                samples.Add(await RunBatchAsync(batch, TransfersPerBatch, file, credentials,
                    process, check, deadline.Token));
            using (var fixture = File.OpenRead(file))
                check(SHA256.HashData(fixture).SequenceEqual(originalHash),
                    "Measured upload transfers preserve every byte of the local original");
            var result = new
            {
                workload = "Two sequential actual ShelfUploadService multipart uploads per batch",
                fileBytes = FileBytes, batches = Batches, transfersPerBatch = TransfersPerBatch,
                medianWallMs = Median(samples.Select(s => s.WallMs)),
                medianCpuMs = Median(samples.Select(s => s.CpuMs)),
                medianAllocatedBytes = Median(samples.Select(s => (double)s.AllocatedBytes)),
                medianChanged = Median(samples.Select(s => (double)s.Changed)),
                medianPropertyChanged = Median(samples.Select(s => (double)s.PropertyChanged)),
                medianBindingReads = Median(samples.Select(s => (double)s.BindingReads)),
                totalCpuMs = samples.Sum(s => s.CpuMs),
                totalWallMs = samples.Sum(s => s.WallMs),
                totalAllocatedBytes = samples.Sum(s => s.AllocatedBytes),
                note = "Private dispatcher; fresh DPAPI profile; one warmup excluded. Injected handler streams to a counting sink on ThreadPool, without network or whole-file buffering. Binding reads follow actual property invalidations and model shelf/compact presentation queries; no real window is constructed by this workload. Process CPU/allocation/memory include any other work in the same verification process. No forced GC, working-set trim, speedup assertion or user profile access. CPU quantization and cached local I/O can dominate short wall-time differences.",
                samples
            };
            await File.WriteAllTextAsync(Path.Combine(output, "upload-performance.json"),
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), deadline.Token);
            return result;
        }
        finally { Environment.SetEnvironmentVariable("WINISLAND_DATA_DIR", previousProfile); }
    }

    private static async Task<Sample> RunBatchAsync(int batch, int transfers, string file,
        UploadCredentialStore credentials, Process process, Action<bool, string> check,
        CancellationToken cancellation)
    {
        var shelf = new FileShelf();
        shelf.Clear();
        var changed = 0;
        var properties = 0;
        var reads = 0;
        var handler = new StreamingHandler();
        using var uploads = new ShelfUploadService(shelf, credentials,
            new UploadClient(new HttpClient(handler)));
        uploads.Changed += (_, _) =>
        {
            changed++;
            // Same collection queries/string construction as shelf and compact callbacks.
            _ = uploads.PendingCount; _ = uploads.PendingCount; _ = uploads.Progress;
            _ = new Uri(credentials.ServerUrl).Host;
            _ = $"{shelf.Items.Count} of 24 files";
            reads += 5;
        };
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(false);
        var clock = Stopwatch.StartNew();
        for (var transfer = 0; transfer < transfers; transfer++)
        {
            cancellation.ThrowIfCancellationRequested();
            shelf.Clear();
            shelf.Add(new[] { file });
            var item = shelf.Items.Single();
            var terminal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            item.PropertyChanged += OnItemChanged;
            try
            {
                uploads.Enqueue(item);
                await terminal.Task.WaitAsync(cancellation);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                check(item.State == UploadState.Uploaded && item.HasLink && item.ShareUrl == Share
                    && item.Progress == 100 && uploads.PendingCount == 0,
                    $"Upload performance batch {batch}, transfer {transfer} reaches its correct terminal state");
            }
            finally { item.PropertyChanged -= OnItemChanged; }

            void OnItemChanged(object? sender, PropertyChangedEventArgs args)
            {
                Application.Current.Dispatcher.VerifyAccess();
                properties++;
                // Equivalent consumers to bound shelf fields, invalidated only when named.
                var all = string.IsNullOrEmpty(args.PropertyName);
                if (all || args.PropertyName == nameof(ShelfItem.Name)) { _ = item.Name; reads++; }
                if (all || args.PropertyName == nameof(ShelfItem.Status)) { _ = item.Status; reads++; }
                if (all || args.PropertyName == nameof(ShelfItem.Progress)) { _ = item.Progress; reads++; }
                if (all || args.PropertyName == nameof(ShelfItem.IsUploading)) { _ = item.IsUploading; reads++; }
                if (all || args.PropertyName == nameof(ShelfItem.CanRetry)) { _ = item.CanRetry; reads++; }
                if (all || args.PropertyName == nameof(ShelfItem.CanCancel)) { _ = item.CanCancel; reads++; }
                if (all || args.PropertyName == nameof(ShelfItem.HasLink)) { _ = item.HasLink; reads++; }
                if (all || args.PropertyName == nameof(ShelfItem.HasDirectLink)) { _ = item.HasDirectLink; reads++; }
                if (all || args.PropertyName == nameof(ShelfItem.FileUrl)) { _ = item.FileUrl; reads++; }
                if (item.State is UploadState.Uploaded or UploadState.Failed or UploadState.Cancelled)
                    terminal.TrySetResult(true);
            }
        }
        clock.Stop();
        var allocated = GC.GetTotalAllocatedBytes(false) - allocatedBefore;
        process.Refresh();
        var cpu = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        check(handler.Requests == transfers && handler.ProtocolCorrect
            && handler.MultipartBytes >= (long)transfers * FileBytes
            && handler.MultipartBytes < (long)transfers * (FileBytes + 4096),
            $"Upload performance batch {batch} streams bounded multipart bytes through the actual client");
        return new Sample(batch, transfers, clock.Elapsed.TotalMilliseconds, cpu, allocated,
            process.PrivateMemorySize64, process.WorkingSet64, changed, properties, reads,
            handler.Requests, handler.MultipartBytes);
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        return ordered[ordered.Length / 2];
    }

    private sealed class StreamingHandler : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        internal long MultipartBytes { get; private set; }
        internal bool ProtocolCorrect { get; private set; } = true;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellation)
        {
            Requests++;
            ProtocolCorrect &= request.Method == HttpMethod.Post
                && request.RequestUri?.AbsoluteUri == Endpoint
                && request.Headers.Authorization?.ToString() == "Bearer performance-fixture-token"
                && request.Content?.Headers.ContentType?.MediaType == "multipart/form-data";
            using var sink = new CountingSink();
            await Task.Run(() => request.Content!.CopyToAsync(sink, cancellation), cancellation);
            MultipartBytes += sink.Bytes;
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"url\":\"" + Share + "\"}", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class CountingSink : Stream
    {
        internal long Bytes { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Bytes;
        public override long Position { get => Bytes; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => Bytes += count;
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellation)
        { cancellation.ThrowIfCancellationRequested(); Bytes += count; return Task.CompletedTask; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellation = default)
        { cancellation.ThrowIfCancellationRequested(); Bytes += buffer.Length; return ValueTask.CompletedTask; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
