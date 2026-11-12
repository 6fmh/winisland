using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Winisland.Services;

namespace Winisland.Controls;

// A compact, iPhone-style music presentation. Playback and seeking stay owned
// by NotchWindow/MediaService; this view only reports direct user intentions.
public partial class DynamicIslandPlayer : UserControl
{
    private readonly DispatcherTimer _keyboardSeekTimer;
    private bool _updatingTimeline;
    private bool _motionEnabled = true;
    private bool _playing;
    private double _duration;
    private long _elapsedSecond = long.MinValue;
    private double _remainingSecond = double.NaN;
    private double _formattedDuration = double.NaN;
    private string _durationText = "";
    private string? _tooltipElapsed, _tooltipDuration;
    private bool? _tooltipCanSeek;

    public event EventHandler? PreviousRequested;
    public event EventHandler? PlayPauseRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? MenuRequested;
    public event EventHandler<double>? SeekPreviewChanged;
    public event EventHandler<double>? SeekRequested;
    public event EventHandler<bool>? SeekInteractionChanged;

    public bool IsSeeking => SeekSlider.IsAdjusting;
    internal bool HasPendingSeek => IsSeeking || _keyboardSeekTimer.IsEnabled;

    public DynamicIslandPlayer()
    {
        InitializeComponent();
        _keyboardSeekTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _keyboardSeekTimer.Tick += (_, _) =>
        {
            _keyboardSeekTimer.Stop();
            if (IsVisible && SeekSlider.IsEnabled && !IsSeeking)
                SeekRequested?.Invoke(this, SeekSlider.Value);
        };
        SeekSlider.ValueChanged += (_, args) =>
        {
            if (_updatingTimeline || !ReferenceEquals(args.OriginalSource, SeekSlider)) return;
            UpdateSeekFeedback(SeekSlider.Value);
            SeekPreviewChanged?.Invoke(this, SeekSlider.Value);
            if (!IsSeeking && IsVisible && SeekSlider.IsEnabled)
            {
                _keyboardSeekTimer.Stop();
                _keyboardSeekTimer.Start();
            }
        };
        SeekSlider.AdjustmentStarted += (_, _) =>
        {
            _keyboardSeekTimer.Stop();
            SeekInteractionChanged?.Invoke(this, true);
        };
        SeekSlider.AdjustmentCompleted += (_, args) =>
        {
            _keyboardSeekTimer.Stop();
            if (!args.Canceled && IsVisible && SeekSlider.IsEnabled)
                SeekRequested?.Invoke(this, SeekSlider.Value);
            SeekInteractionChanged?.Invoke(this, false);
        };
        Unloaded += (_, _) => CancelInteraction();
        IsVisibleChanged += (_, _) => { if (!IsVisible) CancelInteraction(); };
    }

    public void Refresh(MediaSnapshot snapshot, bool updateTimeline = true)
    {
        _playing = snapshot.HasSession && snapshot.IsPlaying;
        TrackTitle.Text = snapshot.HasSession
            ? (string.IsNullOrWhiteSpace(snapshot.Title) ? "Not playing" : snapshot.Title)
            : "Ready when you are";
        TrackArtist.Text = snapshot.HasSession ? snapshot.Artist : "Play music in your favorite app";
        AlbumArtwork.Source = snapshot.Artwork;
        ArtworkPlaceholder.Visibility = snapshot.Artwork is null ? Visibility.Visible : Visibility.Collapsed;
        PlaybackBars.Visibility = snapshot.HasSession ? Visibility.Visible : Visibility.Collapsed;
        PlayGlyph.Kind = _playing ? NotchIconKind.Pause : NotchIconKind.Play;
        PlayButton.IsEnabled = snapshot.HasSession && snapshot.CanToggle;
        PreviousButton.IsEnabled = snapshot.HasSession && snapshot.CanPrevious;
        NextButton.IsEnabled = snapshot.HasSession && snapshot.CanNext;
        System.Windows.Automation.AutomationProperties.SetName(PlayButton, _playing ? "Pause playback" : "Play playback");
        PlayButton.ToolTip = _playing ? "Pause" : "Play";
        if (updateTimeline)
            UpdateTimeline(snapshot.Position.TotalSeconds, snapshot.Duration.TotalSeconds,
                snapshot.HasSession && snapshot.CanSeek);
        if (!_playing) PlaybackBars.Update(ReadOnlySpan<float>.Empty, 0.1, !_motionEnabled);
    }

    public void UpdateTimeline(double seconds, double duration, bool canSeek)
    {
        _duration = double.IsFinite(duration) ? Math.Max(0, duration) : 0;
        _updatingTimeline = true;
        try
        {
            var enabled = canSeek && _duration > 0;
            if (SeekSlider.IsEnabled != enabled) SeekSlider.IsEnabled = enabled;
            var maximum = Math.Max(1, _duration);
            if (SeekSlider.Maximum != maximum) SeekSlider.Maximum = maximum;
            if (IsSeeking || _keyboardSeekTimer.IsEnabled) return;
            SeekSlider.Value = double.IsFinite(seconds) ? Math.Clamp(seconds, 0, _duration) : 0;
            UpdateSeekFeedback(SeekSlider.Value);
        }
        finally { _updatingTimeline = false; }
    }

    public void SetMotionEnabled(bool enabled)
    {
        _motionEnabled = enabled;
        Resources[InteractionMotion.PreferenceKey] = enabled;
        if (!enabled) PlaybackBars.Update(ReadOnlySpan<float>.Empty, 0.1, true);
    }

    public void UpdateSpectrum(ReadOnlySpan<float> bands, double elapsed) =>
        PlaybackBars.Update(_playing ? bands : ReadOnlySpan<float>.Empty, elapsed, !_motionEnabled);

    public void CancelInteraction()
    {
        _keyboardSeekTimer.Stop();
        SeekSlider.CancelAdjustment();
    }

    private void UpdateSeekFeedback(double seconds)
    {
        seconds = Math.Clamp(seconds, 0, _duration);
        // The rail still receives every fractional frame; textual time changes
        // only at its own display boundary. Remaining time rounds independently.
        var elapsed = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, TimeSpan.MaxValue.TotalSeconds - 1));
        var elapsedSecond = elapsed.Ticks / TimeSpan.TicksPerSecond;
        if (_elapsedSecond != elapsedSecond)
        {
            _elapsedSecond = elapsedSecond;
            ElapsedLabel.Text = FormatTime(seconds);
        }
        var remaining = Math.Ceiling(Math.Max(0, _duration - seconds));
        if (_remainingSecond != remaining)
        {
            _remainingSecond = remaining;
            RemainingLabel.Text = "−" + FormatTime(remaining);
        }
        if (_formattedDuration != _duration)
        {
            _formattedDuration = _duration;
            _durationText = FormatTime(_duration);
        }
        var enabled = SeekSlider.IsEnabled;
        if (_tooltipCanSeek == enabled && _tooltipElapsed == ElapsedLabel.Text && _tooltipDuration == _durationText) return;
        _tooltipCanSeek = enabled;
        _tooltipElapsed = ElapsedLabel.Text;
        _tooltipDuration = _durationText;
        SeekSlider.ToolTip = SeekSlider.IsEnabled
            ? $"{ElapsedLabel.Text} / {_durationText}"
            : "Seeking unavailable for this app";
    }

    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Clamp(double.IsFinite(seconds) ? seconds : 0,
            0, TimeSpan.MaxValue.TotalSeconds - 1));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }

    private void OnPreviousClick(object sender, RoutedEventArgs args) => PreviousRequested?.Invoke(this, EventArgs.Empty);
    private void OnPlayClick(object sender, RoutedEventArgs args) => PlayPauseRequested?.Invoke(this, EventArgs.Empty);
    private void OnNextClick(object sender, RoutedEventArgs args) => NextRequested?.Invoke(this, EventArgs.Empty);
    private void OnMenuClick(object sender, RoutedEventArgs args) => MenuRequested?.Invoke(this, EventArgs.Empty);
}
