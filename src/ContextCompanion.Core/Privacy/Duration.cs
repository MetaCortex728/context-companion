using System.Globalization;

namespace ContextCompanion.Core.Privacy;

/// <summary>Short durations such as <c>90s</c>, <c>30m</c>, <c>12h</c>, <c>7d</c> or <c>2w</c>.</summary>
public static class Duration
{
    /// <summary>The longest duration accepted: about ten years.</summary>
    public static readonly TimeSpan Max = TimeSpan.FromDays(3650);

    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = default;
        if (string.IsNullOrEmpty(text) || text.Length < 2 || text.Length > 12) return false;
        if (!long.TryParse(text.AsSpan(0, text.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out long n) || n < 1) return false;
        double seconds = text[^1] switch
        {
            's' => n,
            'm' => n * 60.0,
            'h' => n * 3600.0,
            'd' => n * 86400.0,
            'w' => n * 604800.0,
            _ => -1,
        };
        if (seconds <= 0 || seconds > Max.TotalSeconds) return false;
        value = TimeSpan.FromSeconds(seconds);
        return true;
    }

    public static TimeSpan Parse(string text) =>
        TryParse(text, out var value) ? value : throw new FormatException("A duration is a positive whole number followed by s, m, h, d or w, for example 7d.");
}
