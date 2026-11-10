using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Winisland.Controls;
using Winisland.Services;

namespace Winisland;

internal static class OrganizerVerification
{
    internal static async Task RunAsync(string output, Action<bool, string> check)
    {
        var directory = Path.Combine(output, "organizer-data-" + Guid.NewGuid().ToString("N"));
        var store = new OrganizerStore(directory);
        var leap = OrganizerCalendar.GetDays(new DateOnly(2024, 2, 1), DayOfWeek.Monday);
        check(leap.Count == 42 && leap.Count(d => d == new DateOnly(2024, 2, 29)) == 1,
            "Calendar includes leap day once in a complete six-week month");
        check(OrganizerCalendar.TryShiftMonth(new DateOnly(2024, 12, 1), 1, out var january)
            && january == new DateOnly(2025, 1, 1), "Calendar month navigation crosses a year boundary");
        check(!OrganizerCalendar.TryShiftMonth(new DateOnly(1, 1, 1), -1, out _)
            && OrganizerCalendar.GetDays(new DateOnly(1, 1, 1), DayOfWeek.Sunday).Count == 42,
            "Calendar safely handles the earliest representable month");

        var start = new DateTimeOffset(2027, 3, 12, 14, 30, 0, TimeSpan.Zero);
        var entry = store.AddEvent("Design review", start, 45, description: "First line\nSecond, line; \\ notes — 雪");
        var restarted = new OrganizerStore(directory);
        check(restarted.Events.Single().Id == entry.Id && restarted.Events.Single().Description == entry.Description,
            "Calendar events and their descriptions survive a restart");
        store.UpdateEvent(entry.Id, "Updated review", start.AddHours(1), 60, false, entry.Description);
        check(store.Events.Single().Title == "Updated review" && store.Events.Single().DurationMinutes == 60,
            "Editing an agenda event updates its existing identity");

        var interchange = CalendarInterchange.Export(store.Events);
        var parsed = CalendarInterchange.Parse(interchange);
        check(parsed.Entries.Single().Title == "Updated review" && parsed.Entries.Single().Start == start.AddHours(1)
            && parsed.Entries.Single().Description == entry.Description,
            "Calendar interchange preserves times, Unicode text and escaped descriptions");
        var imports = new OrganizerStore(Path.Combine(directory, "imported"));
        imports.ImportEvents(parsed.Entries); imports.ImportEvents(parsed.Entries);
        check(imports.Events.Count == 1, "Importing the same calendar twice does not duplicate events");
        var recurring = CalendarInterchange.Parse(interchange.Replace("END:VEVENT", "RRULE:FREQ=DAILY\r\nEND:VEVENT"));
        check(recurring.Entries.Count == 0 && recurring.Skipped == 1,
            "Unsupported recurring calendar entries are reported rather than flattened silently");
        var longNote = new string('雪', 130) + "\n" + new string('x', 160);
        var longCalendar = CalendarInterchange.Export(new[] { entry with { Description = longNote } });
        check(longCalendar.Split("\r\n").All(line => System.Text.Encoding.UTF8.GetByteCount(line) <= 75)
            && CalendarInterchange.Parse(longCalendar).Entries.Single().Description == longNote,
            "Calendar exports fold Unicode lines by bytes and imports restore the exact text");
        var allDay = CalendarInterchange.Parse("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:day-fixture\r\nDTSTART;VALUE=DATE:20270312\r\nDTEND;VALUE=DATE:20270313\r\nSUMMARY:A day off\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        check(allDay.Entries.Single().AllDay && allDay.Entries.Single().DurationMinutes == 1440,
            "Calendar imports all-day events as dates rather than shifted UTC times");
        var week = CalendarInterchange.Parse("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nDTSTART;VALUE=DATE:20270312\r\nDURATION:P1W\r\nSUMMARY:A full week\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        check(week.Entries.Single().DurationMinutes == 10080, "Calendar week durations preserve seven full all-day dates");
        var defaultDay = CalendarInterchange.Parse("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nDTSTART;VALUE=DATE:20271031\r\nSUMMARY:Clock-change day\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        check(defaultDay.Entries.Single().DurationMinutes == 1440,
            "An all-day entry without an end lasts one calendar date across daylight-saving changes");
        foreach (var duration in new[] { "P999999999999999999999999D", "P1M", "PT1000000000H" })
        {
            var excessive = CalendarInterchange.Parse("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nDTSTART:20270312T143000Z\r\nDURATION:" + duration + "\r\nSUMMARY:Invalid duration\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
            check(excessive.Entries.Count == 0 && excessive.Skipped == 1,
                "Unsupported and overflowing calendar durations are skipped without crashing the import");
        }
        foreach (var malformed in new[]
        {
            "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\nBEGIN:VEVENT\r\nDTSTART:20270312T143000Z\r\nEND:VEVENT\r\n",
            "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nDTSTART:20270312T143000Z\r\nEND:VCALENDAR\r\n"
        })
        {
            var malformedRejected = false;
            try { CalendarInterchange.Parse(malformed); } catch (ArgumentException) { malformedRejected = true; }
            check(malformedRejected, "Events outside or incomplete inside their calendar are rejected before importing partial data");
        }
        var capacity = new OrganizerStore(Path.Combine(directory, "capacity"));
        capacity.ImportEvents(Enumerable.Range(0, OrganizerStore.MaximumEntries).Select(i => new CalendarEntry(Guid.NewGuid(), "Entry " + i, start, 30)));
        var existing = capacity.Events[0];
        check(capacity.ImportEvents(new[] { new CalendarEntry(Guid.NewGuid(), "Over capacity", start, 30), existing with { Title = "Updated at capacity" } }) == 1
            && capacity.Events.Single(e => e.Id == existing.Id).Title == "Updated at capacity",
            "A full calendar still applies edits after an import row exceeds its capacity");
        var typedTime = CalendarInterchange.Parse("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nDTSTART;VALUE=DATE-TIME:20270312T143000Z\r\nDTEND:20270312T153000Z\r\nSUMMARY:Explicit date-time\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        check(!typedTime.Entries.Single().AllDay && typedTime.Entries.Single().DurationMinutes == 60,
            "Explicit DATE-TIME calendar values remain timed events");
        var calendarFile = Path.Combine(directory, "roundtrip.ics"); File.WriteAllText(calendarFile, longCalendar);
        check(CalendarInterchange.ReadFile(calendarFile).Entries.Single().Description == longNote,
            "Calendar import reads the actual bounded local file");
        var rejected = false;
        try { CalendarInterchange.ReadFile("https://example.com/calendar.ics"); } catch (ArgumentException) { rejected = true; }
        check(rejected, "Calendar file import refuses URLs rather than fetching them");
        var oversizedFile = Path.Combine(directory, "oversized.ics"); File.WriteAllText(oversizedFile, new string('x', CalendarInterchange.MaximumImportBytes + 1));
        rejected = false;
        try { CalendarInterchange.ReadFile(oversizedFile); } catch (ArgumentException) { rejected = true; }
        check(rejected, "Calendar file import enforces its byte limit before parsing");
        rejected = false;
        try { store.AddEvent("  ", start, 0); } catch (ArgumentException) { rejected = true; }
        check(rejected && store.Events.Count == 1, "Invalid calendar input cannot create an empty or zero-length event");

        var due = store.AddReminder("Take a break", start);
        check(store.TakeDueReminders(start.AddMinutes(-1)).Count == 0, "Reminders do not notify before their due time");
        check(store.TakeDueReminders(start).Single().Id == due.Id && store.TakeDueReminders(start.AddMinutes(1)).Count == 0,
            "A due reminder notifies once even when the schedule is checked repeatedly");
        check(new OrganizerStore(directory).TakeDueReminders(start.AddHours(2)).Count == 0,
            "Reminder notification acknowledgement survives a restart");
        store.ToggleReminder(due.Id);
        check(store.Reminders.Single().Completed && store.NextReminderDue is null,
            "Completed reminders no longer schedule notifications");
        check(!OrganizerCalendar.TryParseReminderDue("2027-02-30 09:00", start, out _)
            && OrganizerCalendar.TryParseReminderDue("tomorrow 09:00", start, out var tomorrow)
            && DateOnly.FromDateTime(tomorrow!.Value.LocalDateTime) == DateOnly.FromDateTime(start.LocalDateTime).AddDays(1),
            "Reminder editor accepts relative due times and rejects impossible dates");
        store.SetNote("A quick note \u2014 saved locally.\nA second line.");
        check(new OrganizerStore(directory).Note == store.Note, "Quick notes persist exactly, including line breaks");
        store.SetClockZone(1, "Tokyo Standard Time");
        check(new OrganizerStore(directory).ClockZoneIds[1] == "Tokyo Standard Time",
            "Selected world clock cities survive a restart");

        var stopwatch = new OrganizerStopwatch();
        stopwatch.Toggle(); await Task.Delay(65); stopwatch.Toggle();
        var paused = stopwatch.Elapsed;
        await Task.Delay(45);
        check(paused >= TimeSpan.FromMilliseconds(50) && stopwatch.Elapsed == paused,
            "Stopwatch uses elapsed time and remains fixed while paused");
        stopwatch.Toggle(); await Task.Delay(35); stopwatch.AddLap(); stopwatch.Toggle();
        check(stopwatch.Laps.Count == 1 && stopwatch.Laps[0].Total >= paused,
            "Stopwatch laps include elapsed time accumulated before a pause");
        stopwatch.Reset();
        check(stopwatch.Elapsed == TimeSpan.Zero && stopwatch.Laps.Count == 0 && !stopwatch.IsRunning,
            "Reset clears stopwatch elapsed time and laps");

        using var panel = new OrganizerPanel(new OrganizerStore(Path.Combine(directory, "panel")));
        var window = new Window { Width = 610, Height = 325, ShowInTaskbar = false, Content = panel, Background = System.Windows.Media.Brushes.Black };
        try
        {
            window.Show(); panel.ShowCalendar(); panel.SelectDateForTest(new DateOnly(2027, 3, 12));
            window.UpdateLayout();
            ((Button)panel.FindName("AgendaAddButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((TextBox)panel.FindName("EventTitleBox")).Text = "Native calendar entry";
            ((TextBox)panel.FindName("EventTimeBox")).Text = "16:25";
            ((TextBox)panel.FindName("EventDurationBox")).Text = "40";
            ((Button)panel.FindName("EventSaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(panel.StoreForTest.Events.Single().Title == "Native calendar entry"
                && !panel.EventEditorVisibleForTest, "Native calendar editor validates and saves a selected-day event");
            var multiDay = panel.StoreForTest.AddEvent("Three days", OrganizerCalendar.LocalInstant(new DateOnly(2027, 3, 12), TimeOnly.MinValue), 4320, true);
            panel.EditEventForTest(multiDay);
            ((TextBox)panel.FindName("EventTitleBox")).Text = "Edited three days";
            ((Button)panel.FindName("EventSaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(panel.StoreForTest.Events.Single(e => e.Id == multiDay.Id).DurationMinutes == 4320
                && panel.StoreForTest.EventsOn(new DateOnly(2027, 3, 14)).Any(e => e.Id == multiDay.Id),
                "Editing imported multi-day all-day events preserves their full date range");
            panel.ShowTools(); panel.SelectToolForTest("notes");
            ((TextBox)panel.FindName("NotesBox")).Text = "Saved by the native note editor";
            panel.FlushEdits();
            check(panel.StoreForTest.Note == "Saved by the native note editor", "Native notes editor flushes pending autosave on close");
            panel.SelectToolForTest("clocks"); window.UpdateLayout();
            check(panel.ClockCountForTest == 4, "World clocks render four practical cities in the native panel");
            panel.SelectToolForTest("stopwatch"); window.UpdateLayout();
            ((Button)panel.FindName("StopwatchToggleButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(110);
            check(panel.DisplayTimerRunningForTest, "A visible running stopwatch updates its presentation");
            panel.Visibility = Visibility.Collapsed;
            check(!panel.DisplayTimerRunningForTest, "Hidden organizer tools stop their presentation timer");
            var notifications = 0;
            panel.Notification += (_, message) => { if (message == "Reminder: Hidden due fixture") notifications++; };
            panel.StoreForTest.AddReminder("Hidden due fixture", DateTimeOffset.Now.AddMilliseconds(400));
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (notifications == 0 && DateTime.UtcNow < deadline) await Task.Delay(25);
            check(notifications == 1 && panel.StoreForTest.Reminders.Single().Notified,
                "A collapsed organizer still delivers and acknowledges its scheduled reminder");
            panel.Visibility = Visibility.Visible;
            var oldMotion = Application.Current.TryFindResource(InteractionMotion.PreferenceKey);
            try
            {
                Application.Current.Resources[InteractionMotion.PreferenceKey] = true;
                ((Button)panel.FindName("RemindersTool")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Application.Current.Resources[InteractionMotion.PreferenceKey] = false;
                await Task.Delay(25);
                var remindersView = (FrameworkElement)panel.FindName("RemindersView");
                check(remindersView.Opacity == 1 && remindersView.RenderTransform is System.Windows.Media.TranslateTransform { Y: 0 },
                    "Reduced motion immediately finishes organizer transitions");
            }
            finally
            {
                if (oldMotion is null) Application.Current.Resources.Remove(InteractionMotion.PreferenceKey);
                else Application.Current.Resources[InteractionMotion.PreferenceKey] = oldMotion;
            }
        }
        finally { window.Close(); }
    }
}
