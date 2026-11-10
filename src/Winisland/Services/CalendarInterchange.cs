using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Winisland.Services;

public sealed record CalendarImport(IReadOnlyList<CalendarEntry> Entries, int Skipped);

// A bounded RFC 5545 interchange for individual events. Recurrence rules and
// custom VTIMEZONE definitions are not flattened into potentially wrong dates.
public static class CalendarInterchange
{
    public const int MaximumImportBytes = 1024 * 1024;
    private sealed record Property(string Value, string Parameters);
    private static readonly Regex DurationPattern = new(@"^P(?:(?<w>\d+)W|(?:(?<d>\d+)D)?(?:T(?:(?<h>\d+)H)?(?:(?<m>\d+)M)?(?:(?<s>\d+)S)?)?)$", RegexOptions.CultureInvariant);

    public static CalendarImport ReadFile(string fileName)
    {
        if (!Path.IsPathFullyQualified(fileName) || Uri.TryCreate(fileName, UriKind.Absolute, out var uri) && !uri.IsFile)
            throw new ArgumentException("Choose an .ics file from your computer.");
        using var stream = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumImportBytes) throw new ArgumentException("Choose a calendar smaller than 1 MB.");
        // Read at most one byte beyond the limit, even if a file grows after its
        // initial length check. Native file paths never become HTTP requests.
        var bytes = new byte[MaximumImportBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = stream.Read(bytes, count, bytes.Length - count);
            if (read == 0) break;
            count += read;
        }
        if (count > MaximumImportBytes) throw new ArgumentException("Choose a calendar smaller than 1 MB.");
        try { return Parse(new UTF8Encoding(false, true).GetString(bytes, 0, count).TrimStart('\uFEFF')); }
        catch (DecoderFallbackException) { throw new ArgumentException("Choose an .ics file saved as UTF-8 text."); }
    }

    public static CalendarImport Parse(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaximumImportBytes) throw new ArgumentException("Choose a calendar smaller than 1 MB.");
        var lines = Unfold(text);
        ValidateComponents(lines);
        var entries = new List<CalendarEntry>();
        var skipped = 0;
        Dictionary<string, Property>? current = null;
        var count = 0;
        var nested = 0;
        foreach (var line in lines)
        {
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (++count > OrganizerStore.MaximumEntries) { skipped++; current = null; continue; }
                current = new Dictionary<string, Property>(StringComparer.OrdinalIgnoreCase); nested = 0;
                continue;
            }
            if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (current is null) continue;
                try
                {
                    if (current.ContainsKey("RRULE") || current.ContainsKey("RDATE") || current.ContainsKey("EXRULE") || current.ContainsKey("RECURRENCE-ID"))
                        throw new ArgumentException("Recurring events require an account calendar.");
                    if (!current.TryGetValue("DTSTART", out var startProperty)) throw new ArgumentException("Event has no start date.");
                    var start = ParseInstant(startProperty, out var allDay);
                    var duration = allDay ? 1440 : 60;
                    if (current.TryGetValue("DTEND", out var endProperty))
                    {
                        if (current.ContainsKey("DURATION")) throw new ArgumentException("Event supplies both an end and a duration.");
                        var end = ParseInstant(endProperty, out var endAllDay);
                        if (allDay != endAllDay) throw new ArgumentException("Event start and end use different value types.");
                        duration = allDay ? (DateOnly.FromDateTime(end.LocalDateTime).DayNumber - DateOnly.FromDateTime(start.LocalDateTime).DayNumber) * 1440
                            : (int)Math.Round((end - start).TotalMinutes);
                    }
                    else if (current.TryGetValue("DURATION", out var durationProperty))
                    {
                        var span = ParseDuration(durationProperty.Value, allDay);
                        duration = (int)Math.Round(span.TotalMinutes);
                    }
                    if (duration is < 1 or > 10080 || start.Year < 1900 || start.Year >= 9999) throw new ArgumentException("Event date or duration is out of range.");
                    var title = current.TryGetValue("SUMMARY", out var summary) ? Unescape(summary.Value).Trim() : "Untitled event";
                    if (title.Length is < 1 or > 140 || title.Any(char.IsControl)) throw new ArgumentException("Event title is invalid.");
                    var description = current.TryGetValue("DESCRIPTION", out var note) ? Unescape(note.Value) : "";
                    if (description.Length > 10000) throw new ArgumentException("Event notes are too long.");
                    var uid = current.TryGetValue("UID", out var idProperty) ? idProperty.Value : $"{title}|{start:O}|{duration}";
                    var id = ParseId(uid);
                    entries.Add(new CalendarEntry(id, title, start, duration, allDay, description));
                }
                catch (Exception ex) when (ex is ArgumentException or FormatException or TimeZoneNotFoundException or InvalidTimeZoneException)
                { skipped++; }
                current = null;
                continue;
            }
            if (current is null) continue;
            if (line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase)) { nested++; continue; }
            if (line.StartsWith("END:", StringComparison.OrdinalIgnoreCase) && nested > 0) { nested--; continue; }
            if (nested > 0) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var header = line[..colon];
            var semicolon = header.IndexOf(';');
            var name = semicolon < 0 ? header : header[..semicolon];
            var parameters = semicolon < 0 ? "" : header[(semicolon + 1)..];
            // Multiple attendee and alarm properties do not become app commands.
            current.TryAdd(name, new Property(line[(colon + 1)..], parameters));
        }
        return new CalendarImport(entries, skipped);
    }

    public static string Export(IEnumerable<CalendarEntry> entries)
    {
        var lines = new List<string> { "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//Winisland//Local Calendar//EN", "CALSCALE:GREGORIAN" };
        foreach (var entry in entries.Take(OrganizerStore.MaximumEntries))
        {
            lines.Add("BEGIN:VEVENT");
            lines.Add($"UID:{entry.Id:N}@winisland.local");
            lines.Add("DTSTAMP:" + DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
            if (entry.AllDay)
            {
                var day = DateOnly.FromDateTime(entry.Start.LocalDateTime);
                lines.Add("DTSTART;VALUE=DATE:" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
                lines.Add("DTEND;VALUE=DATE:" + day.AddDays(Math.Max(1, entry.DurationMinutes / 1440)).ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            }
            else
            {
                lines.Add("DTSTART:" + entry.Start.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
                lines.Add("DTEND:" + entry.End.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
            }
            lines.Add("SUMMARY:" + Escape(entry.Title));
            if (entry.Description.Length > 0) lines.Add("DESCRIPTION:" + Escape(entry.Description));
            lines.Add("END:VEVENT");
        }
        lines.Add("END:VCALENDAR");
        return string.Join("\r\n", lines.SelectMany(Fold)) + "\r\n";
    }

    private static DateTimeOffset ParseInstant(Property property, out bool allDay)
    {
        var value = property.Value.Trim();
        var type = property.Parameters.Split(';').FirstOrDefault(p => p.StartsWith("VALUE=", StringComparison.OrdinalIgnoreCase));
        if (type is not null && !type.Equals("VALUE=DATE", StringComparison.OrdinalIgnoreCase) && !type.Equals("VALUE=DATE-TIME", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Unsupported calendar value type.");
        allDay = type?.Equals("VALUE=DATE", StringComparison.OrdinalIgnoreCase) == true || type is null && value.Length == 8;
        if (allDay)
        {
            if (!DateOnly.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) throw new FormatException("Invalid calendar date.");
            return OrganizerCalendar.LocalInstant(date, TimeOnly.MinValue);
        }
        if (value.EndsWith('Z'))
            return DateTimeOffset.ParseExact(value, new[] { "yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd'T'HHmm'Z'" }, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        var wallTime = DateTime.ParseExact(value, new[] { "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm" }, CultureInfo.InvariantCulture, DateTimeStyles.None);
        var zoneId = property.Parameters.Split(';').FirstOrDefault(p => p.StartsWith("TZID=", StringComparison.OrdinalIgnoreCase));
        var zone = zoneId is null ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(zoneId[5..].Trim('"'));
        if (zone.IsInvalidTime(wallTime)) throw new ArgumentException("Calendar event falls in a daylight-saving clock change.");
        return new DateTimeOffset(DateTime.SpecifyKind(wallTime, DateTimeKind.Unspecified), zone.GetUtcOffset(wallTime));
    }

    private static TimeSpan ParseDuration(string value, bool allDay)
    {
        var match = DurationPattern.Match(value);
        if (!match.Success || allDay && value.Contains('T')) throw new FormatException("Unsupported calendar duration.");
        long seconds = 0;
        foreach (var (group, factor) in new[] { ("w", 604800L), ("d", 86400L), ("h", 3600L), ("m", 60L), ("s", 1L) })
        {
            if (!match.Groups[group].Success) continue;
            if (!int.TryParse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var amount) || amount > 604800)
                throw new FormatException("Calendar duration is out of range.");
            seconds += amount * factor;
        }
        if (seconds is < 1 or > 604800) throw new ArgumentException("Calendar duration is out of range.");
        return TimeSpan.FromSeconds(seconds);
    }

    private static void ValidateComponents(IReadOnlyList<string> lines)
    {
        var components = new Stack<string>();
        var complete = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase))
            {
                var component = line[6..];
                if (components.Count == 0 && (complete || !component.Equals("VCALENDAR", StringComparison.OrdinalIgnoreCase))
                    || component.Equals("VEVENT", StringComparison.OrdinalIgnoreCase) && (components.Count != 1 || !components.Peek().Equals("VCALENDAR", StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("That file has an invalid iCalendar structure.");
                components.Push(component);
            }
            else if (line.StartsWith("END:", StringComparison.OrdinalIgnoreCase))
            {
                if (components.Count == 0 || !components.Pop().Equals(line[4..], StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("That file has an invalid iCalendar structure.");
                if (components.Count == 0) complete = true;
            }
            else if (components.Count == 0 && !string.IsNullOrWhiteSpace(line))
                throw new ArgumentException("That file has data outside its iCalendar component.");
        }
        if (!complete || components.Count != 0) throw new ArgumentException("That file is not a complete iCalendar file.");
    }

    private static Guid ParseId(string uid)
    {
        var separator = uid.IndexOf('@');
        var raw = separator < 0 ? uid : uid[..separator];
        if (uid.EndsWith("@winisland.local", StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(raw, "N", out var ownId) && ownId != Guid.Empty) return ownId;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(uid));
        return new Guid(digest.AsSpan(0, 16));
    }

    private static IReadOnlyList<string> Unfold(string text)
    {
        var result = new List<string>();
        using var reader = new StringReader(text);
        StringBuilder? current = null;
        var count = 0;
        while (reader.ReadLine() is { } line)
        {
            if (++count > 100000) throw new ArgumentException("That calendar has too many lines.");
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t') && current is not null) current.Append(line.AsSpan(1));
            else
            {
                if (current is not null) result.Add(current.ToString());
                current = new StringBuilder(line);
            }
        }
        if (current is not null) result.Add(current.ToString());
        return result;
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\r\n", "\n").Replace("\r", "\n")
        .Replace("\n", "\\n").Replace(";", "\\;").Replace(",", "\\,");

    private static string Unescape(string value)
    {
        var result = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            { var escaped = value[++i]; result.Append(escaped is 'n' or 'N' ? '\n' : escaped); }
            else result.Append(value[i]);
        }
        return result.ToString();
    }

    private static IEnumerable<string> Fold(string line)
    {
        var buffer = new StringBuilder();
        var bytes = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > 75)
            { yield return buffer.ToString(); buffer.Clear(); buffer.Append(' '); bytes = 1; }
            buffer.Append(rune.ToString()); bytes += rune.Utf8SequenceLength;
        }
        yield return buffer.ToString();
    }
}
