using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Winisland.Controls;

namespace Winisland;

public partial class NotchWindow
{
    private const double IslandFeatureTopInset = 16;
    private bool? _appliedIslandAppearance;
    private bool _minimalShowing;
    private int _minimalTransitionVersion;
    private string _minimalTab = "focus";

    private bool IsDynamicIsland => _settings.Current.UseDynamicIsland;
    private bool UsesIslandPlayer => IsDynamicIsland && !_islandMenuOpen && _selectedTab == "music";
    private bool UsesCompactIslandPanel => UsesIslandPlayer || _islandMenuOpen;
    private double ClosedNotchHeight => IsDynamicIsland ? DynamicIslandMetrics.Height : _settings.Current.NotchHeight;
    private double AppearanceTopInset => IsDynamicIsland ? DynamicIslandMetrics.TopInset : 0;
    private double ExpandedPanelWidth => UsesCompactIslandPanel ? DynamicIslandMetrics.ExpandedWidth : NotchMetrics.ExpandedWidth;

    private void InitializeDynamicIsland()
    {
        DynamicPlayer.PreviousRequested += async (_, _) => await ReportControlAsync(_media.PreviousAsync());
        DynamicPlayer.PlayPauseRequested += async (_, _) => await ReportControlAsync(_media.ToggleAsync());
        DynamicPlayer.NextRequested += async (_, _) => await ReportControlAsync(_media.NextAsync());
        DynamicPlayer.MenuRequested += (_, _) => OpenIslandMenu();
        DynamicPlayer.SeekPreviewChanged += (_, seconds) =>
        {
            _updatingSeek = true;
            SeekSlider.Value = seconds;
            _updatingSeek = false;
            UpdateSeekFeedback(seconds);
        };
        DynamicPlayer.SeekRequested += async (_, seconds) => await CommitSeekAsync(seconds);
        DynamicPlayer.SeekInteractionChanged += (_, adjusting) =>
        {
            _seeking = adjusting;
            if (adjusting)
            {
                ++_seekVersion;
                _pendingSeekPosition = null;
                _seekKeyboardTimer.Stop();
                _collapseTimer.Stop();
            }
            else
            {
                UpdateTimeline();
                if (!_pointerInside && !_pinned) { _collapseTimer.Stop(); _collapseTimer.Start(); }
            }
        };
        IslandOffset.Changed += (_, _) => UpdateMinimalPosition();
        BuildIslandMenu();
        ApplyNotchAppearance();
    }

    private void ApplyNotchAppearance()
    {
        var changed = _appliedIslandAppearance != IsDynamicIsland;
        if (changed && _appliedIslandAppearance.HasValue) CancelSeekAdjustment();
        _appliedIslandAppearance = IsDynamicIsland;
        Surface.IsDynamicIsland = IsDynamicIsland;
        if (ShadowSurface.Effect is DropShadowEffect shadow)
        {
            shadow.Opacity = IsDynamicIsland ? 0.25 : 0.5;
            shadow.ShadowDepth = IsDynamicIsland ? 2 : 3;
        }
        Island.Margin = new Thickness(0, AppearanceTopInset, 0, 0);
        MinimalActivity.Margin = Island.Margin;
        TopSeam.Visibility = IsDynamicIsland ? Visibility.Collapsed : Visibility.Visible;
        CompactActivity.Margin = IsDynamicIsland ? new Thickness(0) : new Thickness(6, 0, 6, 0);
        var cover = IsDynamicIsland ? DynamicIslandMetrics.CompactArtworkSize : NotchMetrics.CompactArtworkSize;
        var radius = IsDynamicIsland ? DynamicIslandMetrics.CompactArtworkRadius : NotchMetrics.CompactArtworkRadius;
        CompactArtworkFrame.Width = CompactArtworkFrame.Height = cover;
        CompactArtworkFrame.CornerRadius = new CornerRadius(radius);
        CompactArtwork.Clip = new RectangleGeometry(new Rect(0, 0, cover, cover), radius, radius);
        CompactGlyph.Width = CompactGlyph.Height = IsDynamicIsland ? 16 : 14;
        CompactBars.Width = IsDynamicIsland ? 18 : NotchMetrics.CompactSpectrogramWidth;
        CompactBars.Height = IsDynamicIsland ? 16 : NotchMetrics.CompactSpectrogramHeight;
        CompactLabel.FontSize = IsDynamicIsland ? 14 : 11;
        CompactLabel.FontWeight = IsDynamicIsland ? FontWeights.SemiBold : FontWeights.Medium;
        if (changed && _islandMenuOpen) ResetIslandMenu();
        DynamicPlayer.SetMotionEnabled(MotionEnabled);
        if (changed) SetMinimalActivity(false, immediate: true);
        UpdateExpandedLayout();
        UpdateContour();
    }

    private void UpdateExpandedLayout()
    {
        // Settings notifications can request layout before ApplyNotchAppearance.
        if (!IsDynamicIsland && _islandMenuOpen) ResetIslandMenu();
        ExpandedContent.Width = UsesCompactIslandPanel ? DynamicIslandMetrics.ExpandedWidth : 578;
        var inset = IsDynamicIsland && !UsesCompactIslandPanel ? IslandFeatureTopInset : 0;
        ExpandedContent.Margin = new Thickness(0, inset, 0, 0);
        ExpandedContent.Height = UsesCompactIslandPanel ? BaseExpandedPanelHeight
            : BaseExpandedPanelHeight - inset - NotchMetrics.ContentBottomInset;
        ExpandedHeader.Visibility = UsesCompactIslandPanel ? Visibility.Collapsed : Visibility.Visible;
        Height = Math.Max(384, (_expanded ? ExpandedPanelHeight
            : _activeNotification is null ? ClosedNotchHeight : ClosedNotificationHeight)
            + AppearanceTopInset + NotchMetrics.ShadowPadding);
        UpdateCameraSpace();
    }

    private void UpdateDynamicCompact(bool activity, bool music, bool uploading, bool update)
    {
        var wasHidden = CompactActivity.Visibility != Visibility.Visible;
        var primaryMusic = music && !uploading && !update;
        var primaryTimer = !music && !uploading && !update;
        CompactActivity.Visibility = activity ? Visibility.Visible : Visibility.Collapsed;
        Hud.Visibility = Visibility.Collapsed;
        CompactArtwork.Source = primaryMusic ? _media.Current.Artwork : null;
        CompactGlyph.Visibility = CompactArtwork.Source is null ? Visibility.Visible : Visibility.Collapsed;
        CompactGlyph.Kind = uploading ? NotchIconKind.Upload : update ? NotchIconKind.Download
            : primaryTimer ? NotchIconKind.Timer : NotchIconKind.Music;
        CompactGlyph.Foreground = (Brush)FindResource(uploading || update ? "ActivityBlue" : primaryTimer ? "FocusOrange" : "Accent");
        CompactBars.Visibility = activity && primaryMusic && _media.Current.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
        CompactPlaybackIcon.Visibility = activity && primaryMusic && !_media.Current.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
        CompactTimer.Visibility = Visibility.Collapsed;
        CompactLabel.Visibility = activity && !primaryMusic ? Visibility.Visible : Visibility.Collapsed;
        CompactLabel.Foreground = (Brush)FindResource(uploading || update ? "ActivityBlue" : "FocusOrange");
        CompactLabel.Text = uploading ? $"{_uploads.PendingCount}" : update ? "Update" : FormatTimer(_focus.Remaining);
        CompactUploadProgress.Visibility = activity && (uploading || update && _updates!.IsDownloading) ? Visibility.Visible : Visibility.Collapsed;
        CompactUploadProgress.Value = update ? _updates!.Progress : _uploads.Progress;
        CompactIndicators.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var wing = Math.Max(DynamicIslandMetrics.CompactWingWidth, Math.Ceiling(CompactIndicators.DesiredSize.Width) + 16);
        CompactLeftWing.Width = CompactRightWing.Width = wing;
        var secondary = activity && _settings.Current.ShowLiveActivity && (_focus.IsStarted && !primaryTimer || music && !primaryMusic);
        _minimalTab = _focus.IsStarted ? "focus" : "music";
        MinimalGlyph.Kind = _minimalTab == "focus" ? NotchIconKind.Timer : NotchIconKind.Music;
        MinimalGlyph.Foreground = (Brush)FindResource(_minimalTab == "focus" ? "FocusOrange" : "Accent");
        MinimalGlyph.Visibility = _minimalTab == "focus" ? Visibility.Collapsed : Visibility.Visible;
        MinimalTimerLabel.Visibility = _minimalTab == "focus" ? Visibility.Visible : Visibility.Collapsed;
        var remaining = TimeSpan.FromSeconds(Math.Ceiling(_focus.Remaining.TotalSeconds));
        MinimalTimerLabel.Text = remaining.TotalMinutes < 100 ? $"{(int)remaining.TotalMinutes}:{remaining.Seconds:00}"
            : $"{(int)remaining.TotalHours}h{remaining.Minutes:00}";
        var description = _minimalTab == "focus" ? $"Focus timer · {FormatTimer(_focus.Remaining)}" : $"Now playing · {_media.Current.Title}";
        MinimalActivity.ToolTip = description;
        AutomationProperties.SetName(MinimalActivity, description);
        SetMinimalActivity(secondary && !_fullscreenHidden);
        if (wasHidden && activity)
            CompactActivity.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, DurationFor(160))
            { BeginTime = TimeSpan.FromMilliseconds(ExpandedContent.Opacity > 0.05 && MotionEnabled ? 100 : 0) });
        AnimateSize(activity ? DynamicIslandMetrics.CameraWidth + 2 * wing : DynamicIslandMetrics.IdleWidth, DynamicIslandMetrics.Height);
        UpdateVisualizer();
    }

    private void SetMinimalActivity(bool showing, bool immediate = false)
    {
        showing &= IsDynamicIsland && !_expanded && !_showingHud && _activeNotification is null && _deviceConnection is null;
        if (_minimalShowing == showing && !immediate) return;
        _minimalShowing = showing;
        MinimalActivity.IsHitTestVisible = showing;
        var version = ++_minimalTransitionVersion;
        var opacity = MinimalActivity.Visibility == Visibility.Visible ? MinimalActivity.Opacity : 0;
        var offset = IslandOffset.X;
        MinimalActivity.BeginAnimation(OpacityProperty, null);
        IslandOffset.BeginAnimation(TranslateTransform.XProperty, null);
        var target = showing ? -(DynamicIslandMetrics.MinimalSize + DynamicIslandMetrics.MinimalGap) / 2 : 0;
        IslandOffset.X = target;
        MinimalActivity.Opacity = showing ? 1 : 0;
        if (immediate || !MotionEnabled)
        {
            MinimalActivity.Visibility = showing ? Visibility.Visible : Visibility.Collapsed;
            UpdateMinimalPosition();
            return;
        }
        MinimalActivity.Visibility = Visibility.Visible;
        IslandOffset.X = offset;
        MinimalActivity.Opacity = opacity;
        IslandOffset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(offset, target, DurationFor(400))
        {
            EasingFunction = new SpringEase { DampingRatio = 1, ResponseSeconds = 0.32, AnimationSeconds = 0.4 },
            FillBehavior = FillBehavior.Stop
        });
        IslandOffset.X = target;
        var fade = new DoubleAnimation(opacity, showing ? 1 : 0, DurationFor(showing ? 160 : 100));
        fade.Completed += (_, _) =>
        {
            if (version != _minimalTransitionVersion) return;
            MinimalActivity.BeginAnimation(OpacityProperty, null);
            MinimalActivity.Opacity = showing ? 1 : 0;
            if (!showing) MinimalActivity.Visibility = Visibility.Collapsed;
        };
        MinimalActivity.BeginAnimation(OpacityProperty, fade);
    }

    private void UpdateMinimalPosition() => MinimalOffset.X = IslandOffset.X
        + (Island.ActualWidth + DynamicIslandMetrics.MinimalSize) / 2 + DynamicIslandMetrics.MinimalGap;

    private void FinishDynamicAnimations()
    {
        ++_minimalTransitionVersion;
        IslandOffset.BeginAnimation(TranslateTransform.XProperty, null);
        IslandOffset.X = _minimalShowing ? -(DynamicIslandMetrics.MinimalSize + DynamicIslandMetrics.MinimalGap) / 2 : 0;
        MinimalActivity.BeginAnimation(OpacityProperty, null);
        MinimalActivity.Opacity = _minimalShowing ? 1 : 0;
        MinimalActivity.Visibility = _minimalShowing ? Visibility.Visible : Visibility.Collapsed;
        DynamicPlayer.SetMotionEnabled(MotionEnabled);
        UpdateMinimalPosition();
    }

    private void OnMinimalActivityClick(object sender, RoutedEventArgs args)
    {
        SetTab(_minimalTab);
        Expand(true);
        args.Handled = true;
    }

}
