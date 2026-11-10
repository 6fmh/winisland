using System;
using System.Globalization;

namespace Winisland.Services;

public static class FocusDurationParser
{
    public static readonly TimeSpan Minimum = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan Maximum = TimeSpan.FromHours(24);

    // A bare number means minutes; two parts mean mm:ss, three mean h:mm:ss.
    // Interpret explicitly rather than accepting TimeSpan.Parse's ambiguous days.
    public static bool TryParse(string? text, out TimeSpan duration, out string error)
    {
        duration = default;
        error = "Use minutes, mm:ss, or h:mm:ss.";
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Enter minutes or a time such as 25:00.";
            return false;
        }
        text = text.Trim();
        if (text.Length > 20) return false;
        var parts = text.Split(':');
        if (parts.Length is < 1 or > 3) return false;
        Span<int> values = stackalloc int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out values[i])) return false;
        }
        if ((parts.Length == 2 && values[1] > 59) || (parts.Length == 3 && (values[1] > 59 || values[2] > 59)))
        {
            error = "Minutes and seconds after a colon must be 00–59.";
            return false;
        }
        var seconds = parts.Length switch
        {
            1 => values[0] * 60L,
            2 => values[0] * 60L + values[1],
            _ => values[0] * 3600L + values[1] * 60L + values[2]
        };
        if (seconds is < 1 or > 86400)
        {
            error = "Set a time from 0:01 to 24:00:00.";
            return false;
        }
        duration = TimeSpan.FromSeconds(seconds);
        error = "";
        return true;
    }

    public static string Format(TimeSpan duration)
    {
        var seconds = (long)Math.Max(0, Math.Ceiling(duration.TotalSeconds));
        return seconds >= 3600
            ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}"
            : $"{seconds / 60:00}:{seconds % 60:00}";
    }
}
