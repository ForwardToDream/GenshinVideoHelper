using System.Globalization;

namespace GenshinVideoHelper.Core.Progress;

public static class TimeText
{
    public static string Format(double seconds)
    {
        var time = TimeSpan.FromSeconds(double.IsFinite(seconds) ? Math.Max(0, seconds) : 0);
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";
    }

    /// <summary>Accepts seconds ("95"), "1:35" or "1:02:03"; a full-width colon is treated as a colon.</summary>
    public static bool TryParse(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Replace('：', ':').Split(':');
        if (parts.Length > 3) return false;
        foreach (var (part, index) in parts.Select((part, index) => (part, index)))
        {
            if (!double.TryParse(part, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || value < 0) return false;
            // Only the leading component may exceed 59, so "90" and "90:00" both mean ninety units.
            if (index > 0 && value >= 60) return false;
            seconds = seconds * 60 + value;
        }
        return double.IsFinite(seconds);
    }
}
