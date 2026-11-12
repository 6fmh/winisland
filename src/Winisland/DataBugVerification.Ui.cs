using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Winisland.Controls;
using Winisland.Services;

namespace Winisland;

internal static partial class DataBugVerification
{
    internal static void RunUi(string output, Action<bool, string> check)
    {
        var directory = Path.Combine(output, "all-day-editor-" + Guid.NewGuid().ToString("N"));
        var store = new OrganizerStore(directory);
        using var panel = new OrganizerPanel(store);
        foreach (var offset in new[] { TimeSpan.FromHours(14), TimeSpan.FromHours(-12) })
        {
            var entry = store.AddEvent("Foreign offset all-day", new DateTimeOffset(2027, 3, 12, 0, 0, 0, offset), 2880, true);
            panel.EditEventForTest(entry);
            check(panel.EventEditorVisibleForTest
                && ((TextBlock)panel.FindName("SelectedDateLabel")).Text == new DateOnly(2027, 3, 12).ToString("dddd, d MMM", CultureInfo.CurrentCulture),
                "The actual calendar editor selects an all-day event's civil date despite its foreign saved offset");
            ((Button)panel.FindName("EventSaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var saved = new OrganizerStore(directory).Events.Single(e => e.Id == entry.Id);
            check(!panel.EventEditorVisibleForTest && saved.AllDay && saved.DurationMinutes == 2880
                && DateOnly.FromDateTime(saved.Start.DateTime) == new DateOnly(2027, 3, 12),
                "Saving the actual all-day editor retains the original civil date and full date range");
        }
        var timed = store.AddEvent("Timed instant control", new DateTimeOffset(2027, 3, 12, 0, 0, 0, TimeSpan.FromHours(14)), 45);
        panel.EditEventForTest(timed);
        check(((TextBlock)panel.FindName("SelectedDateLabel")).Text
            == DateOnly.FromDateTime(timed.Start.LocalDateTime).ToString("dddd, d MMM", CultureInfo.CurrentCulture),
            "Timed calendar entries still select their local display date rather than the source offset date");
        ((Button)panel.FindName("EventSaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(store.Events.Single(e => e.Id == timed.Id).Start == timed.Start,
            "Saving an unchanged timed event retains its original instant after local timezone display");
    }
}
