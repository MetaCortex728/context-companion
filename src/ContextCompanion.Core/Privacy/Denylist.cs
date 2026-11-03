using System.Text.RegularExpressions;
using ContextCompanion.Core.Packets;

namespace ContextCompanion.Core.Privacy;

/// <summary>
/// One denylist rule. A window matches when every condition the rule sets matches (at least one must be set).
/// Comparisons ignore case. <see cref="ProcessName"/> compares the executable name only, with or without
/// <c>.exe</c>; <see cref="TitlePattern"/> is a .NET regular expression run with a timeout.
/// </summary>
public sealed record DenylistRule
{
    /// <summary>Name recorded in the audit log and in redaction records: 1-64 characters from A-Z, a-z, 0-9, '.', '_', '-'.</summary>
    public string Name { get; set; } = "";
    /// <summary>Executable name, for example <c>KeePass.exe</c>.</summary>
    public string? ProcessName { get; set; }
    /// <summary>Window class name, compared exactly but ignoring case.</summary>
    public string? WindowClass { get; set; }
    /// <summary>Regular expression searched for in the window title, ignoring case.</summary>
    public string? TitlePattern { get; set; }
    /// <summary>Free text for the person editing the settings. Never written anywhere else.</summary>
    public string? Description { get; set; }
}

/// <summary>A compiled, validated denylist.</summary>
public sealed class Denylist
{
    /// <summary>How long one title pattern may run. A pattern that times out counts as a match (fail closed).</summary>
    public static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    private readonly CompiledRule[] rules;

    private Denylist(CompiledRule[] rules) => this.rules = rules;

    public int Count => rules.Length;

    /// <summary>Problems with a list of rules; empty when it is valid. Messages name rules, never titles.</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<DenylistRule>? rules)
    {
        var problems = new List<string>();
        if (rules is null) { problems.Add("denylist must be an array"); return problems; }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            string at = $"denylist[{i}]";
            if (rule is null) { problems.Add($"{at} must be an object"); continue; }
            if (!PacketNames.IsValidAgentName(rule.Name))
                problems.Add($"{at}.name must be 1 to 64 characters from A-Z, a-z, 0-9, '.', '_' and '-'");
            else if (!names.Add(rule.Name))
                problems.Add($"{at}.name '{rule.Name}' is used more than once");
            if (string.IsNullOrWhiteSpace(rule.ProcessName) && string.IsNullOrWhiteSpace(rule.WindowClass) && string.IsNullOrEmpty(rule.TitlePattern))
                problems.Add($"{at} needs process_name, window_class or title_pattern");
            if (rule.ProcessName is not null && string.IsNullOrWhiteSpace(rule.ProcessName))
                problems.Add($"{at}.process_name must not be empty");
            if (rule.WindowClass is not null && string.IsNullOrWhiteSpace(rule.WindowClass))
                problems.Add($"{at}.window_class must not be empty");
            if (rule.TitlePattern is not null)
            {
                if (rule.TitlePattern.Length == 0) problems.Add($"{at}.title_pattern must not be empty");
                else
                {
                    try { _ = new Regex(rule.TitlePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout); }
                    catch (ArgumentException) { problems.Add($"{at}.title_pattern is not a valid .NET regular expression"); }
                }
            }
        }
        return problems;
    }

    /// <summary>Compiles validated rules. Throws <see cref="PrivacySettingsException"/> if they are invalid.</summary>
    public static Denylist Compile(IReadOnlyList<DenylistRule> rules)
    {
        var problems = Validate(rules);
        if (problems.Count > 0) throw new PrivacySettingsException(problems);
        return new Denylist(rules.Select(r => new CompiledRule(
            r.Name,
            r.ProcessName is null ? null : NormaliseProcessName(r.ProcessName),
            r.WindowClass?.Trim(),
            r.TitlePattern is null ? null : new Regex(r.TitlePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout)))
            .ToArray());
    }

    /// <summary>The name of the first rule the window matches, or null.</summary>
    public string? Match(string? processName, string? windowClass, string? title)
    {
        string? process = processName is null ? null : NormaliseProcessName(processName);
        foreach (var rule in rules)
            if (rule.Matches(process, windowClass?.Trim(), title)) return rule.Name;
        return null;
    }

    /// <summary>Executable name without folders and without a trailing <c>.exe</c>.</summary>
    internal static string NormaliseProcessName(string value)
    {
        string name = value.Trim();
        int slash = name.LastIndexOfAny(['\\', '/']);
        if (slash >= 0) name = name[(slash + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name;
    }

    private sealed record CompiledRule(string Name, string? Process, string? WindowClass, Regex? Title)
    {
        public bool Matches(string? process, string? windowClass, string? title)
        {
            if (Process is not null && !string.Equals(Process, process, StringComparison.OrdinalIgnoreCase)) return false;
            if (WindowClass is not null && !string.Equals(WindowClass, windowClass, StringComparison.OrdinalIgnoreCase)) return false;
            if (Title is not null)
            {
                if (title is null) return false;
                try { if (!Title.IsMatch(title)) return false; }
                catch (RegexMatchTimeoutException) { return true; } // cannot tell: treat as denylisted
            }
            return true;
        }
    }
}

/// <summary>Fills denylisted windows inside a captured region with opaque black.</summary>
public static class Blackout
{
    /// <summary>
    /// Maps <paramref name="window"/> (screen pixels) into <paramref name="image"/>, which shows <paramref name="region"/>,
    /// clips it to the image and fills it with opaque black (B=G=R=0, A=255). Edges are rounded outwards so a scaled
    /// image never leaves a partial row or column of the window visible. Returns false if the window lies outside.
    /// </summary>
    public static bool Fill(BgraImage image, ScreenRect region, ScreenRect window)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (region.IsEmpty) throw new ArgumentException("The region is empty.", nameof(region));
        var clip = window.Intersect(region);
        if (clip.IsEmpty) return false;

        int x0 = Scale(clip.X - (long)region.X, image.Width, region.Width, ceiling: false);
        int x1 = Scale(clip.Right - region.X, image.Width, region.Width, ceiling: true);
        int y0 = Scale(clip.Y - (long)region.Y, image.Height, region.Height, ceiling: false);
        int y1 = Scale(clip.Bottom - region.Y, image.Height, region.Height, ceiling: true);
        if (x1 <= x0 || y1 <= y0) return false;

        for (int y = y0; y < y1; y++)
        {
            var row = image.Pixels.AsSpan(y * image.Stride + x0 * 4, (x1 - x0) * 4);
            for (int i = 0; i < row.Length; i += 4)
            {
                row[i] = 0; row[i + 1] = 0; row[i + 2] = 0; row[i + 3] = 255;
            }
        }
        return true;
    }

    private static int Scale(long offset, int imageSize, int regionSize, bool ceiling)
    {
        long scaled = offset * imageSize;
        long value = ceiling ? (scaled + regionSize - 1) / regionSize : scaled / regionSize;
        return (int)Math.Clamp(value, 0, imageSize);
    }
}
