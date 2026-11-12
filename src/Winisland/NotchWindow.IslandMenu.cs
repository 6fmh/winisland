using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Winisland;

public partial class NotchWindow
{
    private bool _islandMenuOpen;
    private int _islandMenuTransitionVersion;

    private void BuildIslandMenu()
    {
        IslandMenuPanel.ActivityRequested += (_, activity) =>
        {
            if (activity == "settings") { CloseIslandMenu(); SettingsRequested?.Invoke(this, EventArgs.Empty); }
            else if (activity == "updates") { ResetIslandMenu(); OpenUpdates(); }
            else { SetTab(activity); Expand(_pinned); SelectedTabControl.Focus(); }
        };
        IslandMenuPanel.BackRequested += (_, _) => CloseIslandMenu();
        IslandMenuPanel.SetupRequested += (_, _) => { CloseIslandMenu(); PlayerSettingsRequested?.Invoke(this, EventArgs.Empty); };
        IslandMenuPanel.MuteRequested += (_, _) => { OnMuteClick(this, new RoutedEventArgs()); RefreshIslandMenu(); };
        IslandMenuPanel.VolumeRequested += (_, value) => { _playerVolume.SetVolume(value); RefreshAudio(false); };
        IslandMenuPanel.PinRequested += (_, _) =>
        {
            OnPinClick(this, new RoutedEventArgs());
            RefreshIslandMenu();
            if (_pinned) ((Button)IslandMenuPanel.FindName("PinButton")).Focus();
        };
        Island.ContextMenuOpening += (_, args) =>
        {
            if (!IsDynamicIsland || !CanOpenIslandMenu(args.OriginalSource as DependencyObject)) return;
            args.Handled = true;
            OpenIslandMenu();
        };
        // With no ContextMenu WPF does not always raise ContextMenuOpening.
        Island.MouseRightButtonUp += (_, args) =>
        {
            if (!IsDynamicIsland || !CanOpenIslandMenu(args.OriginalSource as DependencyObject)) return;
            args.Handled = true;
            OpenIslandMenu();
        };
        IslandMenuPanel.AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler((_, _) =>
        {
            if (!_pointerInside && !_pinned) { _collapseTimer.Stop(); _collapseTimer.Start(); }
        }), true);
    }

    private bool CanOpenIslandMenu(DependencyObject? origin)
    {
        for (var node = origin; node is not null && !ReferenceEquals(node, Island);)
        {
            if (node is TextBoxBase || node is FrameworkElement { ContextMenu: not null }) return false;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return true;
    }

    private void RefreshIslandMenu()
    {
        var audio = _playerVolume.Current;
        IslandMenuPanel.SetActivity(_selectedTab, _updates is not null, _pinned);
        var step = audio.TargetId.StartsWith("spotify-api:", StringComparison.Ordinal) ? 1 : Math.Max(2, audio.Step);
        IslandMenuPanel.SetVolume(audio.Available, audio.Volume, audio.Muted, step,
            audio.Available ? $"{audio.Player} volume · {audio.Volume:0}%" : audio.ConnectionHint);
    }

    private void OpenIslandMenu()
    {
        if (!IsDynamicIsland || _islandMenuOpen) return;
        CancelSeekAdjustment();
        VolumeSlider.CancelAdjustment();
        _controlCenter?.SetActive(false);
        _controlCenter?.CancelAdjustment();
        _islandMenuOpen = true;
        RefreshIslandMenu();
        Expand(_pinned);
        UpdateExpandedLayout();
        UpdateDevicePresentation();
        if (_activeNotification is not null) UpdateNotificationPresentation();
        AnimateSize(ExpandedPanelWidth, ExpandedPanelHeight);
        PresentIslandMenu();
        IslandMenuPanel.FocusActivity(_selectedTab);
    }

    private void CloseIslandMenu()
    {
        if (!_islandMenuOpen) return;
        ResetIslandMenu();
        UpdateExpandedLayout();
        UpdateDevicePresentation();
        if (_activeNotification is not null) UpdateNotificationPresentation();
        AnimateSize(ExpandedPanelWidth, ExpandedPanelHeight);
        PresentIslandMenu();
        if (_selectedTab == "controls") ControlCenter.SetActive(_expanded);
        SelectedTabControl.Focus();
    }

    private void ResetIslandMenu()
    {
        if (!_islandMenuOpen) return;
        _islandMenuOpen = false;
        ++_islandMenuTransitionVersion;
        IslandMenuPanel.CancelInteraction();
    }

    private void PresentIslandMenu()
    {
        var version = ++_islandMenuTransitionVersion;
        if (!MotionEnabled) { FinishTabTransitions(); return; }
        var selected = PanelFor(_selectedTab);
        foreach (var panel in new FrameworkElement[] { MusicPanel, DynamicPlayer, IslandMenuPanel, FilesPanel, FocusPanel, OrganizerHost, ControlCenterHost, UpdatePanel })
        {
            var opacity = panel.IsVisible ? panel.Opacity : 0;
            panel.BeginAnimation(OpacityProperty, null);
            panel.IsHitTestVisible = ReferenceEquals(panel, selected);
            if (ReferenceEquals(panel, selected))
            {
                panel.Visibility = Visibility.Visible;
                panel.Opacity = 1;
                panel.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, 1, DurationFor(180))
                { BeginTime = TimeSpan.FromMilliseconds(MotionEnabled ? 45 : 0), FillBehavior = FillBehavior.Stop });
            }
            else if (panel.Visibility == Visibility.Visible)
            {
                panel.Opacity = 0;
                var fade = new DoubleAnimation(opacity, 0, DurationFor(90)) { FillBehavior = FillBehavior.Stop };
                fade.Completed += (_, _) =>
                {
                    if (version != _islandMenuTransitionVersion || ReferenceEquals(panel, PanelFor(_selectedTab))) return;
                    panel.Visibility = Visibility.Collapsed;
                    panel.BeginAnimation(OpacityProperty, null);
                    panel.Opacity = 1;
                };
                panel.BeginAnimation(OpacityProperty, fade);
                if (!MotionEnabled) panel.Visibility = Visibility.Collapsed;
            }
        }
    }
}
