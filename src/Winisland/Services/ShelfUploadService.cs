using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Winisland.Services;

public sealed class ShelfUploadService : IDisposable
{
    private readonly FileShelf _shelf;
    private readonly UploadClient _client;
    private readonly Queue<(ShelfItem Item, CancellationTokenSource Cancellation, UploadConfiguration Connection)> _queue = new();
    private readonly Dictionary<ShelfItem, CancellationTokenSource> _pending = new();
    private bool _processing, _disposed;
    public UploadCredentialStore Credentials { get; }
    public event EventHandler? Changed;
    public int PendingCount => _shelf.Items.Count(i => i.CanCancel);
    public double Progress => _shelf.Items.FirstOrDefault(i => i.IsUploading)?.Progress ?? 0;

    public ShelfUploadService(FileShelf shelf, UploadCredentialStore credentials, UploadClient? client = null)
    {
        _shelf = shelf; Credentials = credentials; _client = client ?? new UploadClient();
        Credentials.Changed += OnCredentialChanged;
        _shelf.Items.CollectionChanged += OnShelfChanged;
    }

    public void Enqueue(ShelfItem item)
    {
        if (_disposed || !_shelf.Items.Contains(item) || _pending.ContainsKey(item) || item.HasLink || Directory.Exists(item.Path)) return;
        if (!Credentials.IsConnected) { item.SetUpload(UploadState.NeedsConnection); Changed?.Invoke(this, EventArgs.Empty); return; }
        var cancellation = new CancellationTokenSource();
        _pending.Add(item, cancellation);
        item.SetUpload(UploadState.Queued);
        _queue.Enqueue((item, cancellation, UploadCredentialStore.Validate(Credentials.Connection)));
        Changed?.Invoke(this, EventArgs.Empty);
        _ = ProcessAsync();
    }

    public void Cancel(ShelfItem item)
    {
        if (_pending.TryGetValue(item, out var cancellation)) cancellation.Cancel();
        if (item.CanCancel) item.SetUpload(UploadState.Cancelled);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task ProcessAsync()
    {
        if (_processing || _disposed) return;
        _processing = true;
        try
        {
            while (_queue.TryDequeue(out var work))
            {
                var (item, cancellation, connection) = work;
                try
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    item.SetUpload(UploadState.Uploading);
                    Changed?.Invoke(this, EventArgs.Empty);
                    var progress = new Progress<double>(value =>
                    {
                        if (!item.IsUploading || cancellation.IsCancellationRequested || _disposed) return;
                        var nextState = value >= 100 ? UploadState.Processing : UploadState.Uploading;
                        if (item.State == nextState && item.Progress == value) return;
                        item.SetUpload(nextState, value);
                        Changed?.Invoke(this, EventArgs.Empty);
                    });
                    var result = await _client.UploadAsync(item.Path, connection, progress, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (!_disposed && _shelf.Items.Contains(item)) item.SetUpload(UploadState.Uploaded, 100, result: result);
                    _shelf.Save();
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    item.SetUpload(UploadState.Cancelled, error: "Transfer stopped; check the host if it had already finished.");
                }
                catch (OperationCanceledException) { item.SetUpload(UploadState.Failed, error: "Upload timed out. Check the host, then retry."); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException or ArgumentException)
                {
                    item.SetUpload(UploadState.Failed, error: ex is HttpRequestException ? "Connection failed. Try again." : ex.Message);
                }
                finally { _pending.Remove(item); cancellation.Dispose(); if (!_disposed) Changed?.Invoke(this, EventArgs.Empty); }
            }
        }
        finally { _processing = false; }
    }

    private void OnCredentialChanged(object? sender, EventArgs e)
    {
        foreach (var item in _pending.Keys.ToArray()) Cancel(item);
        if (Credentials.IsConnected) foreach (var item in _shelf.Items.Where(i => i.State == UploadState.NeedsConnection).ToArray()) Enqueue(item);
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private void OnShelfChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var item in _pending.Keys.Where(i => !_shelf.Items.Contains(i)).ToArray()) Cancel(item);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Credentials.Changed -= OnCredentialChanged;
        _shelf.Items.CollectionChanged -= OnShelfChanged;
        foreach (var cancellation in _pending.Values) cancellation.Cancel();
        _client.Dispose();
    }
}
