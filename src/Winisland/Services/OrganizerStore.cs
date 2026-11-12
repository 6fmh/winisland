using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Winisland.Services;

public sealed record CalendarEntry(Guid Id, string Title, DateTimeOffset Start, int DurationMinutes,
    bool AllDay = false, string Description = "")
{
    public DateTimeOffset End => Start.AddMinutes(DurationMinutes);
    public string TimeLabel => AllDay ? "All day" : Start.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);
}

public sealed record ReminderEntry(Guid Id, string Title, DateTimeOffset? Due = null,
    bool Completed = false, bool Notified = false)
{
    public string DueLabel => Due is { } due ? due.ToLocalTime().ToString("ddd, d MMM · HH:mm", CultureInfo.CurrentCulture) : "No due time";
}

[Flags]
public enum OrganizerChange { Events = 1, Reminders = 2, Notes = 4, Clocks = 8 }
public sealed class OrganizerChangedEventArgs : EventArgs
{
    public OrganizerChange Change { get; }
    public OrganizerChangedEventArgs(OrganizerChange change) => Change = change;
}

// The organizer has its own atomic data file. It does not read account calendars,
// access the clipboard, or alter existing settings, shelf files or credentials.
public sealed class OrganizerStore
{
    public const int MaximumEntries = 200;
    public const int MaximumNoteLength = 20000;
    private const int MaximumFileBytes = 2 * 1024 * 1024;
    private sealed record OrganizerData
    {
        public int Version { get; init; } = 1;
        public CalendarEntry[] Events { get; init; } = Array.Empty<CalendarEntry>();
        public ReminderEntry[] Reminders { get; init; } = Array.Empty<ReminderEntry>();
        public string Note { get; init; } = "";
        public string[] ClockZoneIds { get; init; } = new[] { "Local", "GMT Standard Time", "Eastern Standard Time", "Tokyo Standard Time" };
    }
    private readonly string _filePath;
    private OrganizerData _data = new();
    public IReadOnlyList<CalendarEntry> Events => _data.Events;
    public IReadOnlyList<ReminderEntry> Reminders => _data.Reminders;
    public string Note => _data.Note;
    public IReadOnlyList<string> ClockZoneIds => _data.ClockZoneIds;
    public DateTimeOffset? NextReminderDue => _data.Reminders.Where(r => !r.Completed && !r.Notified && r.Due.HasValue)
        .Select(r => r.Due).OrderBy(d => d).FirstOrDefault();
    public event EventHandler<OrganizerChangedEventArgs>? Changed;

    public OrganizerStore(string? directoryPath = null)
    {
        _filePath = Path.Combine(directoryPath ?? SettingsStore.DirectoryPath, "organizer.json");
        try
        {
            if (!File.Exists(_filePath)) return;
            if (new FileInfo(_filePath).Length > MaximumFileBytes) throw new JsonException("Organizer data exceeds its size limit.");
            var read = JsonSerializer.Deserialize<OrganizerData>(File.ReadAllText(_filePath));
            if (read is null || read.Version != 1) throw new JsonException("Unrecognized organizer format.");
            _data = Normalize(read);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        { Log.Error("Load organizer", ex); }
    }

    public CalendarEntry AddEvent(string title, DateTimeOffset start, int durationMinutes, bool allDay = false, string description = "")
    {
        if (_data.Events.Length >= MaximumEntries) throw new ArgumentException("The calendar holds up to 200 events. Remove an old event first.");
        var item = ValidateEvent(new CalendarEntry(Guid.NewGuid(), title, start, durationMinutes, allDay, description));
        Persist(_data with { Events = _data.Events.Append(item).OrderBy(e => e.Start).ToArray() }, OrganizerChange.Events);
        return item;
    }

    public void UpdateEvent(Guid id, string title, DateTimeOffset start, int durationMinutes, bool allDay, string description)
    {
        if (!_data.Events.Any(e => e.Id == id)) throw new ArgumentException("That event is no longer available.");
        var item = ValidateEvent(new CalendarEntry(id, title, start, durationMinutes, allDay, description));
        Persist(_data with { Events = _data.Events.Select(e => e.Id == id ? item : e).OrderBy(e => e.Start).ToArray() }, OrganizerChange.Events);
    }

    public void RemoveEvent(Guid id) => Persist(_data with { Events = _data.Events.Where(e => e.Id != id).ToArray() }, OrganizerChange.Events);

    public IReadOnlyList<CalendarEntry> EventsOn(DateOnly day)
    {
        if (_data.Events.Length == 0 || day.Year < 1900) return Array.Empty<CalendarEntry>();
        return _data.Events.Where(e => e.AllDay
                ? DateOnly.FromDateTime(e.Start.DateTime) <= day && day.DayNumber < DateOnly.FromDateTime(e.Start.DateTime).DayNumber + Math.Max(1, e.DurationMinutes / 1440)
                : DateOnly.FromDateTime(e.Start.LocalDateTime) <= day
                    && (DateOnly.FromDateTime(e.End.LocalDateTime) > day
                        || DateOnly.FromDateTime(e.End.LocalDateTime) == day && e.End.LocalDateTime.TimeOfDay > TimeSpan.Zero))
            .OrderBy(e => !e.AllDay).ThenBy(e => e.Start).ToArray();
    }

    public int ImportEvents(IEnumerable<CalendarEntry> entries)
    {
        var merged = _data.Events.ToDictionary(e => e.Id);
        var count = 0;
        foreach (var proposed in entries.Take(MaximumEntries + 1))
        {
            var item = ValidateEvent(proposed);
            if (!merged.ContainsKey(item.Id) && merged.Count >= MaximumEntries) continue;
            if (merged.TryGetValue(item.Id, out var previous) && previous == item) continue;
            merged[item.Id] = item; count++;
        }
        if (count > 0) Persist(_data with { Events = merged.Values.OrderBy(e => e.Start).ToArray() }, OrganizerChange.Events);
        return count;
    }

    public ReminderEntry AddReminder(string title, DateTimeOffset? due = null)
    {
        if (_data.Reminders.Length >= MaximumEntries) throw new ArgumentException("Reminders holds up to 200 items. Remove a completed item first.");
        var item = ValidateReminder(new ReminderEntry(Guid.NewGuid(), title, due));
        Persist(_data with { Reminders = _data.Reminders.Append(item).ToArray() }, OrganizerChange.Reminders);
        return item;
    }

    public void UpdateReminder(Guid id, string title, DateTimeOffset? due)
    {
        var previous = _data.Reminders.FirstOrDefault(r => r.Id == id) ?? throw new ArgumentException("That reminder is no longer available.");
        var item = ValidateReminder(previous with { Title = title, Due = due, Notified = due == previous.Due && previous.Notified });
        Persist(_data with { Reminders = _data.Reminders.Select(r => r.Id == id ? item : r).ToArray() }, OrganizerChange.Reminders);
    }

    public void ToggleReminder(Guid id) => Persist(_data with
    {
        Reminders = _data.Reminders.Select(r => r.Id == id ? r with { Completed = !r.Completed } : r).ToArray()
    }, OrganizerChange.Reminders);

    public void RemoveReminder(Guid id) => Persist(_data with { Reminders = _data.Reminders.Where(r => r.Id != id).ToArray() }, OrganizerChange.Reminders);

    public IReadOnlyList<ReminderEntry> TakeDueReminders(DateTimeOffset now)
    {
        var due = _data.Reminders.Where(r => !r.Completed && !r.Notified && r.Due <= now).ToArray();
        if (due.Length == 0) return due;
        var ids = due.Select(r => r.Id).ToHashSet();
        // Persist acknowledgement before emitting the notification, so restarting
        // does not re-alert for already delivered reminders.
        Persist(_data with { Reminders = _data.Reminders.Select(r => ids.Contains(r.Id) ? r with { Notified = true } : r).ToArray() }, OrganizerChange.Reminders);
        return due;
    }

    public void SetNote(string text)
    {
        if (text.Length > MaximumNoteLength) throw new ArgumentException("Notes holds up to 20,000 characters.");
        if (_data.Note != text) Persist(_data with { Note = text }, OrganizerChange.Notes);
    }

    public void SetClockZone(int index, string id)
    {
        if (index is < 1 or > 3 || !WorldClockChoice.All.Any(c => c.Id == id)) throw new ArgumentException("Choose one of the available cities.");
        if (_data.ClockZoneIds[index] == id) return;
        var zones = (string[])_data.ClockZoneIds.Clone(); zones[index] = id;
        Persist(_data with { ClockZoneIds = zones }, OrganizerChange.Clocks);
    }

    private static CalendarEntry ValidateEvent(CalendarEntry entry)
    {
        var title = ValidateTitle(entry.Title);
        if (entry.Id == Guid.Empty || entry.Start.Year < 1900 || entry.Start.Year >= 9999 || entry.DurationMinutes is < 1 or > 10080)
            throw new ArgumentException("Choose a valid date and a duration from 1 minute to 7 days.");
        if (entry.AllDay && entry.DurationMinutes % 1440 != 0)
            throw new ArgumentException("All-day events must last a whole number of days.");
        var description = entry.Description ?? "";
        if (description.Length > 10000) throw new ArgumentException("Event notes hold up to 10,000 characters.");
        _ = entry.End;
        return entry with { Title = title, Description = description };
    }

    private static ReminderEntry ValidateReminder(ReminderEntry entry)
    {
        if (entry.Id == Guid.Empty || entry.Due is { Year: < 1900 or >= 9999 }) throw new ArgumentException("Choose a valid reminder date.");
        return entry with { Title = ValidateTitle(entry.Title) };
    }

    private static string ValidateTitle(string? text)
    {
        var title = text?.Trim() ?? "";
        if (title.Length is < 1 or > 140 || title.Any(char.IsControl)) throw new ArgumentException("Use a title from 1 to 140 characters.");
        return title;
    }

    private static OrganizerData Normalize(OrganizerData data)
    {
        var events = new List<CalendarEntry>();
        foreach (var entry in (data.Events ?? Array.Empty<CalendarEntry>()).Take(MaximumEntries))
        { try { if (entry is not null && !events.Any(e => e.Id == entry.Id)) events.Add(ValidateEvent(entry)); } catch (ArgumentException) { } }
        var reminders = new List<ReminderEntry>();
        foreach (var entry in (data.Reminders ?? Array.Empty<ReminderEntry>()).Take(MaximumEntries))
        { try { if (entry is not null && !reminders.Any(r => r.Id == entry.Id)) reminders.Add(ValidateReminder(entry)); } catch (ArgumentException) { } }
        var defaults = new OrganizerData().ClockZoneIds;
        var supplied = data.ClockZoneIds;
        for (var i = 1; i < 4; i++)
            if (supplied is { Length: 4 } && WorldClockChoice.All.Any(c => c.Id == supplied[i])) defaults[i] = supplied[i];
        return data with { Events = events.OrderBy(e => e.Start).ToArray(), Reminders = reminders.ToArray(),
            Note = (data.Note ?? "")[..Math.Min((data.Note ?? "").Length, MaximumNoteLength)], ClockZoneIds = defaults };
    }

    private void Persist(OrganizerData next, OrganizerChange change)
    {
        var serialized = JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true });
        if (System.Text.Encoding.UTF8.GetByteCount(serialized) > MaximumFileBytes)
            throw new ArgumentException("The organizer is full. Shorten notes or remove old entries first.");
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporary = _filePath + ".tmp";
        File.WriteAllText(temporary, serialized);
        File.Move(temporary, _filePath, true);
        _data = next;
        Changed?.Invoke(this, new OrganizerChangedEventArgs(change));
    }
}

public static class OrganizerCalendar
{
    public static IReadOnlyList<DateOnly?> GetDays(DateOnly month, DayOfWeek firstDay)
    {
        var first = new DateOnly(month.Year, month.Month, 1);
        var offset = ((int)first.DayOfWeek - (int)firstDay + 7) % 7;
        return Enumerable.Range(0, 42).Select(i =>
        {
            var ordinal = first.DayNumber + i - offset;
            return ordinal >= DateOnly.MinValue.DayNumber && ordinal <= DateOnly.MaxValue.DayNumber ? (DateOnly?)DateOnly.FromDayNumber(ordinal) : null;
        }).ToArray();
    }

    public static bool TryShiftMonth(DateOnly month, int offset, out DateOnly result)
    {
        try { result = new DateOnly(month.Year, month.Month, 1).AddMonths(offset); return true; }
        catch (ArgumentOutOfRangeException) { result = month; return false; }
    }

    public static DateTimeOffset LocalInstant(DateOnly day, TimeOnly time)
    {
        var wallTime = day.ToDateTime(time, DateTimeKind.Unspecified);
        if (TimeZoneInfo.Local.IsInvalidTime(wallTime)) throw new ArgumentException("That local time falls in a daylight-saving clock change. Choose another time.");
        return new DateTimeOffset(wallTime, TimeZoneInfo.Local.GetUtcOffset(wallTime));
    }

    public static bool TryParseReminderDue(string text, DateTimeOffset now, out DateTimeOffset? due)
    {
        due = null;
        var input = text.Trim();
        if (input.Length == 0) return true;
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        DateOnly date;
        string time;
        if (input.StartsWith("today ", StringComparison.OrdinalIgnoreCase)) { date = today; time = input[6..].Trim(); }
        else if (input.StartsWith("tomorrow ", StringComparison.OrdinalIgnoreCase))
        {
            if (today == DateOnly.MaxValue) return false;
            date = today.AddDays(1); time = input[9..].Trim();
        }
        else if (TimeOnly.TryParseExact(input, new[] { "H:mm", "HH:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timeOnly))
        { try { due = LocalInstant(today, timeOnly); return true; } catch (ArgumentException) { return false; } }
        else
        {
            var space = input.IndexOf(' ');
            if (space <= 0 || !DateOnly.TryParseExact(input[..space], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return false;
            time = input[(space + 1)..].Trim();
        }
        if (!TimeOnly.TryParseExact(time, new[] { "H:mm", "HH:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedTime)) return false;
        try { due = LocalInstant(date, parsedTime); return true; } catch (ArgumentException) { return false; }
    }
}

public sealed record WorldClockChoice(string Id, string City)
{
    public static IReadOnlyList<WorldClockChoice> All { get; } = new[]
    {
        new WorldClockChoice("GMT Standard Time", "London"), new WorldClockChoice("W. Europe Standard Time", "Stockholm"),
        new WorldClockChoice("Romance Standard Time", "Paris"), new WorldClockChoice("Eastern Standard Time", "New York"),
        new WorldClockChoice("Pacific Standard Time", "Los Angeles"), new WorldClockChoice("Central Standard Time", "Chicago"),
        new WorldClockChoice("Tokyo Standard Time", "Tokyo"), new WorldClockChoice("Korea Standard Time", "Seoul"),
        new WorldClockChoice("Singapore Standard Time", "Singapore"), new WorldClockChoice("India Standard Time", "Mumbai"),
        new WorldClockChoice("Arabian Standard Time", "Dubai"), new WorldClockChoice("AUS Eastern Standard Time", "Sydney"),
        new WorldClockChoice("New Zealand Standard Time", "Auckland"), new WorldClockChoice("UTC", "UTC")
    };
    public static TimeZoneInfo Resolve(string id) => id == "Local" ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(id);
}

public sealed record StopwatchLap(int Number, TimeSpan Total, TimeSpan Split);
public sealed class OrganizerStopwatch
{
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private readonly List<StopwatchLap> _laps = new();
    private TimeSpan _lastLap;
    private int _lapCount;
    public bool IsRunning => _clock.IsRunning;
    public TimeSpan Elapsed => _clock.Elapsed;
    public IReadOnlyList<StopwatchLap> Laps => _laps;
    public void Toggle() { if (_clock.IsRunning) _clock.Stop(); else _clock.Start(); }
    public void Reset() { _clock.Reset(); _laps.Clear(); _lastLap = TimeSpan.Zero; _lapCount = 0; }
    public void AddLap()
    {
        if (!_clock.IsRunning) return;
        var elapsed = _clock.Elapsed;
        _laps.Insert(0, new StopwatchLap(++_lapCount, elapsed, elapsed - _lastLap));
        if (_laps.Count > 20) _laps.RemoveAt(_laps.Count - 1);
        _lastLap = elapsed;
    }
    public static string Format(TimeSpan time) => time.TotalHours >= 1
        ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds / 10:00}"
        : $"{(int)time.TotalMinutes:00}:{time.Seconds:00}.{time.Milliseconds / 10:00}";
}
