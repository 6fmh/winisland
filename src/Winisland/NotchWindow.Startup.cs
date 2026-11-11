using System;
using System.Windows.Threading;
using Winisland.Controls;
using Winisland.Services;

namespace Winisland;

public partial class NotchWindow
{
    private OrganizerPanel? _organizer;
    private ControlCenterPanel? _controlCenter;
    private OrganizerStore? _organizerStore;
    private ReminderScheduler? _reminders;
    private bool _runtimeStarted, _closed, _pollingSuppressedForTest;
    internal bool DeferredPanelsCreatedForTest => _organizer is not null || _controlCenter is not null;
    private OrganizerPanel Organizer
    {
        get
        {
            if (_organizer is not null) return _organizer;
            EnsureReminders();
            _organizer = new OrganizerPanel(_organizerStore!, _reminders) { Name = "Organizer" };
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
        // Load only reminder data while idle; both secondary panels stay lazy.
        Dispatcher.BeginInvoke(() => { if (!_closed) EnsureReminders(); }, DispatcherPriority.ApplicationIdle);
    }
    private void EnsureReminders()
    {
        if (_reminders is not null) return;
        _organizerStore = new OrganizerStore();
        _reminders = new ReminderScheduler(Dispatcher, _organizerStore);
        _reminders.Notification += OnOrganizerNotification;
    }
    internal void InitializeDeferredPanelsForTest() { _ = Organizer; _ = ControlCenter; }
}
