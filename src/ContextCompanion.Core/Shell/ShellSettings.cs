using System.Globalization;

namespace ContextCompanion.Core.Shell;

/// <summary>
/// Settings of the tray app: where the inbox is, the hotkeys, the optional horizontal-wheel gesture and the preview.
/// Stored as the <c>app</c> section of the settings file.
/// </summary>
public sealed record ShellSettings
{
    /// <summary>The inbox folder. Null means the default, <c>%USERPROFILE%\ContextCompanion\inbox</c>.</summary>
    public string? Inbox { get; set; }

    /// <summary>Show each capture, after the privacy rules, before it is queued. Default on.</summary>
    public bool Preview { get; set; } = true;

    public HotkeySettings Hotkeys { get; set; } = new();

    /// <summary>
    /// Tilting the mouse wheel left captures the foreground window and right captures a region around the pointer.
    /// It needs a low-level mouse hook, so it is off by default.
    /// </summary>
    public bool WheelGesture { get; set; }

    /// <summary>Share of the monitor's width and height that the pointer region covers. Default 0.4.</summary>
    public double PointerRegionFraction { get; set; } = 0.4;

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Inbox is { } inbox && (inbox.Length is 0 or > 1024 || inbox.Any(char.IsControl)))
            problems.Add("app.inbox must be a folder path of 1 to 1024 characters");
        if (Hotkeys is null) problems.Add("app.hotkeys must be an object");
        else problems.AddRange(Hotkeys.Validate());
        if (!(PointerRegionFraction >= 0.1 && PointerRegionFraction <= 1))
            problems.Add("app.pointer_region_fraction must be from 0.1 to 1");
        return problems;
    }
}

/// <summary>Global hotkeys, written like <c>Ctrl+Alt+W</c>. An empty string switches a hotkey off.</summary>
public sealed record HotkeySettings
{
    public string Window { get; set; } = "Ctrl+Alt+W";
    public string Region { get; set; } = "Ctrl+Alt+R";
    public string Text { get; set; } = "Ctrl+Alt+T";

    public IEnumerable<(string Name, string Value)> All()
    {
        yield return ("window", Window);
        yield return ("region", Region);
        yield return ("text", Text);
    }

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        var seen = new Dictionary<HotkeyChord, string>();
        foreach (var (name, value) in All())
        {
            if (value is null) { problems.Add($"app.hotkeys.{name} must be a string"); continue; }
            if (value.Length == 0) continue;
            if (!HotkeyChord.TryParse(value, out var chord, out string? problem)) { problems.Add($"app.hotkeys.{name}: {problem}"); continue; }
            if (seen.TryGetValue(chord, out var other)) problems.Add($"app.hotkeys.{name} is the same as app.hotkeys.{other}");
            else seen[chord] = name;
        }
        return problems;
    }
}

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// A hotkey: modifiers and one key, with Windows virtual-key codes. At least one of Ctrl, Alt or Win is required, so
/// a hotkey never takes over ordinary typing.
/// </summary>
public readonly record struct HotkeyChord(HotkeyModifiers Modifiers, int VirtualKey)
{
    private static readonly Dictionary<string, int> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20, ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["End"] = 0x23, ["Home"] = 0x24,
        ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["PrintScreen"] = 0x2C, ["Pause"] = 0x13,
    };

    public static HotkeyChord Parse(string text) =>
        TryParse(text, out var chord, out string? problem) ? chord : throw new FormatException(problem);

    public static bool TryParse(string text, out HotkeyChord chord, out string? problem)
    {
        chord = default;
        problem = null;
        var parts = (text ?? "").Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts.Any(p => p.Length == 0)) { problem = "write a hotkey as modifiers and a key, such as Ctrl+Alt+W"; return false; }
        var modifiers = HotkeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            var modifier = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => HotkeyModifiers.Ctrl,
                "alt" => HotkeyModifiers.Alt,
                "shift" => HotkeyModifiers.Shift,
                "win" or "windows" => HotkeyModifiers.Win,
                _ => HotkeyModifiers.None,
            };
            if (modifier == HotkeyModifiers.None) { problem = $"'{Clip(part)}' is not a modifier (use Ctrl, Alt, Shift or Win)"; return false; }
            if ((modifiers & modifier) != 0) { problem = $"{modifier} appears twice"; return false; }
            modifiers |= modifier;
        }
        if ((modifiers & (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Win)) == 0)
        {
            problem = "a hotkey needs Ctrl, Alt or Win, so that it does not take over ordinary typing";
            return false;
        }
        string key = parts[^1];
        int vk;
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) vk = char.ToUpperInvariant(key[0]);
        else if (key.Length is 2 or 3 && (key[0] is 'F' or 'f') && int.TryParse(key[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int f) && f is >= 1 and <= 24)
            vk = 0x70 + f - 1;
        else if (!NamedKeys.TryGetValue(key, out vk)) { problem = $"'{Clip(key)}' is not a supported key (A-Z, 0-9, F1-F24, Space, Insert, Delete, Home, End, PageUp, PageDown, PrintScreen, Pause)"; return false; }
        chord = new HotkeyChord(modifiers, vk);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        int vk = VirtualKey;
        string key = vk switch
        {
            >= 0x70 and <= 0x87 => "F" + (vk - 0x70 + 1).ToString(CultureInfo.InvariantCulture),
            _ when NamedKeys.FirstOrDefault(k => k.Value == vk).Key is { } name => name,
            _ => ((char)vk).ToString(),
        };
        parts.Add(key);
        return string.Join("+", parts);
    }

    private static string Clip(string text) => text.Length <= 24 ? text : text[..24] + "...";
}
