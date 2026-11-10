using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Winisland.Services;

namespace Winisland;

internal static partial class FocusVerification
{
    internal static async Task RunCoreAsync(Action<bool, string> assert)
    {
        RunParserChecks(assert);
        await RunTimerChecksAsync(assert);
    }

    private static void RunParserChecks(Action<bool, string> assert)
    {
        foreach (var (input, seconds) in new (string, int)[]
        {
            ("45", 2700), (" 25 ", 1500), ("00:01", 1), ("25:00", 1500), ("2:15", 135),
            ("90:00", 5400), ("1:30:00", 5400), ("0:00:01", 1), ("23:59:59", 86399),
            ("24:00:00", 86400), ("1440", 86400), ("1440:00", 86400)
        })
        {
            assert(FocusDurationParser.TryParse(input, out var duration, out var error)
                && duration == TimeSpan.FromSeconds(seconds) && error.Length == 0,
                $"Focus time parser accepts '{input.Trim()}' with the intended units");
        }
        foreach (var input in new[] { "", "0", "00:00", "-1", "+5", "1.5", "25:", ":30", "1:60", "1:60:00",
            "1:00:60", "24:00:01", "1441", "25:00:00", "1:2:3:4", "999999999999999999999999", "NaN", "abc" })
        {
            assert(!FocusDurationParser.TryParse(input, out _, out var error) && !string.IsNullOrWhiteSpace(error),
                $"Focus time parser rejects invalid input '{input}' with useful feedback");
        }
        foreach (var seconds in new[] { 1, 59, 60, 3599, 3600, 86399, 86400 })
        {
            var formatted = FocusDurationParser.Format(TimeSpan.FromSeconds(seconds));
            assert(FocusDurationParser.TryParse(formatted, out var parsed, out _) && parsed.TotalSeconds == seconds,
                $"Focus time formatting round-trips {seconds} seconds without wrapping hours");
        }
    }

    private static async Task RunTimerChecksAsync(Action<bool, string> assert)
    {
        using var timer = new FocusTimer();
        timer.Start(TimeSpan.FromHours(24));
        await Task.Delay(50);
        assert(timer.Duration == TimeSpan.FromHours(24) && timer.IsRunning && timer.Remaining < timer.Duration,
            "The focus timer supports a full day and measures real elapsed time between UI ticks");
        timer.SetDuration(TimeSpan.FromMinutes(2));
        assert(timer.IsRunning && timer.IsStarted && timer.Duration == TimeSpan.FromMinutes(2)
            && timer.Remaining > TimeSpan.FromSeconds(119),
            "Retiming an active focus session preserves its running state");
        timer.TogglePause();
        timer.SetDuration(TimeSpan.FromHours(1));
        var paused = timer.Remaining;
        await Task.Delay(50);
        assert(timer.IsStarted && !timer.IsRunning && paused == TimeSpan.FromHours(1) && timer.Remaining == paused,
            "Retiming a paused focus session gives the requested time without advancing it");
        timer.TogglePause();
        await Task.Delay(50);
        assert(timer.IsRunning && timer.Remaining < paused, "A custom paused duration resumes from the retimed value");
        timer.Reset(TimeSpan.FromSeconds(1));
        assert(!timer.IsStarted && !timer.IsRunning && timer.Remaining == TimeSpan.FromSeconds(1),
            "Resetting a custom timer restores its exact duration and stops the session");
        foreach (var invalid in new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(500), TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1) })
        {
            var rejected = false;
            try { timer.SetDuration(invalid); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            assert(rejected && timer.Duration == TimeSpan.FromSeconds(1),
                "Invalid programmatic focus durations cannot corrupt timer state");
        }
        var completions = 0;
        timer.Completed += (_, _) => completions++;
        timer.Start(TimeSpan.FromSeconds(1));
        var wait = Stopwatch.StartNew();
        while (completions == 0 && wait.ElapsedMilliseconds < 3000) await Task.Delay(20);
        await Task.Delay(80);
        timer.TogglePause();
        assert(completions == 1 && !timer.IsStarted && !timer.IsRunning && timer.Remaining == TimeSpan.Zero,
            "A short custom focus timer completes once and cannot resume after completion");
    }
}
