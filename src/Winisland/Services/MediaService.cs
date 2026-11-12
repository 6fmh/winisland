using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Winisland.Services;

public sealed record MediaSnapshot
{
    public string Title { get; init; } = "Not playing";
    public string Artist { get; init; } = "";
    public string Source { get; init; } = "";
    public bool HasSession { get; init; }
    public bool IsPlaying { get; init; }
    public bool CanToggle { get; init; }
    public bool CanPrevious { get; init; }
    public bool CanNext { get; init; }
    public bool CanSeek { get; init; }
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public ImageSource? Artwork { get; init; }
}

public sealed class MediaService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private long _generation;
    private bool _disposed;
    private long _timelineAt = Stopwatch.GetTimestamp();
    private DateTimeOffset _lastTimelineUpdate;
    private TimeSpan _lastReportedPosition;
    private string _artworkHash = "";
    private TimeSpan _timelineStart;
    public MediaSnapshot Current { get; private set; } = new();
    public event EventHandler? Changed;

    public MediaService(Dispatcher dispatcher) => _dispatcher = dispatcher;
    internal void SetSnapshotForTest(MediaSnapshot snapshot) { Current = snapshot; _timelineAt = Stopwatch.GetTimestamp(); Changed?.Invoke(this, EventArgs.Empty); }
    internal Func<double, Task<bool>>? SeekHandlerForTest { get; set; }
    internal Func<Task<bool>>? ToggleHandlerForTest { get; set; }
    internal Func<Task<bool>>? PreviousHandlerForTest { get; set; }
    internal Func<Task<bool>>? NextHandlerForTest { get; set; }

    public async Task InitializeAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_disposed) return;
            _manager = manager;
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            _manager.SessionsChanged += OnSessionsChanged;
            await AttachAsync();
        }
        catch (Exception ex) { Log.Error("Connect Windows media controls", ex); }
    }

    private async void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        await _dispatcher.InvokeAsync(AttachAsync).Task.Unwrap();
    }

    private async void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        await _dispatcher.InvokeAsync(AttachAsync).Task.Unwrap();
    }

    private async Task AttachAsync()
    {
        if (_disposed) return;
        var next = _manager?.GetCurrentSession();
        if (next == _session) return;
        Detach();
        _session = next;
        if (_session is null) { Current = new(); Changed?.Invoke(this, EventArgs.Empty); return; }
        _session.MediaPropertiesChanged += OnMetadataChanged;
        _session.PlaybackInfoChanged += OnPlaybackChanged;
        _session.TimelinePropertiesChanged += OnTimelineChanged;
        Current = new() { HasSession = true, Source = _session.SourceAppUserModelId };
        _lastTimelineUpdate = default;
        _lastReportedPosition = TimeSpan.MinValue;
        RefreshPlayback();
        Changed?.Invoke(this, EventArgs.Empty);
        await RefreshMetadataAsync();
    }

    private async void OnMetadataChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        await _dispatcher.InvokeAsync(() => sender == _session ? RefreshMetadataAsync() : Task.CompletedTask).Task.Unwrap();
    }
    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => QueuePlaybackRefresh(sender);
    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => QueuePlaybackRefresh(sender);
    private void QueuePlaybackRefresh(GlobalSystemMediaTransportControlsSession sender)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() => { if (sender == _session) RefreshPlayback(); }, DispatcherPriority.DataBind);
    }

    private async Task RefreshMetadataAsync()
    {
        var session = _session;
        var generation = ++_generation;
        if (session is null || _disposed) return;
        try
        {
            var properties = await session.TryGetMediaPropertiesAsync();
            ImageSource? artwork = null;
            var artworkHash = "";
            if (properties.Thumbnail is not null)
            {
                try
                {
                    using var stream = await properties.Thumbnail.OpenReadAsync();
                    if (stream.Size <= 8 * 1024 * 1024)
                    {
                        using var reader = new DataReader(stream);
                        await reader.LoadAsync((uint)stream.Size);
                        var bytes = new byte[(int)stream.Size];
                        reader.ReadBytes(bytes);
                        artworkHash = Convert.ToHexString(SHA256.HashData(bytes));
                        if (artworkHash == _artworkHash && Current.Artwork is not null) artwork = Current.Artwork;
                        else
                        {
                            artwork = ArtworkDecoder.Decode(bytes);
                        }
                    }
                }
                catch (Exception ex) { Log.Error("Read album artwork", ex); }
            }
            if (generation != _generation || session != _session || _disposed) return;
            var snapshot = Current with
            {
                HasSession = true,
                Title = string.IsNullOrWhiteSpace(properties.Title) ? "Untitled media" : properties.Title,
                Artist = string.IsNullOrWhiteSpace(properties.Artist) ? session.SourceAppUserModelId.Split('!')[0] : properties.Artist,
                Source = session.SourceAppUserModelId,
                Artwork = artwork
            };
            _artworkHash = artworkHash;
            if (snapshot == Current) return;
            Current = snapshot;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { Log.Error("Read media metadata", ex); }
    }

    private void RefreshPlayback()
    {
        if (_session is null || _disposed) return;
        try
        {
            var info = _session.GetPlaybackInfo();
            var timeline = _session.GetTimelineProperties();
            var playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var duration = timeline.EndTime > timeline.StartTime ? timeline.EndTime - timeline.StartTime : TimeSpan.Zero;
            var reported = timeline.Position > timeline.StartTime ? timeline.Position - timeline.StartTime : TimeSpan.Zero;
            ApplyTimeline(playing, reported, duration, timeline.StartTime, timeline.LastUpdatedTime);
            Current = Current with
            {
                IsPlaying = playing,
                CanToggle = info.Controls.IsPlayPauseToggleEnabled,
                CanPrevious = info.Controls.IsPreviousEnabled,
                CanNext = info.Controls.IsNextEnabled,
                CanSeek = info.Controls.IsPlaybackPositionEnabled,
                Duration = duration
            };
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { Log.Error("Read media playback", ex); }
    }

    internal void ApplyTimeline(bool playing, TimeSpan reported, TimeSpan duration, TimeSpan start, DateTimeOffset updatedAt)
    {
        var position = EstimatedPosition;
        var updated = updatedAt != _lastTimelineUpdate || reported != _lastReportedPosition
            || duration != Current.Duration || start != _timelineStart;
        if (updated)
        {
            var age = DateTimeOffset.UtcNow - updatedAt;
            var candidate = reported + (playing && age >= TimeSpan.Zero && age < TimeSpan.FromSeconds(30) ? age : TimeSpan.Zero);
            // Keep the monotonic clock through provider rounding errors. Larger
            // corrections represent seeks or a change in the playing track.
            if (!Current.HasSession || duration != Current.Duration || start != _timelineStart || Math.Abs((candidate - position).TotalSeconds) > .75) position = candidate;
            _lastTimelineUpdate = updatedAt;
            _lastReportedPosition = reported;
        }
        _timelineAt = Stopwatch.GetTimestamp();
        _timelineStart = start;
        Current = Current with { IsPlaying = playing, Duration = duration,
            Position = TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, Math.Max(0, duration.Ticks))) };
    }

    public TimeSpan EstimatedPosition => Current.IsPlaying && Current.Duration > TimeSpan.Zero
        ? TimeSpan.FromTicks(Math.Clamp((Current.Position + Stopwatch.GetElapsedTime(_timelineAt)).Ticks, 0, Current.Duration.Ticks))
        : Current.Position;

    public async Task<bool> ToggleAsync() => ToggleHandlerForTest is { } handler
        ? Current.HasSession && Current.CanToggle && await handler()
        : await ControlAsync(s => s.TryTogglePlayPauseAsync().AsTask(), Current.CanToggle);
    public async Task<bool> PreviousAsync() => PreviousHandlerForTest is { } handler
        ? Current.HasSession && Current.CanPrevious && await handler()
        : await ControlAsync(s => s.TrySkipPreviousAsync().AsTask(), Current.CanPrevious);
    public async Task<bool> NextAsync() => NextHandlerForTest is { } handler
        ? Current.HasSession && Current.CanNext && await handler()
        : await ControlAsync(s => s.TrySkipNextAsync().AsTask(), Current.CanNext);
    public async Task<bool> SeekAsync(double seconds)
    {
        if (!double.IsFinite(seconds)) return false;
        var position = Math.Clamp(seconds, 0, Current.Duration.TotalSeconds);
        if (SeekHandlerForTest is { } handler) return Current.HasSession && Current.CanSeek && await handler(position);
        return await ControlAsync(s => s.TryChangePlaybackPositionAsync((TimeSpan.FromSeconds(position) + _timelineStart).Ticks).AsTask(), Current.CanSeek);
    }
    private async Task<bool> ControlAsync(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> action, bool enabled)
    {
        if (_session is null || !enabled || _disposed) return false;
        try
        {
            var success = await action(_session);
            if (success) RefreshPlayback();
            return success;
        }
        catch (Exception ex) { Log.Error("Control media playback", ex); return false; }
    }

    private void Detach()
    {
        ++_generation;
        _artworkHash = "";
        if (_session is null) return;
        _session.MediaPropertiesChanged -= OnMetadataChanged;
        _session.PlaybackInfoChanged -= OnPlaybackChanged;
        _session.TimelinePropertiesChanged -= OnTimelineChanged;
        _session = null;
    }
    public void Dispose()
    {
        _disposed = true;
        Detach();
        if (_manager is not null)
        {
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _manager.SessionsChanged -= OnSessionsChanged;
        }
    }
}
