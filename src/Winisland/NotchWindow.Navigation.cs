using System;
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
    private int _tabTransitionVersion;
    private double ExpandedPanelHeight => _selectedTab switch
    {
        "controls" => NotchMetrics.ControlCenterExpandedHeight,
        "calendar" or "tools" => NotchMetrics.OrganizerExpandedHeight,
        _ => NotchMetrics.ExpandedHeight
    };
    private Control SelectedTabControl => _selectedTab switch
    {
        "files" => FilesTab, "focus" => FocusTab, "calendar" => CalendarTab, "tools" => ToolsTab,
        "controls" => ControlCenterTab, "updates" => UpdateTab, _ => MusicTab
    };

    private FrameworkElement PanelFor(string tab) => tab switch
    {
        "files" => FilesPanel, "focus" => FocusPanel, "calendar" or "tools" => OrganizerHost,
        "controls" => ControlCenterHost, "updates" => UpdatePanel, _ => MusicPanel
    };

    private void SetTab(string tab)
    {
        if (_changingTab || MusicPanel is null || OrganizerHost is null) return;
        if (tab is not ("music" or "files" or "focus" or "calendar" or "tools" or "controls" or "updates")) tab = "music";
        _changingTab = true;
        try
        {
            var changed = _selectedTab != tab;
            _selectedTab = tab;
            if (tab != "music")
            {
                CancelSeekAdjustment();
                VolumeSlider.CancelAdjustment();
            }
            UpdatePlaybackRendering();
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

            ExpandedContent.Height = ExpandedPanelHeight - NotchMetrics.ContentBottomInset;
            if (_expanded) AnimateSize(NotchMetrics.ExpandedWidth, ExpandedPanelHeight);
            if (!changed || !_expanded || _settings.Current.ReducedMotion || !SystemParameters.ClientAreaAnimation)
            {
                FinishTabTransitions();
                return;
            }
            var incoming = PanelFor(tab);
            var version = ++_tabTransitionVersion;
            foreach (var panel in new FrameworkElement[] { MusicPanel, FilesPanel, FocusPanel, OrganizerHost, ControlCenterHost, UpdatePanel })
            {
                if (ReferenceEquals(panel, incoming))
                {
                    var wasVisible = panel.Visibility == Visibility.Visible;
                    var from = wasVisible ? panel.Opacity : 0;
                    panel.Visibility = Visibility.Visible;
                    panel.IsHitTestVisible = true;
                    panel.Opacity = 1;
                    panel.BeginAnimation(OpacityProperty, new DoubleAnimation(from, 1, DurationFor(180))
                    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
                    if (panel.RenderTransform is not TranslateTransform) panel.RenderTransform = new TranslateTransform();
                    var offset = (TranslateTransform)panel.RenderTransform;
                    var fromOffset = wasVisible ? offset.Y : 5;
                    offset.Y = 0;
                    offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromOffset, 0, DurationFor(180))
                    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
                }
                else if (panel.Visibility == Visibility.Visible)
                {
                    panel.IsHitTestVisible = false;
                    var exit = new DoubleAnimation(panel.Opacity, 0, DurationFor(90)) { FillBehavior = FillBehavior.Stop };
                    exit.Completed += (_, _) =>
                    {
                        if (version != _tabTransitionVersion || ReferenceEquals(panel, PanelFor(_selectedTab))) return;
                        panel.Visibility = Visibility.Collapsed;
                        panel.BeginAnimation(OpacityProperty, null);
                        panel.Opacity = 1;
                    };
                    panel.BeginAnimation(OpacityProperty, exit);
                    panel.Opacity = 0;
                }
            }
        }
        finally { _changingTab = false; }
    }

    private void FinishTabTransitions()
    {
        ++_tabTransitionVersion;
        var selected = PanelFor(_selectedTab);
        foreach (var panel in new FrameworkElement[] { MusicPanel, FilesPanel, FocusPanel, OrganizerHost, ControlCenterHost, UpdatePanel })
        {
            panel.BeginAnimation(OpacityProperty, null);
            panel.Opacity = 1;
            panel.Visibility = ReferenceEquals(panel, selected) ? Visibility.Visible : Visibility.Collapsed;
            panel.IsHitTestVisible = ReferenceEquals(panel, selected);
            if (panel.RenderTransform is TranslateTransform offset)
            { offset.BeginAnimation(TranslateTransform.YProperty, null); offset.Y = 0; }
        }
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
        if (e.Key == Key.Escape) { Collapse(); e.Handled = true; return; }
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
