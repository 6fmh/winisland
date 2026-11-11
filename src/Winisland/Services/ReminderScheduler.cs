using System;
using System.IO;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Winisland.Services;

// Reminder delivery must not depend on constructing or displaying organizer UI.
internal sealed class ReminderScheduler : IDisposable
{
    private readonly OrganizerStore _store;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private bool _disposed;
    internal bool IsScheduledForTest => _timer.IsEnabled;
    internal event EventHandler<string>? Notification;

    internal ReminderScheduler(Dispatcher dispatcher, OrganizerStore store)
    {
        _dispatcher = dispatcher;
        _store = store;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _timer.Tick += OnDue;
        _store.Changed += OnStoreChanged;
        SystemEvents.TimeChanged += OnTimeChanged;
        Schedule();
    }

    private void OnStoreChanged(object? sender, OrganizerChangedEventArgs e)
    {
        if (e.Change.HasFlag(OrganizerChange.Reminders)) Schedule();
    }

    private void Schedule()
    {
        _timer.Stop();
        if (_disposed || _store.NextReminderDue is not { } due) return;
        var delay = due - DateTimeOffset.Now;
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds, 250, TimeSpan.FromHours(1).TotalMilliseconds));
        _timer.Start();
    }

    private void OnDue(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (_disposed) return;
        try
        {
            foreach (var item in _store.TakeDueReminders(DateTimeOffset.Now))
                Notification?.Invoke(this, "Reminder: " + item.Title);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Log.Error("Acknowledge organizer reminder", ex); }
        Schedule();
    }

    private void OnTimeChanged(object? sender, EventArgs e)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() => { if (!_disposed) Schedule(); });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnDue;
        _store.Changed -= OnStoreChanged;
        SystemEvents.TimeChanged -= OnTimeChanged;
        Notification = null;
    }
}
