using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Winisland.Controls;
using Winisland.Services;

namespace Winisland;

public partial class NotchWindow : Window
{
    private readonly SettingsStore _settings;
    private readonly MediaService _media;
    private readonly AudioService _audio;
    private readonly PlayerVolumeService _playerVolume;
    private readonly bool _ownsPlayerVolume;
    private readonly FocusTimer _focus;
    private readonly FileShelf _shelf;
    private readonly ShelfUploadService _uploads;
    private readonly bool _ownsUploads;
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(240) };
    private readonly Stopwatch _visualizerClock = new();
    private bool _visualizerRunning;
    private TimeSpan _lastVisualizerFrame = TimeSpan.MinValue;
    private readonly AudioSpectrumService _spectrum = new();
    private readonly float[] _spectrumBands = new float[SpectrumAnalyzer.BandCount];
    private long _nextSpectrumDeviceCheck;
    private readonly DispatcherTimer _pulse = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _playerPoll = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _playbackRendering;
    private TimeSpan _lastPlaybackFrame = TimeSpan.MinValue;
    private readonly DispatcherTimer _hudTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _seekKeyboardTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private double? _pendingSeekPosition;
    private DateTime _seekConfirmationDeadline;
    private int _seekVersion;
    private (string Source, string Title, string Artist) _seekIdentity;
    private PlayerVolumeSnapshot? _lastAudio;
    private BatterySnapshot? _lastBattery;
    private bool _updatingVolume, _updatingSeek, _seeking, _expanded, _pinned, _showingHud, _fullscreenHidden;
    private bool _dialogOpen;
    private bool _allowClose;
    private bool _viewInitialized;
    private bool _pointerInside, _dragActive;
    private int _pulseCount;
    private bool _changingMediaSource;
    private long _elapsedFeedbackSecond = long.MinValue;
    private bool _elapsedFeedbackHasTime;
    private string? _seekTooltipElapsed, _seekTooltipDuration;
    private bool? _seekTooltipEnabled;
    private long _lastPlayerFailure;
    private Point _dragStart;
    private double _targetWidth = -1, _targetHeight = -1;
    private double _targetExpansion = -1;
    private SizeMotion? _sizeMotion;
    private int _sizeTransitionVersion;
    private ImageSource? _lastArtwork;
    private bool? _artworkPlaying;
    private int _transitionVersion;
    private int _fullscreenTransitionVersion;
    private string _selectedTab = "music";
    private HwndSource? _source;
    private IntPtr? _testForeground;
    private FullscreenWatcher? _fullscreenWatcher;
    private IntPtr _previousForeground;
    public bool IsExpanded => _expanded;
    internal bool IsPinnedForTest => _pinned;
    internal bool PlaybackRenderingForTest => _playbackRendering;
    public event EventHandler? SettingsRequested;
    public event EventHandler? UploadSettingsRequested;
    public event EventHandler? PlayerSettingsRequested;
    public event EventHandler<string>? Notification;

    public NotchWindow(SettingsStore settings, MediaService media, AudioService audio, FocusTimer focus, FileShelf shelf, ShelfUploadService? uploads = null, PlayerVolumeService? playerVolume = null, UpdateService? updates = null)
    {
        _updates = updates;
        _settings = settings; _media = media; _audio = audio; _focus = focus; _shelf = shelf;
        _ownsUploads = uploads is null;
        _uploads = uploads ?? new ShelfUploadService(shelf, new UploadCredentialStore());
        _ownsPlayerVolume = playerVolume is null;
        _playerVolume = playerVolume ?? new PlayerVolumeService(Dispatcher, deferInitialization: true);
        InitializeComponent();
        _viewInitialized = true;
        InitializeNotifications();
        PreviewKeyDown += (_, args) => OnNotificationKeyDown(args);
        InitializeUpdates();
        InitializeFocus();
        InitializeAppleDevices();
        Surface.ContourChanged += (_, _) => UpdateContour();
        InitializeDynamicIsland();
        UpdateCameraSpace();
        SyncMotionPreference();
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        WatchOverlayVisibility();
        SetTab("music");
        ShelfItems.ItemsSource = shelf.Items;
        SourceInitialized += OnSourceInitialized;
        ContentRendered += OnFirstContentRendered;
        _collapseTimer.Tick += (_, _) => { _collapseTimer.Stop(); CollapseIfPointerOutside(); };
        VolumeSlider.LostMouseCapture += (_, _) =>
        {
            RefreshAudio(false);
            if (!_pointerInside && !_pinned) { _collapseTimer.Stop(); _collapseTimer.Start(); }
        };
        _hudTimer.Tick += (_, _) => { _hudTimer.Stop(); _showingHud = false; UpdateCompact(); };
        _seekKeyboardTimer.Tick += async (_, _) => { _seekKeyboardTimer.Stop(); await CommitSeekAsync(SeekSlider.Value); };
        SeekSlider.ValueChanged += (_, args) =>
        {
            if (_updatingSeek || !ReferenceEquals(args.OriginalSource, SeekSlider)) return;
            UpdateSeekFeedback(SeekSlider.Value);
            if (!_seeking && _expanded && SeekSlider.IsEnabled) { _seekKeyboardTimer.Stop(); _seekKeyboardTimer.Start(); }
        };
        SeekSlider.AdjustmentStarted += (_, _) =>
        {
            ++_seekVersion;
            _seeking = true; _pendingSeekPosition = null;
            _seekKeyboardTimer.Stop(); _collapseTimer.Stop();
        };
        SeekSlider.AdjustmentCompleted += async (_, args) =>
        {
            _seeking = false;
            if (args.Canceled) UpdateTimeline();
            else if (_expanded && SeekSlider.IsEnabled) await CommitSeekAsync(SeekSlider.Value);
            if (!_pointerInside && !_pinned) { _collapseTimer.Stop(); _collapseTimer.Start(); }
        };
        _pulse.Tick += OnPulse;
        _playerPoll.Tick += (_, _) => _playerVolume.Refresh(_expanded && _selectedTab == "music");
        _settings.Changed += OnSettingsChanged;
        _media.Changed += OnMediaChanged;
        _focus.Changed += OnFocusChanged;
        _focus.Completed += OnFocusCompleted;
        _shelf.Items.CollectionChanged += OnShelfChanged;
        _uploads.Changed += OnUploadsChanged;
        _playerVolume.Changed += OnPlayerVolumeChanged;
        _playerVolume.Failure += OnPlayerVolumeFailure;
        RefreshMedia(); RefreshFocus(); RefreshShelf(); UpdateCompact();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.ConfigureOverlay(handle);
        _source = HwndSource.FromHwnd(handle);
        _source.AddHook(WindowHook);
        if (!BackgroundVerification.IsWorker)
        {
            var openHotkey = NativeMethods.RegisterHotKey(handle, 1, 0x4003, 0x4E); // Ctrl + Alt + N, no repeat
            var settingsHotkey = NativeMethods.RegisterHotKey(handle, 2, 0x4003, 0x53);
            if (!openHotkey || !settingsHotkey) Notification?.Invoke(this, "A keyboard shortcut is already in use. The notch and tray controls still work.");
        }
        NativeMethods.PositionOverlay(this, _settings.Current.MonitorDevice);
        _fullscreenWatcher = new FullscreenWatcher(Dispatcher, handle, UpdateFullscreenVisibility,
            () => _testForeground ?? NativeMethods.GetForegroundWindow());
        UpdateFullscreenVisibility();
    }

    private IntPtr WindowHook(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == NativeMethods.WmMouseActivate && !_pinned) { handled = true; return new IntPtr(3); }
        if (message == NativeMethods.WmHotkey)
        {
            if (wParam.ToInt32() == 1) { if (_expanded) Collapse(); else Expand(true); }
            else if (wParam.ToInt32() == 2) SettingsRequested?.Invoke(this, EventArgs.Empty);
            handled = true;
        }
        if (message is NativeMethods.WmDpiChanged or NativeMethods.WmDisplayChange)
            Dispatcher.BeginInvoke(() =>
            {
                NativeMethods.PositionOverlay(this, _settings.Current.MonitorDevice);
                UpdateFullscreenVisibility();
            }, DispatcherPriority.Loaded);
        if (message == NativeMethods.WmDwmCompositionChanged) QueueOverlayRecovery();
        return IntPtr.Zero;
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (!_settings.Current.ShowNotifications) DismissNotification(true);
        SyncMotionPreference();
        ApplyNotchAppearance();
        UpdateCameraSpace();
        SyncFocusSettings();
        NativeMethods.PositionOverlay(this, _settings.Current.MonitorDevice);
        UpdateFullscreenVisibility();
        if (_settings.Current.ReducedMotion || !SystemParameters.ClientAreaAnimation)
        {
            AnimateFullscreenVisibility(_fullscreenHidden, false);
            FinishPanelTransitions();
            _artworkPlaying = null;
            UpdateArtwork();
        }
        if (!_expanded) UpdateCompact();
        else
        {
            UpdateExpandedLayout();
            FinishTabTransitions();
            AnimateSize(ExpandedPanelWidth, ExpandedPanelHeight);
            UpdateDevicePresentation();
            if (_activeNotification is not null) UpdateNotificationPresentation();
        }
        UpdateVisualizer();
    }
    private bool MotionEnabled => !_settings.Current.ReducedMotion && SystemParameters.ClientAreaAnimation;
    private void SyncMotionPreference()
    {
        Application.Current.Resources[InteractionMotion.PreferenceKey] = MotionEnabled;
        DynamicPlayer.SetMotionEnabled(MotionEnabled);
    }
    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SystemParameters.ClientAreaAnimation)) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnSystemParametersChanged(sender, e));
            return;
        }
        SyncMotionPreference();
        if (!MotionEnabled)
        {
            AnimateFullscreenVisibility(_fullscreenHidden, false);
            FinishPanelTransitions();
            _artworkPlaying = null;
            UpdateArtwork();
        }
        UpdateVisualizer();
    }
    private void OnMediaChanged(object? sender, EventArgs e) { RefreshMedia(); UpdateCompact(); }
    private void OnFocusChanged(object? sender, EventArgs e) { RefreshFocus(); UpdateCompact(); }
    private void OnShelfChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => RefreshShelf();
    private void OnUploadsChanged(object? sender, EventArgs e) { RefreshShelf(); UpdateCompact(); }

    private void OnPulse(object? sender, EventArgs e)
    {
        // Fallback for applications that do not emit the expected WinEvents.
        UpdateFullscreenVisibility();
        if (!_playbackRendering && _expanded && _selectedTab == "music" && _media.Current.IsPlaying && !_seeking) UpdateTimeline();
        if (++_pulseCount % 4 != 1) return;
        MaintainOverlayVisibility();
        var battery = NativeMethods.GetBattery();
        if (_lastBattery is not null && battery.PluggedIn != _lastBattery.PluggedIn && battery.HasBattery)
            ShowHud(battery.PluggedIn ? "Power connected" : "On battery", NotchIconKind.Battery, battery.Percent ?? 0, battery.Percent is { } percent ? $"{percent}%" : "—");
        _lastBattery = battery;
        BatteryStatus.Visibility = battery.HasBattery && !(_expanded && _deviceConnection is not null) ? Visibility.Visible : Visibility.Collapsed;
        BatteryLabel.Text = battery.Percent is { } p ? $"{p}%" : "";
        BatteryStatus.ToolTip = battery.Charging ? "Charging" : battery.PluggedIn ? "Power connected" : "On battery";
        BatteryGlyph.Kind = battery.Charging ? NotchIconKind.ChargingBattery : NotchIconKind.Battery;
        BatteryGlyph.BatteryLevel = battery.Percent ?? 0;
    }

    private void UpdateFullscreenVisibility()
    {
        var ownHandle = new WindowInteropHelper(this).Handle;
        var fullscreen = _settings.Current.HideInFullscreen && (_testForeground is { } foreground
            ? NativeMethods.IsFullscreenWindowOn(foreground, _settings.Current.MonitorDevice, ownHandle)
            : NativeMethods.IsFullscreenOn(_settings.Current.MonitorDevice, ownHandle));
        if (fullscreen != _fullscreenHidden)
        {
            _fullscreenHidden = fullscreen;
            AnimateFullscreenVisibility(fullscreen);
            if (fullscreen) { DismissNotification(true); DismissDeviceConnection(true); Collapse(); }
            UpdateVisualizer();
        }
    }

    internal void SetForegroundWindowForTest(IntPtr handle)
    {
        BackgroundVerification.RequirePrivateDesktop();
        _testForeground = handle;
        // Exercise the same queued foreground-event path without changing the
        // session's physical input focus. Native fixture resize events remain real.
        _fullscreenWatcher?.NotifyForegroundChangedForTest();
    }

    private void AnimateFullscreenVisibility(bool hidden, bool animate = true)
    {
        var version = ++_fullscreenTransitionVersion;
        var opacity = Island.Opacity;
        var offset = FullscreenOffset.Y;
        var hiddenOffset = -Math.Max(Island.ActualHeight, ClosedNotchHeight) - AppearanceTopInset - 16;
        Island.BeginAnimation(OpacityProperty, null);
        FullscreenOffset.BeginAnimation(TranslateTransform.YProperty, null);
        Island.Opacity = hidden ? 0 : 1;
        FullscreenOffset.Y = hidden ? hiddenOffset : 0;
        // Yield pointer input immediately, even while the last frames fade out.
        Island.IsHitTestVisible = !hidden;
        var duration = animate && IsLoaded ? DurationFor(hidden ? 100 : 120) : new Duration(TimeSpan.Zero);
        if (duration.TimeSpan == TimeSpan.Zero)
        {
            Island.Visibility = hidden ? Visibility.Hidden : Visibility.Visible;
            return;
        }
        if (!hidden && Island.Visibility != Visibility.Visible)
        {
            opacity = 0;
            offset = hiddenOffset;
        }
        Island.Visibility = Visibility.Visible;
        // Pending animation clocks must expose the current presentation, rather
        // than the destination, if another fullscreen event arrives this frame.
        Island.Opacity = opacity;
        FullscreenOffset.Y = offset;
        // Tuck the whole silhouette and its shadow into the top edge. Keep the
        // notch visible early in the exit so the short movement is perceptible.
        var ease = new CubicEase { EasingMode = hidden ? EasingMode.EaseIn : EasingMode.EaseOut };
        var fade = new DoubleAnimation(opacity, hidden ? 0 : 1, duration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };
        // A quick fullscreen exit or an explicit opening can reverse the motion.
        fade.Completed += (_, _) =>
        {
            if (version == _fullscreenTransitionVersion)
            {
                Island.Opacity = hidden ? 0 : 1;
                FullscreenOffset.Y = hidden ? hiddenOffset : 0;
                Island.Visibility = hidden ? Visibility.Hidden : Visibility.Visible;
            }
        };
        Island.BeginAnimation(OpacityProperty, fade);
        FullscreenOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offset, hidden ? hiddenOffset : 0, duration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        });
    }

    private void RefreshAudio(bool showHud = true)
    {
        var audio = _playerVolume.Current;
        if (_islandMenuOpen) RefreshIslandMenu();
        if (audio != _lastAudio)
        {
            if (showHud && _lastAudio is { Available: true } && audio.Available && audio.Source == _lastAudio.Source && !_expanded && _settings.Current.ShowVolumeHud)
                ShowHud(audio.Muted ? "Player muted" : "Player volume", audio.Muted ? NotchIconKind.MutedSpeaker : NotchIconKind.Speaker, audio.Volume, audio.Muted ? "Off" : $"{audio.Volume:0}%");
            _lastAudio = audio;
            _updatingVolume = true;
            // Player readbacks must not pull an active drag away from its pointer.
            if (!VolumeSlider.IsAdjusting || !audio.Available) VolumeSlider.Value = audio.Volume;
            VolumeSlider.IsEnabled = audio.Available; MuteButton.IsEnabled = audio.Available;
            VolumeSlider.WheelChange = audio.TargetId.StartsWith("spotify-api:", StringComparison.Ordinal) ? 1 : Math.Max(2, audio.Step);
            VolumeSlider.SmallChange = Math.Max(1, audio.Step);
            VolumeSlider.LargeChange = Math.Max(5, audio.Step);
            VolumeGlyph.Kind = audio.Muted ? NotchIconKind.MutedSpeaker : NotchIconKind.Speaker;
            VolumeGlyph.SoundLevel = audio.Volume;
            MuteButton.ToolTip = audio.Available ? (audio.Muted ? "Unmute player" : "Mute player") : audio.ConnectionHint;
            VolumeSlider.ToolTip = audio.Available ? $"{audio.Player} volume · {audio.Volume:0}%"
                + (audio.Step > 2 ? $" · {audio.Step:0.#}% steps" : "") : audio.ConnectionHint;
            PlayerVolumeControls.Visibility = audio.Available || !_media.Current.HasSession ? Visibility.Visible : Visibility.Collapsed;
            PlayerConnectionHint.Visibility = !audio.Available && _media.Current.HasSession ? Visibility.Visible : Visibility.Collapsed;
            PlayerConnectionHint.Content = _media.Current.Source.Contains("spotify", StringComparison.OrdinalIgnoreCase)
                ? "Set up Spotify player volume" : BrowserVolumeBridge.IsBrowserSource(_media.Current.Source)
                    ? "Connect browser for player volume" : "Player volume unavailable";
            PlayerConnectionHint.ToolTip = audio.ConnectionHint;
            System.Windows.Automation.AutomationProperties.SetName(MuteButton, audio.Muted ? "Unmute player" : "Mute player");
            _updatingVolume = false;
        }
    }

    public void Expand(bool pin)
    {
        _collapseTimer.Stop(); _hudTimer.Stop(); _showingHud = false;
        if (_expanded && !pin) return;
        if (pin && !_pinned) _previousForeground = NativeMethods.GetForegroundWindow();
        _expanded = true; _pinned = pin; _fullscreenHidden = false;
        SetMinimalActivity(false);
        AnimateFullscreenVisibility(false, false);
        PinGlyph.Foreground = pin ? (Brush)FindResource("Accent") : (Brush)FindResource("Muted");
        CompactActivity.Visibility = Hud.Visibility = Visibility.Collapsed;
        UpdateDevicePresentation();
        UpdateExpandedLayout();
        FinishTabTransitions();
        UpdateVisualizer();
        AnimateSize(ExpandedPanelWidth, ExpandedPanelHeight);
        AnimateExpandedContent(true);
        if (_activeNotification is not null) UpdateNotificationPresentation();
        if (_selectedTab == "controls") ControlCenter.SetActive(true);
        if (pin)
        {
            var handle = new WindowInteropHelper(this).Handle;
            NativeMethods.SetActivatable(handle, true);
            Activate();
            SelectedTabControl.Focus();
        }
    }

    public void Collapse()
    {
        ResetIslandMenu();
        _controlCenter?.SetActive(false);
        _controlCenter?.CancelAdjustment();
        CancelSeekAdjustment();
        VolumeSlider.CancelAdjustment();
        _expanded = _pinned = false;
        _collapseTimer.Stop();
        AnimateExpandedContent(false);
        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.SetActivatable(handle, false);
        if (!BackgroundVerification.IsWorker && NativeMethods.GetForegroundWindow() == handle)
        {
            var restored = _previousForeground != IntPtr.Zero && _previousForeground != handle
                && NativeMethods.SetForegroundWindow(_previousForeground);
            // The previous app can close while the notch is pinned. Release our
            // own activation even when there is no valid window to return to.
            if (!restored && NativeMethods.GetForegroundWindow() == handle)
                NativeMethods.SetActiveWindow(IntPtr.Zero);
        }
        _previousForeground = IntPtr.Zero;
        UpdateCompact();
    }

    private void AnimateSize(double width, double height, int milliseconds = 600)
    {
        if (IsDynamicIsland) milliseconds = Math.Min(milliseconds, 450);
        var expansion = _expanded ? 1.0 : _activeNotification is not null ? 0.65 : _deviceConnection is not null ? 0.25 : 0.0;
        if (_targetWidth == width && _targetHeight == height && _targetExpansion == expansion) return;
        _targetWidth = width; _targetHeight = height;
        _targetExpansion = expansion;
        var fromWidth = Island.Width; var fromHeight = Island.Height; var fromExpansion = Surface.Expansion;
        var velocities = _sizeMotion?.PresentedVelocities() ?? (Width: 0.0, Height: 0.0, Contour: 0.0);
        var distance = Math.Max(Math.Abs(width - fromWidth) / Math.Max(1, ExpandedPanelWidth - (IsDynamicIsland ? DynamicIslandMetrics.IdleWidth : _settings.Current.NotchWidth)),
            Math.Abs(height - fromHeight) / Math.Max(1, BaseExpandedPanelHeight - ClosedNotchHeight));
        var duration = DurationFor((int)(milliseconds * Math.Clamp(distance, 0.65, 1)));
        var version = ++_sizeTransitionVersion;
        SpringEase CreateEase(double velocity, double distance) => new()
        {
            // Apple's recommended general-purpose spring has no bounce. Desktop
            // input gets a restrained response while preserving retarget velocity.
            DampingRatio = 1,
            ResponseSeconds = IsDynamicIsland ? (_expanded ? 0.35 : 0.38) : (_expanded ? 0.42 : 0.45),
            AnimationSeconds = Math.Max(0.01, duration.TimeSpan.TotalSeconds),
            InitialVelocity = Math.Abs(distance) > 0.000001 ? velocity / distance : 0
        };
        Island.BeginAnimation(WidthProperty, null); Island.BeginAnimation(HeightProperty, null);
        Surface.BeginAnimation(NotchSurface.ExpansionProperty, null);
        _sizeMotion = null;
        if (duration.TimeSpan == TimeSpan.Zero)
        {
            Island.Width = width; Island.Height = height; Surface.Expansion = expansion;
            return;
        }
        // Pending clocks expose the snapshot, including on a same-frame reversal.
        Island.Width = fromWidth; Island.Height = fromHeight; Surface.Expansion = fromExpansion;
        var widthEase = CreateEase(velocities.Width, width - fromWidth);
        var heightEase = CreateEase(velocities.Height, height - fromHeight);
        var contourEase = CreateEase(velocities.Contour, expansion - fromExpansion);
        var widthClock = new DoubleAnimation(fromWidth, width, duration) { EasingFunction = widthEase }.CreateClock();
        _sizeMotion = new SizeMotion(widthClock, duration.TimeSpan.TotalSeconds,
            width - fromWidth, height - fromHeight, expansion - fromExpansion,
            widthEase, heightEase, contourEase);
        widthClock.Completed += (_, _) =>
        {
            if (version != _sizeTransitionVersion) return;
            Island.Width = width; Island.Height = height; Surface.Expansion = expansion;
            Island.BeginAnimation(WidthProperty, null); Island.BeginAnimation(HeightProperty, null);
            Surface.BeginAnimation(NotchSurface.ExpansionProperty, null);
            _sizeMotion = null;
        };
        Island.ApplyAnimationClock(WidthProperty, widthClock);
        Island.BeginAnimation(HeightProperty, new DoubleAnimation(fromHeight, height, duration) { EasingFunction = heightEase });
        Surface.BeginAnimation(NotchSurface.ExpansionProperty, new DoubleAnimation(fromExpansion, expansion, duration) { EasingFunction = contourEase });
    }
    private sealed record SizeMotion(AnimationClock Clock, double Seconds,
        double WidthDistance, double HeightDistance, double ContourDistance,
        SpringEase WidthEase, SpringEase HeightEase, SpringEase ContourEase)
    {
        internal (double Width, double Height, double Contour) PresentedVelocities()
        {
            var progress = Math.Clamp((Clock.CurrentTime?.TotalSeconds ?? 0) / Seconds, 0, 1);
            return (WidthDistance * WidthEase.VelocityAt(progress),
                HeightDistance * HeightEase.VelocityAt(progress),
                ContourDistance * ContourEase.VelocityAt(progress));
        }
    }
    private void AnimateExpandedContent(bool opening)
    {
        var version = ++_transitionVersion;
        var wasHidden = ExpandedContent.Visibility != Visibility.Visible;
        var opacity = wasHidden ? 0 : ExpandedContent.Opacity;
        var offset = wasHidden && opening ? -4 : ContentOffset.Y;
        ExpandedContent.BeginAnimation(OpacityProperty, null);
        ContentOffset.BeginAnimation(TranslateTransform.YProperty, null);
        ContentScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ContentScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        // Keep text and controls at their native size. Apple recommends subdued
        // motion for mouse/trackpad input; the surface carries the spatial change.
        ContentScale.ScaleX = ContentScale.ScaleY = 1;
        ExpandedContent.Opacity = opening ? 1 : 0;
        ContentOffset.Y = opening || !MotionEnabled ? 0 : -3;
        ExpandedContent.Visibility = Visibility.Visible;
        ExpandedContent.IsHitTestVisible = opening;
        if (!MotionEnabled)
        {
            ExpandedContent.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        if (opening && opacity >= 1 && Math.Abs(offset) < 0.001) return;
        ExpandedContent.Opacity = opacity;
        ContentOffset.Y = offset;
        var fade = new DoubleAnimation(opacity, opening ? 1 : 0, DurationFor(opening ? 220 : 120))
        {
            BeginTime = TimeSpan.FromMilliseconds(opening && wasHidden ? 60 : 0),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        fade.Completed += (_, _) =>
        {
            if (version != _transitionVersion) return;
            ExpandedContent.Opacity = opening ? 1 : 0;
            ContentOffset.Y = opening ? 0 : -3;
            ExpandedContent.BeginAnimation(OpacityProperty, null);
            ContentOffset.BeginAnimation(TranslateTransform.YProperty, null);
            if (!opening && !_expanded) ExpandedContent.Visibility = Visibility.Collapsed;
        };
        ExpandedContent.BeginAnimation(OpacityProperty, fade);
        ContentOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offset, opening ? 0 : -3, DurationFor(opening ? 280 : 120)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
    private Duration DurationFor(int milliseconds) => new(TimeSpan.FromMilliseconds(MotionEnabled ? milliseconds : 0));
    private void OnIslandSizeChanged(object sender, SizeChangedEventArgs e) => UpdateContour();
    private void UpdateContour()
    {
        if (Surface is null || DropHighlight is null) return;
        var geometry = NotchSurface.CreateGeometry(Island.ActualWidth, Island.ActualHeight, Surface.Expansion, IsDynamicIsland);
        Island.Clip = geometry; DropHighlight.Data = geometry;
        var shoulder = NotchMetrics.ClosedShoulder + (NotchMetrics.OpenShoulder - NotchMetrics.ClosedShoulder) * Surface.Expansion;
        TopSeam.Margin = new Thickness(shoulder, 0, shoulder, 0);
        UpdateMinimalPosition();
    }
    private void UpdateCameraSpace()
    {
        CompactCameraSpace.Width = IsDynamicIsland ? DynamicIslandMetrics.CameraWidth : _settings.Current.NotchWidth - 2 * NotchMetrics.ClosedShoulder;
        HeaderCameraSpace.Width = IsDynamicIsland ? 0 : _settings.Current.NotchWidth - 2 * NotchMetrics.ClosedShoulder;
        // Reserve the actual tab margins and round down so layout rounding never
        // pushes the last tab into the fixed camera area at wider custom sizes.
        var leftTabs = new[] { MusicTab, FilesTab, FocusTab, CalendarTab, UpdateTab }.Where(tab => tab.Visibility != Visibility.Collapsed).ToArray();
        var wingWidth = (ExpandedContent.Width - HeaderCameraSpace.Width) / 2;
        var tabMargins = leftTabs.Sum(tab => tab.Margin.Left + tab.Margin.Right);
        var tabWidth = Math.Floor(Math.Min(40, (wingWidth - tabMargins) / leftTabs.Length));
        MusicTab.Width = FilesTab.Width = FocusTab.Width = CalendarTab.Width = UpdateTab.Width = ToolsTab.Width = tabWidth;
        var utilityWidth = wingWidth < 170 ? 24 : 32;
        PinButton.Width = NotchSettingsButton.Width = utilityWidth;
        var rightTabWidth = Math.Floor(Math.Min(tabWidth, (wingWidth - 2 * utilityWidth - 38) / 2));
        ToolsTab.Width = rightTabWidth;
        ControlCenterTab.Width = Math.Min(32, rightTabWidth);
        BatteryLabel.Visibility = wingWidth >= 210 ? Visibility.Visible : Visibility.Collapsed;
        DeviceHeaderLabel.Visibility = wingWidth >= 226 ? Visibility.Visible : Visibility.Collapsed;
        BatteryStatus.Margin = new Thickness(0, 0, 4, 0);
        BatteryGlyph.Margin = new Thickness(4, 0, 0, 0);
    }
    private void FinishPanelTransitions()
    {
        ++_transitionVersion;
        ++_sizeTransitionVersion;
        _sizeMotion = null;
        Island.BeginAnimation(WidthProperty, null); Island.BeginAnimation(HeightProperty, null);
        if (_targetWidth >= 0) { Island.Width = _targetWidth; Island.Height = _targetHeight; }
        Surface.BeginAnimation(NotchSurface.ExpansionProperty, null); Surface.Expansion = _expanded ? 1 : _activeNotification is not null ? 0.65 : _deviceConnection is not null ? 0.25 : 0;
        ExpandedContent.BeginAnimation(OpacityProperty, null); ExpandedContent.Opacity = _expanded ? 1 : 0;
        ExpandedContent.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        ContentOffset.BeginAnimation(TranslateTransform.YProperty, null); ContentOffset.Y = 0;
        ContentScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); ContentScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ContentScale.ScaleX = ContentScale.ScaleY = 1;
        CompactActivity.BeginAnimation(OpacityProperty, null); CompactActivity.Opacity = 1;
        DropHighlight.BeginAnimation(OpacityProperty, null); DropHighlight.Opacity = _dragActive ? 1 : 0;
        FinishTabTransitions();
        FinishDeviceAnimations();
        FinishDynamicAnimations();
        if (_activeNotification is not null) UpdateNotificationPresentation();
        UpdateContour();
    }
    private void OnIslandEnter(object sender, MouseEventArgs e)
    {
        _pointerInside = true;
        _collapseTimer.Stop();
        if (_activeNotification is not null) _notificationTimer.Stop();
        if (_activeNotification is null && _settings.Current.ExpandOnHover && !_expanded) Expand(false);
    }
    private void OnIslandLeave(object sender, MouseEventArgs e)
    {
        _pointerInside = false;
        if (_activeNotification is not null) ResumeNotificationTimer();
        _collapseTimer.Stop();
        CollapseIfPointerOutside();
    }
    private void CollapseIfPointerOutside()
    {
        if (_expanded && !_pointerInside && !_pinned && !_dialogOpen && !_dragActive
            && !IsFocusEditing && _organizer?.IsEditing != true
            && _controlCenter?.IsInteracting != true
            && !VolumeSlider.IsAdjusting && !SeekSlider.IsAdjusting && !DynamicPlayer.IsSeeking && !IslandMenuPanel.IsAdjustingVolume)
            Collapse();
    }
    private void OnIslandClick(object sender, MouseButtonEventArgs e)
    {
        if (_activeNotification is not null && _notificationBanner.IsMouseOver) { e.Handled = true; return; }
        if (!_pinned) { Expand(true); e.Handled = true; }
    }
    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        if (!_pinned) _previousForeground = NativeMethods.GetForegroundWindow();
        _pinned = !_pinned;
        PinGlyph.Foreground = _pinned ? (Brush)FindResource("Accent") : (Brush)FindResource("Muted");
        NativeMethods.SetActivatable(new WindowInteropHelper(this).Handle, _pinned);
        if (_pinned) { Activate(); PinButton.Focus(); }
        if (!_pinned) _collapseTimer.Start();
    }
    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    private void OnUploadSettingsClick(object sender, RoutedEventArgs e) => UploadSettingsRequested?.Invoke(this, EventArgs.Empty);
    private void OnPlayerSetupClick(object sender, RoutedEventArgs e) => PlayerSettingsRequested?.Invoke(this, EventArgs.Empty);
    private async void OnIslandMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_activeNotification is not null && _notificationBanner.IsMouseOver) return;
        if (e.ChangedButton == MouseButton.Middle && _media.Current.CanToggle)
        { e.Handled = true; await ReportControlAsync(_media.ToggleAsync()); }
    }
    private void OnIslandWheel(object sender, MouseWheelEventArgs e)
    {
        if (_activeNotification is not null && (!_expanded || _notificationBanner.IsMouseOver)) { e.Handled = true; return; }
        if (_expanded) return;
        var audio = _playerVolume.Current;
        if (!audio.Available) return;
        var step = audio.TargetId.StartsWith("spotify-api:", StringComparison.Ordinal) ? 1 : Math.Max(2, audio.Step);
        var volume = Math.Clamp(audio.Volume + Math.Sign(e.Delta) * step, 0, 100);
        if (_playerVolume.SetVolume(volume) && _settings.Current.ShowVolumeHud) ShowHud("Player volume", NotchIconKind.Speaker, volume, $"{volume:0}%");
        RefreshAudio(false);
        e.Handled = true;
    }

    private void UpdateCompact()
    {
        if (_activeNotification is not null) { UpdateNotificationPresentation(); return; }
        if (_expanded || _showingHud) return;
        if (_deviceConnection is not null) { UpdateDevicePresentation(); return; }
        DeviceHeaderStatus.Visibility = Visibility.Collapsed;
        var uploading = _uploads.PendingCount > 0;
        var update = !uploading && _updates?.HasUpdate == true;
        var music = _media.Current.HasSession;
        var activity = update || _settings.Current.ShowLiveActivity && (_focus.IsStarted || music || uploading);
        if (IsDynamicIsland) { UpdateDynamicCompact(activity, music, uploading, update); return; }
        var wasHidden = CompactActivity.Visibility != Visibility.Visible;
        CompactActivity.Visibility = activity ? Visibility.Visible : Visibility.Collapsed;
        Hud.Visibility = Visibility.Collapsed;
        CompactArtwork.Source = uploading || update ? null : music ? _media.Current.Artwork : null;
        CompactGlyph.Visibility = CompactArtwork.Source is null ? Visibility.Visible : Visibility.Collapsed;
        CompactGlyph.Kind = uploading ? NotchIconKind.Upload : update ? NotchIconKind.Download : _focus.IsStarted && !music ? NotchIconKind.Timer : NotchIconKind.Music;
        CompactGlyph.Foreground = (Brush)FindResource(uploading || update ? "ActivityBlue" : _focus.IsStarted && !music ? "FocusOrange" : "Accent");
        CompactBars.Visibility = activity && !uploading && !update && _media.Current.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
        CompactLabel.Visibility = activity && (uploading || update || _focus.IsStarted && CompactBars.Visibility != Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
        CompactLabel.Foreground = (Brush)FindResource(uploading || update ? "ActivityBlue" : "FocusOrange");
        CompactLabel.Text = uploading ? $"{_uploads.PendingCount}" : update ? "Update" : FormatTimer(_focus.Remaining);
        CompactPlaybackIcon.Visibility = activity && !uploading && !update && !_focus.IsStarted && CompactBars.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        CompactTimer.Visibility = activity && !uploading && !update && _focus.IsStarted && _media.Current.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
        CompactTimer.Text = FormatTimer(_focus.Remaining);
        CompactUploadProgress.Visibility = activity && (uploading || update && _updates!.IsDownloading) ? Visibility.Visible : Visibility.Collapsed;
        CompactUploadProgress.Value = update ? _updates!.Progress : _uploads.Progress;
        if (wasHidden && activity)
            CompactActivity.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, DurationFor(160)) { BeginTime = TimeSpan.FromMilliseconds(ExpandedContent.Opacity > 0.05 && MotionEnabled ? 110 : 0) });
        CompactIndicators.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var wingWidth = Math.Max(30, Math.Ceiling(CompactIndicators.DesiredSize.Width) + 10);
        CompactLeftWing.Width = CompactRightWing.Width = wingWidth;
        AnimateSize(_settings.Current.NotchWidth + (activity ? 2 * wingWidth : 0), _settings.Current.NotchHeight);
        UpdateVisualizer();
    }

    private void UpdateVisualizer()
    {
        UpdatePlaybackRendering();
        var animate = ((!_expanded && CompactBars.Visibility == Visibility.Visible) || (_expanded && UsesIslandPlayer && _media.Current.IsPlaying))
            && !_showingHud && _deviceConnection is null && !_fullscreenHidden
            && !_settings.Current.ReducedMotion && SystemParameters.ClientAreaAnimation;
        if (animate)
        {
            _spectrum.EnsureStarted(_audio.ReadVisualizerDeviceId());
            if (_visualizerRunning) return;
            _visualizerRunning = true;
            _lastVisualizerFrame = TimeSpan.MinValue;
            _nextSpectrumDeviceCheck = Environment.TickCount64 + 1000;
            _visualizerClock.Restart();
            CompositionTarget.Rendering += OnVisualizerRendering;
        }
        else StopVisualizer();
    }

    private void OnVisualizerRendering(object? sender, EventArgs e)
    {
        // WPF may raise Rendering more than once for the same presentation frame.
        if (e is not RenderingEventArgs frame || frame.RenderingTime == _lastVisualizerFrame) return;
        _lastVisualizerFrame = frame.RenderingTime;
        var elapsed = Math.Clamp(_visualizerClock.Elapsed.TotalSeconds, 0.001, 0.1);
        _visualizerClock.Restart();
        if (Environment.TickCount64 >= _nextSpectrumDeviceCheck)
        {
            _spectrum.EnsureStarted(_audio.ReadVisualizerDeviceId());
            _nextSpectrumDeviceCheck = Environment.TickCount64 + 1000;
        }
        _spectrum.Read(_spectrumBands);
        CompactBars.Update(_spectrumBands, elapsed, false);
        if (UsesIslandPlayer && _expanded) DynamicPlayer.UpdateSpectrum(_spectrumBands, elapsed);
    }

    private void StopVisualizer()
    {
        if (_visualizerRunning) CompositionTarget.Rendering -= OnVisualizerRendering;
        _visualizerRunning = false;
        _visualizerClock.Reset();
        _spectrum.Stop();
        CompactBars.Update(ReadOnlySpan<float>.Empty, 0, true);
        DynamicPlayer?.UpdateSpectrum(ReadOnlySpan<float>.Empty, 0.1);
    }

    private void ShowHud(string title, NotchIconKind icon, double progress, string value)
    {
        if (_expanded || _fullscreenHidden || _deviceConnection is not null || _activeNotification is not null) return;
        _showingHud = true;
        SetMinimalActivity(false);
        CompactActivity.Visibility = Visibility.Collapsed; Hud.Visibility = Visibility.Visible;
        HudTitle.Text = title; HudGlyph.Kind = icon; HudValue.Text = value; HudProgress.Value = progress;
        HudGlyph.BatteryLevel = progress;
        HudGlyph.SoundLevel = progress;
        var volume = icon is NotchIconKind.Speaker or NotchIconKind.MutedSpeaker;
        HudGlyph.Foreground = HudProgress.Foreground = (Brush)FindResource(icon == NotchIconKind.Check ? "FocusOrange" : "Accent");
        HudTitle.Visibility = volume ? Visibility.Collapsed : Visibility.Visible;
        AnimateSize(IsDynamicIsland ? DynamicIslandMetrics.CompactWidth : 300, volume ? 48 : 62);
        UpdateVisualizer();
        _hudTimer.Stop(); _hudTimer.Start();
    }

    private void RefreshMedia()
    {
        var media = _media.Current;
        var identity = (media.Source, media.Title, media.Artist);
        if (_seekIdentity != identity)
        {
            _seekIdentity = identity;
            ++_seekVersion;
            _pendingSeekPosition = null;
            _seekKeyboardTimer.Stop();
            SeekSlider.CancelAdjustment();
            DynamicPlayer.CancelInteraction();
        }
        _audio.SetSource(media.HasSession ? media.Source : "");
        _changingMediaSource = true;
        try { _playerVolume.SetSource(media); }
        finally { _changingMediaSource = false; }
        RefreshAudio(false);
        MusicPlaybackControls.Visibility = media.HasSession ? Visibility.Visible : Visibility.Collapsed;
        MusicEmptyState.Visibility = media.HasSession ? Visibility.Collapsed : Visibility.Visible;
        TrackTitle.Text = media.HasSession ? media.Title : "No music playing";
        TrackArtist.Text = media.HasSession ? media.Artist : "Start a song in your favorite app";
        AlbumArtwork.Source = media.Artwork;
        DynamicPlayer.Refresh(media, updateTimeline: false);
        ArtworkPlaceholder.Visibility = media.Artwork is null ? Visibility.Visible : Visibility.Collapsed;
        PlayGlyph.Kind = media.IsPlaying ? NotchIconKind.Pause : NotchIconKind.Play;
        UpdateArtwork();
        PlayButton.ToolTip = media.IsPlaying ? "Pause" : "Play";
        System.Windows.Automation.AutomationProperties.SetName(PlayButton, media.IsPlaying ? "Pause" : "Play");
        PlayButton.IsEnabled = media.HasSession && media.CanToggle;
        PreviousButton.IsEnabled = media.HasSession && media.CanPrevious;
        NextButton.IsEnabled = media.HasSession && media.CanNext;
        SeekSlider.IsEnabled = media.HasSession && media.CanSeek && media.Duration > TimeSpan.Zero;
        _updatingSeek = true;
        SeekSlider.Maximum = Math.Max(1, media.Duration.TotalSeconds);
        _updatingSeek = false;
        DurationLabel.Text = media.HasSession && media.Duration > TimeSpan.Zero ? FormatMediaTime(media.Duration) : "";
        UpdateTimeline();
        UpdatePlaybackRendering();
    }

    private void UpdatePlaybackRendering()
    {
        var running = _expanded && _selectedTab == "music" && !_fullscreenHidden && _media.Current.IsPlaying
            && !_settings.Current.ReducedMotion && SystemParameters.ClientAreaAnimation;
        if (running == _playbackRendering) return;
        _playbackRendering = running;
        _lastPlaybackFrame = TimeSpan.MinValue;
        if (running) CompositionTarget.Rendering += OnPlaybackRendering;
        else CompositionTarget.Rendering -= OnPlaybackRendering;
    }

    private void OnPlaybackRendering(object? sender, EventArgs args)
    {
        if (args is not RenderingEventArgs frame || frame.RenderingTime == _lastPlaybackFrame) return;
        _lastPlaybackFrame = frame.RenderingTime;
        UpdateTimeline();
    }
    private void UpdateArtwork()
    {
        var changed = !ReferenceEquals(_lastArtwork, _media.Current.Artwork);
        if (changed)
        {
            _lastArtwork = _media.Current.Artwork;
            Resources["ArtworkAccent"] = new SolidColorBrush(SampleArtworkTint(_lastArtwork));
        }
        var playing = _media.Current.IsPlaying;
        if (_artworkPlaying == playing && !changed) return;
        _artworkPlaying = playing;
        var size = !_media.Current.HasSession || playing ? 1 : 0.85;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ArtworkScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(ArtworkScale.ScaleX, size, DurationFor(300)) { EasingFunction = ease });
        ArtworkScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(ArtworkScale.ScaleY, size, DurationFor(300)) { EasingFunction = ease });
        ArtworkGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(ArtworkGlow.Opacity, playing && _lastArtwork is not null ? 0.22 : 0, DurationFor(300)) { EasingFunction = ease });
    }
    private static Color SampleArtworkTint(ImageSource? artwork)
    {
        if (artwork is not BitmapSource bitmap || bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0) return Color.FromRgb(160, 160, 165);
        var thumbnail = new TransformedBitmap(bitmap, new ScaleTransform(12.0 / bitmap.PixelWidth, 12.0 / bitmap.PixelHeight));
        var converted = new FormatConvertedBitmap(thumbnail, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        double red = 0, green = 0, blue = 0, count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] < 192) continue;
            red += pixels[i + 2]; green += pixels[i + 1]; blue += pixels[i]; count++;
        }
        if (count == 0) return Color.FromRgb(160, 160, 165);
        var brightest = Math.Max(red, Math.Max(green, blue)) / count;
        if (brightest < 1) return Color.FromRgb(160, 160, 165);
        var gain = Math.Max(1, 153 / brightest) / count;
        var tint = Color.FromRgb((byte)Math.Clamp(Math.Round(red * gain), 0, 255),
            (byte)Math.Clamp(Math.Round(green * gain), 0, 255), (byte)Math.Clamp(Math.Round(blue * gain), 0, 255));
        // Highly saturated covers (especially blue) can pass an HSV brightness
        // floor while leaving small text illegible. Lift toward white as needed.
        for (var step = 0; RelativeLuminance(tint) < 0.22 && step < 20; step++)
            tint = Color.FromRgb((byte)(tint.R + (255 - tint.R) * 0.1),
                (byte)(tint.G + (255 - tint.G) * 0.1), (byte)(tint.B + (255 - tint.B) * 0.1));
        return tint;
    }
    private static double RelativeLuminance(Color color)
    {
        static double Linear(byte component) => component / 255.0 <= 0.04045
            ? component / 255.0 / 12.92 : Math.Pow((component / 255.0 + 0.055) / 1.055, 2.4);
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }
    private void UpdateTimeline()
    {
        if (_seeking || _seekKeyboardTimer.IsEnabled || UsesIslandPlayer && DynamicPlayer.HasPendingSeek) return;
        var position = _media.Current.HasSession ? _media.EstimatedPosition.TotalSeconds : 0;
        // Some players acknowledge a seek before publishing their new position.
        // Hold the user's chosen time through those stale timeline updates.
        if (_pendingSeekPosition is { } target && DateTime.UtcNow < _seekConfirmationDeadline && Math.Abs(position - target) > 2) return;
        _pendingSeekPosition = null;
        _updatingSeek = true;
        SeekSlider.Value = position;
        _updatingSeek = false;
        UpdateSeekFeedback(SeekSlider.Value);
        DynamicPlayer.UpdateTimeline(SeekSlider.Value, _media.Current.Duration.TotalSeconds, SeekSlider.IsEnabled);
    }
    private void UpdateSeekFeedback(double position)
    {
        var hasTime = _media.Current.HasSession && _media.Current.Duration > TimeSpan.Zero;
        var time = hasTime ? TimeSpan.FromSeconds(position) : TimeSpan.Zero;
        var second = time.Ticks / TimeSpan.TicksPerSecond;
        if (_elapsedFeedbackSecond != second || _elapsedFeedbackHasTime != hasTime)
        {
            _elapsedFeedbackSecond = second;
            _elapsedFeedbackHasTime = hasTime;
            ElapsedLabel.Text = hasTime ? FormatMediaTime(time) : "";
        }
        // Duration and availability can change while elapsed stays in the same
        // second; keep their tooltip feedback independent from the text cache.
        var enabled = SeekSlider.IsEnabled;
        if (_seekTooltipEnabled == enabled && _seekTooltipElapsed == ElapsedLabel.Text && _seekTooltipDuration == DurationLabel.Text) return;
        _seekTooltipEnabled = enabled;
        _seekTooltipElapsed = ElapsedLabel.Text;
        _seekTooltipDuration = DurationLabel.Text;
        SeekSlider.ToolTip = enabled ? $"{ElapsedLabel.Text} / {DurationLabel.Text}" : "Seeking unavailable for this app";
    }
    private async Task CommitSeekAsync(double position)
    {
        if (!SeekSlider.IsEnabled) return;
        var version = ++_seekVersion;
        _pendingSeekPosition = position;
        _seekConfirmationDeadline = DateTime.UtcNow.AddSeconds(1.5);
        var success = await _media.SeekAsync(position);
        if (version != _seekVersion) return;
        if (!success)
        {
            _pendingSeekPosition = null;
            Notification?.Invoke(this, "This media app could not change playback position.");
        }
        UpdateTimeline();
    }
    private void CancelSeekAdjustment()
    {
        ++_seekVersion;
        _seekKeyboardTimer.Stop();
        _pendingSeekPosition = null;
        SeekSlider.CancelAdjustment();
        DynamicPlayer.CancelInteraction();
        _seeking = false;
        UpdateTimeline();
    }
    private static string FormatMediaTime(TimeSpan time) => time.TotalHours >= 1 ? $"{(long)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    private async void OnPlayClick(object sender, RoutedEventArgs e) => await ReportControlAsync(_media.ToggleAsync());
    private async void OnPreviousClick(object sender, RoutedEventArgs e) => await ReportControlAsync(_media.PreviousAsync());
    private async void OnNextClick(object sender, RoutedEventArgs e) => await ReportControlAsync(_media.NextAsync());
    private async Task ReportControlAsync(Task<bool> control) { if (!await control) Notification?.Invoke(this, "This media app could not complete the command."); }
    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ReferenceEquals(e.OriginalSource, VolumeSlider)) return;
        if (!_updatingVolume && IsInitialized && _expanded)
        {
            var volume = Math.Clamp(e.NewValue, 0, 100);
            if (_lastAudio is { Available: true } && _lastAudio.Volume == volume) return;
            if (_playerVolume.SetVolume(volume))
            {
                if (_lastAudio is not null) _lastAudio = _lastAudio with { Volume = volume };
                VolumeGlyph.SoundLevel = volume;
                VolumeSlider.ToolTip = $"Player volume · {volume:0}%";
            }
            else
            {
                RefreshAudio(false);
            }
        }
    }
    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        var current = _playerVolume.Current;
        if (!current.Available || !_playerVolume.SetMuted(!current.Muted)) { RefreshAudio(false); return; }
        RefreshAudio(false);
    }

    private void RefreshShelf()
    {
        ShelfEmpty.Visibility = _shelf.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShelfScroll.Visibility = _shelf.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShelfConnection.Content = !_uploads.Credentials.IsConnected ? "Connect uploads" : _uploads.PendingCount > 0 ? $"{_uploads.PendingCount} uploading" : "Uploads connected";
        ShelfConnection.ToolTip = $"{(_uploads.Credentials.IsConnected ? new Uri(_uploads.Credentials.ServerUrl).Host : "Choose an upload server")} · {_shelf.Items.Count} of 24 files";
    }
    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        _dragActive = true; _collapseTimer.Stop();
        DropHighlight.BeginAnimation(OpacityProperty, new DoubleAnimation(1, DurationFor(140)));
        Expand(false); SetTab("files");
        e.Effects = DragDropEffects.Copy; e.Handled = true;
    }
    private void OnDragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private void OnDragLeave(object sender, DragEventArgs e)
    {
        _dragActive = false;
        DropHighlight.BeginAnimation(OpacityProperty, new DoubleAnimation(0, DurationFor(140)));
        if (!_pinned) _collapseTimer.Start();
    }
    private void OnFilesDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        _dragActive = false;
        DropHighlight.BeginAnimation(OpacityProperty, new DoubleAnimation(0, DurationFor(140)));
        AddAndUpload(paths);
        Expand(false); SetTab("files"); e.Handled = true;
        if (!_pointerInside && !_pinned) _collapseTimer.Start();
    }
    private void OnAddFilesClick(object sender, RoutedEventArgs e)
    {
        _dialogOpen = true;
        try
        {
            var picker = new OpenFileDialog { Multiselect = true, Title = "Add files to your shelf" };
            if (picker.ShowDialog() == true)
            {
                AddAndUpload(picker.FileNames);
            }
        }
        finally { _dialogOpen = false; if (!_pointerInside && !_pinned) _collapseTimer.Start(); }
    }
    private void AddAndUpload(string[] paths)
    {
        var normalized = paths.Select(path => FileShelf.NormalizePath(path)).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var added = _shelf.Add(normalized);
        var items = _shelf.Items.Where(i => normalized.Contains(i.Path, StringComparer.OrdinalIgnoreCase)).ToArray();
        foreach (var item in items) _uploads.Enqueue(item);
        if (added == 0 && items.Length == 0) Notification?.Invoke(this, _shelf.Items.Count >= 24 ? "The shelf is full (24 items). Remove an item to make space." : "Those files are unavailable.");
    }
    private static ShelfItem? TaggedItem(object sender) => (sender as FrameworkElement)?.Tag as ShelfItem;
    private void OnShelfCopy(object sender, RoutedEventArgs e) { if (TaggedItem(sender) is { HasLink: true } item) CopyLink(item.ShareUrl); }
    private void OnShelfCopyDirect(object sender, RoutedEventArgs e) { if (TaggedItem(sender) is { HasDirectLink: true } item) CopyLink(item.FileUrl); }
    private void CopyLink(string link)
    {
        if (!UploadClient.IsShareLink(link)) return;
        try { Clipboard.SetText(link); ShelfConnection.Content = "Link copied"; }
        catch (System.Runtime.InteropServices.COMException) { ShelfConnection.Content = "Clipboard busy. Try again."; }
    }
    private void OnShelfRetry(object sender, RoutedEventArgs e) { if (TaggedItem(sender) is { } item) _uploads.Enqueue(item); }
    private void OnShelfCancel(object sender, RoutedEventArgs e) { if (TaggedItem(sender) is { } item) _uploads.Cancel(item); }
    private void OnShelfOpenLink(object sender, RoutedEventArgs e)
    {
        if (TaggedItem(sender) is not { HasLink: true } item) return;
        try { Process.Start(new ProcessStartInfo(item.ShareUrl) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Log.Error("Open shelf link", ex);
            ShelfConnection.Content = "Couldn't open the link. You can copy it and open it in your browser.";
        }
    }
    private void OnShelfReveal(object sender, RoutedEventArgs e)
    {
        if (TaggedItem(sender) is not { } item) return;
        var start = new ProcessStartInfo("explorer.exe");
        start.ArgumentList.Add("/select,"); start.ArgumentList.Add(item.Path);
        try { Process.Start(start); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Log.Error("Reveal shelf file", ex);
            ShelfConnection.Content = "Couldn't open the file location. Please try again.";
        }
    }
    private void OnShelfOpen(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not ShelfItem item) return;
        try { FileShelf.Open(item); }
        catch (Exception ex) { Log.Error("Open shelf file", ex); Notification?.Invoke(this, "That file is no longer available. You can remove its shelf reference."); }
    }
    private void OnShelfRemove(object sender, RoutedEventArgs e) { if (((Button)sender).Tag is ShelfItem item) _shelf.Remove(item); }
    private void OnShelfDragStart(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(this);
    private void OnShelfDrag(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || ((Button)sender).Tag is not ShelfItem item) return;
        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (!File.Exists(item.Path) && !Directory.Exists(item.Path)) return;
        _dialogOpen = true;
        try { DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(DataFormats.FileDrop, new[] { item.Path }), DragDropEffects.Copy | DragDropEffects.Link); }
        finally { _dialogOpen = false; }
    }


    internal void SelectTabForTest(string tab) => SetTab(tab);
    internal void ShowHudForTest(NotchIconKind icon, int progress = 50) => ShowHud(icon == NotchIconKind.Check ? "Focus complete" : "App volume", icon, progress, $"{progress}%");
    internal void StopPollingForTest() { _pollingSuppressedForTest = true; _pulse.Stop(); _playerPoll.Stop(); _appleDevices?.Dispose(); }
    internal void CloseForShutdown() { _allowClose = true; Close(); }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_viewInitialized) { base.OnClosing(e); return; }
        if (!_allowClose) { e.Cancel = true; Collapse(); }
        base.OnClosing(e);
    }
    protected override void OnClosed(EventArgs e)
    {
        if (!_viewInitialized)
        {
            if (_ownsUploads) _uploads.Dispose();
            if (_ownsPlayerVolume) _playerVolume.Dispose();
            base.OnClosed(e);
            return;
        }
        _closed = true;
        DynamicPlayer?.CancelInteraction();
        IslandMenuPanel?.CancelInteraction();
        StopWatchingOverlayVisibility();
        _notificationTimer.Stop(); _notificationQueue.Clear(); _activeNotification = null;
        if (_updates is not null) _updates.Changed -= OnUpdateChanged;
        ContentRendered -= OnFirstContentRendered;
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        _fullscreenWatcher?.Dispose();
        _appleDevices?.Dispose(); _deviceTimer.Stop(); _deviceQueue.Clear();
        _pulse.Stop(); _playerPoll.Stop(); _collapseTimer.Stop(); _hudTimer.Stop(); _seekKeyboardTimer.Stop(); StopVisualizer();
        CompositionTarget.Rendering -= OnPlaybackRendering;
        _uploads.Changed -= OnUploadsChanged;
        _playerVolume.Changed -= OnPlayerVolumeChanged;
        _playerVolume.Failure -= OnPlayerVolumeFailure;
        if (_ownsPlayerVolume) _playerVolume.Dispose();
        if (_organizer is not null)
        {
            _organizer.Notification -= OnOrganizerNotification;
            _organizer.EditingChanged -= OnOrganizerEditingChanged;
            _organizer.Dispose();
        }
        _reminders?.Dispose();
        if (_controlCenter is not null)
        {
            _controlCenter.FocusRequested -= OnControlCenterFocus;
            _controlCenter.ShortcutRequested -= OnControlCenterShortcut;
            _controlCenter.InteractionEnded -= OnControlCenterInteractionEnded;
            _controlCenter.Dispose();
        }
        if (_ownsUploads) _uploads.Dispose();
        _settings.Changed -= OnSettingsChanged; _media.Changed -= OnMediaChanged;
        _focus.Changed -= OnFocusChanged; _focus.Completed -= OnFocusCompleted;
        _shelf.Items.CollectionChanged -= OnShelfChanged;
        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.UnregisterHotKey(handle, 1); NativeMethods.UnregisterHotKey(handle, 2);
        _source?.RemoveHook(WindowHook);
        base.OnClosed(e);
    }
}
