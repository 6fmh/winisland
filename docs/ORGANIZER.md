# Calendar and everyday tools

The calendar and Tools panels use the same black surface, rounded controls, Inter fonts, and small transitions as the rest of the notch. The main calendar shows a complete six-week month using the Windows locale's first weekday. Select a day to see its agenda, use the arrows to browse, or choose Today to return. Events can be added, edited, and removed, with a 24-hour start time, minute duration, all-day option, and optional notes. Editing keeps the notch open; Enter saves, Ctrl+Enter saves from the notes field, and Escape cancels the editor.

This is a **local calendar**. Import reads a user-selected `.ics` file up to 1 MB; export writes individual events as RFC 5545 calendar data. Unicode line folding, escaped descriptions, UTC times, named system time zones, and all-day dates are supported. Reimporting the same event updates its stable identity. Recurring events and unavailable custom time zones are counted as skipped, with that result shown to the user. Winisland does not connect to iCloud, Google Calendar, or Microsoft accounts.

Tools keeps five features behind its own navigation strip:

- **Notes:** a scratchpad with 450 ms debounced saving, a character count, and a flush when switching panels, collapsing, or closing. It holds up to 20,000 characters.
- **Reminders:** add, edit, complete, or remove tasks. A due time is optional; use `18:00`, `tomorrow 09:00`, or `2027-03-12 14:30`. Due reminders are acknowledged in the data file before notification, and they remain scheduled while the panel is hidden. Notifications require Winisland to remain running.
- **Clocks:** local time and three selectable world cities, calculated with Windows time-zone and daylight-saving data. City choices persist. The display updates only while visible.
- **Stopwatch:** elapsed-time measurement, pause/resume, reset, and the latest 20 laps with split and total times. Timing continues while the panel is hidden, but display updates stop. Stopwatch state resets when Winisland exits.
- **Shortcuts:** user-initiated Downloads, Documents, screenshot, sound settings, network settings, and Task Manager actions.

Events, reminders, notes, and clock choices live in `%LOCALAPPDATA%\Winisland\organizer.json`, or the isolated data directory used by native verification. Writes replace the file atomically, and failed writes do not publish the new in-memory state. Entries and data size are bounded. No media history, clipboard content, account credentials, or remote-calendar data is collected by these tools.

Transitions honor the application's reduced-motion preference, including changes during an animation. Clocks update once per second while visible; the stopwatch updates every 100 ms while visible and running. The reminder timer sleeps until the next due time, with an hourly recheck for clock changes. The organizer never rebuilds its visual tree on these display ticks.
