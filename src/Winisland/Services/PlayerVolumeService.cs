using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Winisland.Services;

public sealed record PlayerVolumeSnapshot
{
    public bool Available { get; init; }
    public double Volume { get; init; }
    public double Step { get; init; } = .1;
    public bool Muted { get; init; }
    public string Source { get; init; } = "";
    public string Player { get; init; } = "Player";
    public string ConnectionHint { get; init; } = "Play something to control its player volume.";
    public string TargetId { get; init; } = "";
}

// Backends operate on a player's own controls. Windows audio-session volume is
// deliberately absent from this contract and from all implementations.
public interface IPlayerVolumeBackend
{
    bool Supports(MediaSnapshot media);
    Task<PlayerVolumeSnapshot> ReadAsync(MediaSnapshot media, CancellationToken cancellationToken);
    Task<bool> WriteAsync(PlayerVolumeSnapshot desired, CancellationToken cancellationToken);
}

public interface IPlayerVolumeNotifications
{
    event EventHandler? Changed;
}

public sealed class PlayerVolumeService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private IPlayerVolumeBackend[] _backends;
    private readonly CancellationTokenSource _lifetime = new();
    private BrowserVolumeBridge? _browser;
    private bool _started;
    internal bool StartedForTest => _started;
    private CancellationTokenSource _sourceLifetime = new();
    private MediaSnapshot _media = new();
    private PlayerVolumeSnapshot? _pending;
    private bool _readRequested, _disposed;
    private long _generation, _nextRead, _lastFailure;
    private Task _pump = Task.CompletedTask;
    private double _unmutedVolume = 50;
    public PlayerVolumeSnapshot Current { get; private set; } = new();
    public bool BrowserConnected => _browser?.IsConnected ?? false;
    public SpotifyAccount? SpotifyAccount { get { Start(); return _backends.OfType<SpotifyPlayerVolume>().FirstOrDefault()?.Account; } }
    public event EventHandler? Changed;
    public event EventHandler<string>? Failure;

    public PlayerVolumeService(Dispatcher dispatcher, IPlayerVolumeBackend[]? backends = null, bool deferInitialization = false)
    {
        _dispatcher = dispatcher;
        _backends = backends ?? Array.Empty<IPlayerVolumeBackend>();
        if (backends is not null)
        {
            _started = true;
            foreach (var backend in _backends.OfType<IPlayerVolumeNotifications>()) backend.Changed += OnBackendChanged;
        }
        else if (!deferInitialization) Start();
    }

    public void Start()
    {
        if (_disposed || _started) return;
        if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(Start); return; }
        _started = true;
        if (_backends.Length == 0)
        {
            _browser = new BrowserVolumeBridge();
            _backends = new IPlayerVolumeBackend[] { new SpotifyPlayerVolume(), _browser };
        }
        foreach (var backend in _backends.OfType<IPlayerVolumeNotifications>()) backend.Changed += OnBackendChanged;
        _nextRead = 0; _readRequested = true; StartPump();
    }

    public void SetSource(MediaSnapshot media)
    {
        if (_disposed) return;
        if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(() => SetSource(media)); return; }
        var changed = _media.Source != media.Source || _media.HasSession != media.HasSession
            || !SpotifyPlayerVolume.IsSpotifySource(media.Source) && (_media.Title != media.Title || _media.Artist != media.Artist || _media.IsPlaying != media.IsPlaying);
        _media = media;
        if (!changed) return;
        ++_generation;
        _sourceLifetime.Cancel(); _sourceLifetime.Dispose();
        _sourceLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _pending = null; _nextRead = 0; _unmutedVolume = 50;
        Publish(Unavailable(media));
        _readRequested = true;
        StartPump();
    }

    public void Refresh(bool active)
    {
        if (_disposed) return;
        if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(() => Refresh(active)); return; }
        var now = Environment.TickCount64;
        if (now < _nextRead || !Current.Available && !_media.HasSession) return;
        _nextRead = now + (Current.Available ? active ? 150 : 1000 : 2000);
        _readRequested = true;
        StartPump();
    }

    public bool SetVolume(double value)
    {
        if (_disposed || !_dispatcher.CheckAccess() || !Current.Available || !double.IsFinite(value)) return false;
        value = Math.Clamp(value, 0, 100);
        _unmutedVolume = value > 0 ? value : _unmutedVolume;
        _pending = Current with { Volume = value, Muted = value == 0 };
        Publish(_pending);
        StartPump();
        return true;
    }

    public bool SetMuted(bool muted)
    {
        if (_disposed || !_dispatcher.CheckAccess() || !Current.Available) return false;
        var volume = !muted && Current.Volume == 0 ? _unmutedVolume : Current.Volume;
        _pending = Current with { Volume = volume, Muted = muted };
        Publish(_pending);
        StartPump();
        return true;
    }

    public async Task<bool> PrepareSpotifyAccessibilityAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return false;
        var spotify = _backends.OfType<SpotifyPlayerVolume>().FirstOrDefault();
        if (spotify is null) return false;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        var connected = false;
        try { connected = await spotify.PrepareAccessibilityAsync(operation.Token); }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException
            or System.Windows.Automation.ElementNotAvailableException or System.Runtime.InteropServices.COMException or ArgumentException) { }
        if (!_disposed && !_dispatcher.HasShutdownStarted)
            await _dispatcher.InvokeAsync(() => { _nextRead = 0; Refresh(true); }, DispatcherPriority.Background);
        return connected;
    }

    public async Task WhenIdleAsync()
    {
        while (!_disposed)
        {
            var operation = _pump;
            await operation;
            if (ReferenceEquals(operation, _pump) && _pending is null && !_readRequested) return;
            await Task.Delay(1);
        }
    }

    private void StartPump()
    {
        if (_disposed || !_pump.IsCompleted) return;
        _pump = RunPumpAsync();
    }

    private async Task RunPumpAsync()
    {
        // Yield before inspecting the queue so one frame of pointer events can
        // coalesce, and never synchronously call another process's UI provider.
        await Task.Yield();
        while (!_disposed && (_pending is not null || _readRequested))
        {
            var generation = _generation;
            var media = _media;
            var cancellationToken = _sourceLifetime.Token;
            var backend = _backends.FirstOrDefault(b => b.Supports(media));
            if (backend is null)
            {
                _readRequested = false; _pending = null;
                Publish(Unavailable(media));
                continue;
            }
            try
            {
                if (_pending is not null)
                {
                    // Remote providers get at most 25 writes/second. Always take
                    // the newest target after this short interval.
                    await Task.Delay(40, cancellationToken);
                    if (generation != _generation) continue;
                    var desired = _pending;
                    _pending = null;
                    if (desired is null) continue;
                    var accepted = await backend.WriteAsync(desired, cancellationToken);
                    if (generation != _generation || _disposed) continue;
                    if (!accepted)
                    {
                        Publish(Unavailable(media) with { ConnectionHint = "The player could not change its volume. Try reconnecting it." });
                        NotifyFailure("The player could not change its volume.");
                    }
                    _readRequested = true;
                }
                if (_pending is not null || !_readRequested) continue;
                _readRequested = false;
                var snapshot = await backend.ReadAsync(media, cancellationToken);
                if (generation != _generation || _disposed || _pending is not null) continue;
                if (!double.IsFinite(snapshot.Volume)) snapshot = Unavailable(media);
                snapshot = snapshot with { Volume = Math.Clamp(snapshot.Volume, 0, 100), Source = media.Source };
                if (snapshot.Available && !snapshot.Muted && snapshot.Volume > 0) _unmutedVolume = snapshot.Volume;
                Publish(snapshot);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                // Accessibility and disappearing browser processes fail closed.
                // Exceptions never include page data in logs or notifications.
                if (generation == _generation && !_disposed)
                {
                    _pending = null; _readRequested = false;
                    Publish(Unavailable(media));
                }
            }
        }
    }

    private static PlayerVolumeSnapshot Unavailable(MediaSnapshot media) => new()
    {
        Source = media.Source,
        ConnectionHint = !media.HasSession ? "Play something to control its player volume."
            : SpotifyPlayerVolume.IsSpotifySource(media.Source) ? "Open Player controls in Settings and choose Connect Spotify."
            : BrowserVolumeBridge.IsBrowserSource(media.Source) ? "Connect the browser companion to control this player's volume."
            : "This app does not expose its own player volume."
    };

    private void Publish(PlayerVolumeSnapshot snapshot)
    {
        if (_disposed || snapshot == Current) return;
        Current = snapshot;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyFailure(string message)
    {
        var now = Environment.TickCount64;
        if (now - _lastFailure < 2000) return;
        _lastFailure = now;
        Failure?.Invoke(this, message);
    }

    private void OnBackendChanged(object? sender, EventArgs e)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            _nextRead = 0;
            Refresh(true);
            // Connection status matters even when the media identity is absent.
            Changed?.Invoke(this, EventArgs.Empty);
        }, DispatcherPriority.DataBind);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel(); _sourceLifetime.Cancel();
        foreach (var backend in _backends.OfType<IPlayerVolumeNotifications>()) backend.Changed -= OnBackendChanged;
        foreach (var backend in _backends.OfType<IDisposable>()) backend.Dispose();
        _lifetime.Dispose(); _sourceLifetime.Dispose();
    }
}
