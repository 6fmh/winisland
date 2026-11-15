using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Winisland.Controls;
using Winisland.Services;

namespace Winisland;

public partial class NotchWindow
{
    private bool _changingTab;
    private readonly Dictionary<FrameworkElement, PanelMotion> _panelMotions = new();
    private sealed record PanelMotion(AnimationClock OpacityClock, AnimationClock OffsetClock,
        SpringEase OffsetEase, double OffsetDistance, bool WasEnabled);
    private double ExpandedPanelHeight => BaseExpandedPanelHeight + (_activeNotification is not null ? NotificationFooterHeight
        : UsesIslandPlayer && _deviceConnection is not null ? AppleConnectionContentHeight : 0);
    private double BaseExpandedPanelHeight => _islandMenuOpen ? DynamicIslandMetrics.MenuHeight : UsesIslandPlayer ? DynamicIslandMetrics.ExpandedHeight : (_selectedTab switch
    {
        "controls" => NotchMetrics.ControlCenterExpandedHeight,
        "calendar" or "tools" => NotchMetrics.OrganizerExpandedHeight,
        "updates" => _updatePanelHeight,
        _ => NotchMetrics.ExpandedHeight
    }) + (IsDynamicIsland ? IslandFeatureTopInset : 0);
    private Control SelectedTabControl => _islandMenuOpen ? (Control)IslandMenuPanel.FindName("BackButton") : UsesIslandPlayer ? (Control)DynamicPlayer.FindName("MenuButton") : _selectedTab switch
    {
        "files" => FilesTab, "focus" => FocusTab, "calendar" => CalendarTab, "tools" => ToolsTab,
        "controls" => ControlCenterTab, "updates" => UpdateTab, _ => MusicTab
    };

    private FrameworkElement PanelFor(string tab) => _islandMenuOpen ? IslandMenuPanel : tab == "music" && IsDynamicIsland ? DynamicPlayer : tab switch
    {
        "files" => FilesPanel, "focus" => FocusPanel, "calendar" or "tools" => OrganizerHost,
        "controls" => ControlCenterHost, "updates" => UpdatePanel, _ => MusicPanel
    };

    private void SetTab(string tab)
    {
        if (_changingTab || MusicPanel is null || OrganizerHost is null) return;
        if (tab is not ("music" or "files" or "focus" or "calendar" or "tools" or "controls" or "updates")) tab = "music";
        var menuWasOpen = _islandMenuOpen;
        if (_islandMenuOpen) ResetIslandMenu();
        _changingTab = true;
        try
        {
            var changed = menuWasOpen || _selectedTab != tab;
            _selectedTab = tab;
            if (tab != "music")
            {
                CancelSeekAdjustment();
                VolumeSlider.CancelAdjustment();
            }
            UpdateVisualizer();
            MusicTab.IsChecked = tab == "music";
            FilesTab.IsChecked = tab == "files";
            FocusTab.IsChecked = tab == "focus";
            CalendarTab.IsChecked = tab == "calendar";
            ToolsTab.IsChecked = tab == "tools";
            ControlCenterTab.IsChecked = tab == "controls";
            UpdateTab.IsChecked = tab == "updates";
            if (tab == "controls") ControlCenter.SetActive(_expanded);
            else { _controlCenter?.SetActive(false); _controlCenter?.CancelAdjustment(); }
            if (tab == "calendar") Organizer.ShowCalendar();
            else if (tab == "tools") Organizer.ShowTools();

            UpdateExpandedLayout();
            if (_expanded) AnimateSize(ExpandedPanelWidth, ExpandedPanelHeight);
            if (_activeNotification is not null) UpdateNotificationPresentation();
            if (!changed || !_expanded || _settings.Current.ReducedMotion || !SystemParameters.ClientAreaAnimation)
            {
                FinishTabTransitions();
                return;
            }
            PresentActivityPanels();
        }
        finally { _changingTab = false; }
    }

    private FrameworkElement[] ActivityPanels => new FrameworkElement[]
        { MusicPanel, DynamicPlayer, IslandMenuPanel, FilesPanel, FocusPanel, OrganizerHost, ControlCenterHost, UpdatePanel };

    private void HandoffPanelFocus(FrameworkElement selected)
    {
        // Only move focus that belongs to a departing view. Hover and background
        // state changes must never take focus from header/footer actions or apps.
        if (!_expanded) return;
        foreach (var panel in ActivityPanels)
            if (!ReferenceEquals(panel, selected) && panel.IsKeyboardFocusWithin)
            { SelectedTabControl.Focus(); break; }
    }

    private void PresentActivityPanels()
    {
        if (!MotionEnabled || !_expanded) { FinishTabTransitions(); return; }
        var selected = PanelFor(_selectedTab);
        AnimateActivityPanel(selected, true);
        HandoffPanelFocus(selected);
        foreach (var panel in ActivityPanels)
        {
            if (ReferenceEquals(panel, selected) || panel.Visibility != Visibility.Visible) continue;
            AnimateActivityPanel(panel, false);
        }
    }

    private void AnimateActivityPanel(FrameworkElement panel, bool entering)
    {
        var wasVisible = panel.Visibility == Visibility.Visible;
        var fromOpacity = wasVisible ? panel.Opacity : 0;
        if (panel.RenderTransform is not TranslateTransform) panel.RenderTransform = new TranslateTransform();
        var offset = (TranslateTransform)panel.RenderTransform;
        var fromOffset = wasVisible ? offset.Y : 4;
        var wasEnabled = panel.IsEnabled;
        var velocity = 0.0;
        if (_panelMotions.TryGetValue(panel, out var previous))
        {
            wasEnabled = previous.WasEnabled;
            var progress = (previous.OffsetClock.CurrentTime?.TotalSeconds ?? 0) / previous.OffsetEase.AnimationSeconds;
            velocity = previous.OffsetDistance * previous.OffsetEase.VelocityAt(progress);
        }
        panel.BeginAnimation(OpacityProperty, null);
        offset.BeginAnimation(TranslateTransform.YProperty, null);
        // Keep the presented values as the base until the clock's first tick.
        // A reversal in this dispatcher turn then cannot reveal a future target.
        panel.Opacity = fromOpacity;
        offset.Y = fromOffset;
        panel.Visibility = Visibility.Visible;
        panel.IsHitTestVisible = entering;
        panel.IsEnabled = entering && wasEnabled;
        var targetOpacity = entering ? 1 : 0;
        var targetOffset = entering ? 0 : -3;
        var seconds = entering ? 0.18 : 0.09;
        var distance = targetOffset - fromOffset;
        var ease = new SpringEase
        {
            DampingRatio = 1, ResponseSeconds = seconds * 0.9, AnimationSeconds = seconds,
            InitialVelocity = Math.Abs(distance) > 0.000001 ? velocity / distance : 0
        };
        var fadeClock = new DoubleAnimation(fromOpacity, targetOpacity, TimeSpan.FromSeconds(seconds))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }.CreateClock();
        var offsetClock = new DoubleAnimation(fromOffset, targetOffset, TimeSpan.FromSeconds(seconds))
            { EasingFunction = ease }.CreateClock();
        var motion = new PanelMotion(fadeClock, offsetClock, ease, distance, wasEnabled);
        _panelMotions[panel] = motion;
        fadeClock.Completed += (_, _) =>
        {
            if (!_panelMotions.TryGetValue(panel, out var current) || !ReferenceEquals(current, motion)) return;
            _panelMotions.Remove(panel);
            panel.BeginAnimation(OpacityProperty, null);
            offset.BeginAnimation(TranslateTransform.YProperty, null);
            panel.Opacity = 1;
            offset.Y = 0;
            panel.Visibility = entering ? Visibility.Visible : Visibility.Collapsed;
            panel.IsEnabled = wasEnabled;
        };
        panel.ApplyAnimationClock(OpacityProperty, fadeClock);
        offset.ApplyAnimationClock(TranslateTransform.YProperty, offsetClock);
    }

    private void FinishTabTransitions()
    {
        var selected = PanelFor(_selectedTab);
        if (_panelMotions.TryGetValue(selected, out var previous)) selected.IsEnabled = previous.WasEnabled;
        selected.Visibility = Visibility.Visible;
        HandoffPanelFocus(selected);
        foreach (var panel in ActivityPanels)
        {
            if (_panelMotions.TryGetValue(panel, out var motion)) panel.IsEnabled = motion.WasEnabled;
            panel.BeginAnimation(OpacityProperty, null);
            panel.Opacity = 1;
            panel.Visibility = ReferenceEquals(panel, selected) ? Visibility.Visible : Visibility.Collapsed;
            panel.IsHitTestVisible = ReferenceEquals(panel, selected);
            if (panel.RenderTransform is TranslateTransform offset)
            { offset.BeginAnimation(TranslateTransform.YProperty, null); offset.Y = 0; }
        }
        _panelMotions.Clear();
    }

    private void OnMusicTab(object sender, RoutedEventArgs e) => SetTab("music");
    private void OnFilesTab(object sender, RoutedEventArgs e) => SetTab("files");
    private void OnFocusTab(object sender, RoutedEventArgs e) => SetTab("focus");
    private void OnCalendarTab(object sender, RoutedEventArgs e) => SetTab("calendar");
    private void OnToolsTab(object sender, RoutedEventArgs e) => SetTab("tools");
    private void OnControlCenterTab(object sender, RoutedEventArgs e) => SetTab("controls");
    private void OnControlCenterFocus(object? sender, EventArgs e) => SetTab("focus");
    private void OnControlCenterShortcut(object? sender, string uri) => Collapse();
    private void OnControlCenterInteractionEnded(object? sender, EventArgs e)
    {
        if (!_pointerInside && !_pinned) { _collapseTimer.Stop(); _collapseTimer.Start(); }
    }
    private void OnOrganizerNotification(object? sender, string message) => Notification?.Invoke(this, message);
    private void OnOrganizerEditingChanged(object? sender, EventArgs args)
    {
        if (Organizer.IsEditing) EnsureKeyboardInput();
    }

    internal void EnsureKeyboardInput()
    {
        if (!_expanded) { Expand(true); return; }
        if (!_pinned)
        {
            _previousForeground = NativeMethods.GetForegroundWindow();
            _pinned = true;
            PinGlyph.Foreground = (Brush)FindResource("Accent");
            NativeMethods.SetActivatable(new WindowInteropHelper(this).Handle, true);
        }
        _collapseTimer.Stop();
        if (!IsActive) Activate();
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null;)
        {
            if (node is TextBoxBase or ComboBox) { EnsureKeyboardInput(); break; }
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        base.OnPreviewMouseLeftButtonDown(e);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (e.Key == Key.Escape) { if (_islandMenuOpen) CloseIslandMenu(); else Collapse(); e.Handled = true; return; }
        if (IsDynamicIsland && (e.Key == Key.Apps || e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift)
            && CanOpenIslandMenu(e.OriginalSource as DependencyObject))
        { OpenIslandMenu(); e.Handled = true; return; }
        if (!_pinned || Keyboard.Modifiers != ModifierKeys.Control) return;
        var tab = e.Key switch
        {
            Key.D1 or Key.NumPad1 => "music", Key.D2 or Key.NumPad2 => "files", Key.D3 or Key.NumPad3 => "focus",
            Key.D4 or Key.NumPad4 => "calendar", Key.D5 or Key.NumPad5 => "tools",
            Key.D6 or Key.NumPad6 => "controls", Key.D7 or Key.NumPad7 when _updates is not null => "updates", _ => null
        };
        if (tab is null) return;
        if (tab == "updates") { _showUpdateTab = true; RefreshUpdates(); UpdateCameraSpace(); }
        SetTab(tab);
        SelectedTabControl.Focus();
        e.Handled = true;
    }

    private void OnPlayerVolumeChanged(object? sender, EventArgs args) => RefreshAudio(!_changingMediaSource);
    private void OnPlayerVolumeFailure(object? sender, string message)
    {
        RefreshAudio(false);
        if (Environment.TickCount64 - _lastPlayerFailure < 5000) return;
        _lastPlayerFailure = Environment.TickCount64;
        Notification?.Invoke(this, message);
    }
}
