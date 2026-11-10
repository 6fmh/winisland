using System;
using System.Windows.Threading;
using Winisland.Controls;

namespace Winisland;

public partial class NotchWindow
{
    private OrganizerPanel? _organizer;
    private ControlCenterPanel? _controlCenter;
    private bool _runtimeStarted, _closed, _pollingSuppressedForTest;
    internal bool DeferredPanelsCreatedForTest => _organizer is not null || _controlCenter is not null;
    private OrganizerPanel Organizer
    {
        get
        {
            if (_organizer is not null) return _organizer;
            _organizer = new OrganizerPanel { Name = "Organizer" };
            _organizer.Notification += OnOrganizerNotification;
            _organizer.EditingChanged += OnOrganizerEditingChanged;
            OrganizerHost.Content = _organizer; RegisterName("Organizer", _organizer);
            return _organizer;
        }
    }
    private ControlCenterPanel ControlCenter
    {
        get
        {
            if (_controlCenter is not null) return _controlCenter;
            _controlCenter = new ControlCenterPanel { Name = "ControlCenter" };
            _controlCenter.FocusRequested += OnControlCenterFocus;
            _controlCenter.ShortcutRequested += OnControlCenterShortcut;
            _controlCenter.InteractionEnded += OnControlCenterInteractionEnded;
            ControlCenterHost.Content = _controlCenter; RegisterName("ControlCenter", _controlCenter);
            return _controlCenter;
        }
    }
    private void OnFirstContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnFirstContentRendered;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed || _runtimeStarted || _pollingSuppressedForTest) return;
            _runtimeStarted = true;
            _playerVolume.Start(); StartAppleDevices(); _pulse.Start(); _playerPoll.Start();
        }, DispatcherPriority.Background);
        // Reminders must run even if Tools is never opened. Build their panel
        // after first paint; Control Center is created only when selected.
        Dispatcher.BeginInvoke(() => { if (!_closed) _ = Organizer; }, DispatcherPriority.ApplicationIdle);
    }
    internal void InitializeDeferredPanelsForTest() { _ = Organizer; _ = ControlCenter; }
}
