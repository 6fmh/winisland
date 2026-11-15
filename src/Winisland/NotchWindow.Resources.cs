using System;
using System.Windows;
using Winisland.Services;

namespace Winisland;

public partial class NotchWindow
{
    private PointerProximityWatcher? _pointerWatcher;
    private Rect _prewarmBounds = Rect.Empty;
    private bool _pointerNear;
    internal bool PlayerPollingForTest => _playerPoll.IsEnabled;
    internal bool VisualizerRunningForTest => _visualizerRunning;
    internal bool PointerWatcherRegisteredForTest => _pointerWatcher?.Registered == true;

    private void UpdateResourceDemand()
    {
        if (!_viewInitialized) return;
        var visible = !_closed && IsVisible && WindowState != WindowState.Minimized && !_fullscreenHidden;
        var needsPlayer = visible && (_expanded ? _selectedTab == "music" || _islandMenuOpen : _pointerNear || _pointerInside);
        var interval = TimeSpan.FromMilliseconds(visible && _expanded ? 500 : 2000);
        if (_pulse.Interval != interval) _pulse.Interval = interval;
        _playerVolume.SetPollingEnabled(needsPlayer);
        if (needsPlayer && _runtimeStarted && !_pollingSuppressedForTest)
        {
            if (!_playerPoll.IsEnabled) _playerPoll.Start();
        }
        else _playerPoll.Stop();
    }

    private void UpdatePrewarmBounds()
    {
        if (_source is null || !IsVisible || Island.ActualWidth <= 0) { _prewarmBounds = Rect.Empty; return; }
        var origin = Island.PointToScreen(new Point());
        var scale = _source.CompositionTarget?.TransformToDevice ?? System.Windows.Media.Matrix.Identity;
        _prewarmBounds = new Rect(origin.X - 64 * scale.M11, origin.Y - 48 * scale.M22,
            (Island.ActualWidth + 128) * scale.M11, (Island.ActualHeight + 96) * scale.M22);
    }

    private void OnPointerMoved(Point screenPoint)
    {
        var near = !_prewarmBounds.IsEmpty && _prewarmBounds.Contains(screenPoint);
        if (_pointerNear == near) return;
        _pointerNear = near;
        UpdateResourceDemand();
    }

    internal void MovePointerForTest(Point screenPoint)
    {
        BackgroundVerification.RequirePrivateDesktop();
        UpdatePrewarmBounds();
        OnPointerMoved(screenPoint);
    }

    private void OnResourceVisibilityChanged(object? sender, EventArgs args)
    {
        if (!_viewInitialized) return;
        if (!IsVisible || WindowState == WindowState.Minimized) _pointerNear = _pointerInside = false;
        UpdatePrewarmBounds();
        UpdateVisualizer();
    }
}
