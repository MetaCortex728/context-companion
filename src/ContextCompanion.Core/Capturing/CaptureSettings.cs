using System.Text.Json;
using System.Text.Json.Serialization;
using ContextCompanion.Core.Packets;

namespace ContextCompanion.Core.Capturing;

/// <summary>
/// How captures are taken: the <c>capture</c> section of the settings file (see <see cref="Privacy.PrivacySettings"/>),
/// versioned with the file's <c>schema_version</c>. A property left out keeps its default. Documented in
/// docs/capture.md.
/// </summary>
public sealed record CaptureSettings
{
    public const int DefaultMaxLongEdge = 1568;
    public const int MinCopyTimeoutMs = 50, MaxCopyTimeoutMs = 5000;

    /// <summary>
    /// Images are downscaled (area averaging, never enlarged) so their long edge is at most this many pixels.
    /// 0 keeps the original size. Default 1568.
    /// </summary>
    public int MaxLongEdge { get; set; } = DefaultMaxLongEdge;

    /// <summary>Include the mouse pointer in window and region captures. Default off.</summary>
    public bool IncludeCursor { get; set; }

    /// <summary>
    /// When no selection can be read through UI Automation, send Ctrl+C to the focused window, read the copied text
    /// and restore the clipboard. Never done in console and terminal windows. Default on.
    /// </summary>
    public bool ExplicitCopy { get; set; } = true;

    /// <summary>How long the explicit copy waits for the clipboard to change, in milliseconds (50-5000). Default 500.</summary>
    public int CopyTimeoutMs { get; set; } = 500;

    /// <summary>
    /// Window classes added to the built-in list of terminals, where Ctrl+C is never sent. The built-in list cannot be
    /// shortened. Default none.
    /// </summary>
    public List<string> ExtraTerminalWindowClasses { get; set; } = [];

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (MaxLongEdge != 0 && MaxLongEdge is < 16 or > Privacy.BgraImage.MaxDimension)
            problems.Add($"capture.max_long_edge must be 0 (keep the original size) or 16 to {Privacy.BgraImage.MaxDimension}");
        if (CopyTimeoutMs is < MinCopyTimeoutMs or > MaxCopyTimeoutMs)
            problems.Add($"capture.copy_timeout_ms must be {MinCopyTimeoutMs} to {MaxCopyTimeoutMs}");
        if (ExtraTerminalWindowClasses is null) problems.Add("capture.extra_terminal_window_classes must be an array");
        else if (ExtraTerminalWindowClasses.Any(c => string.IsNullOrWhiteSpace(c) || c.Length > 256))
            problems.Add("capture.extra_terminal_window_classes entries must be 1 to 256 characters");
        return problems;
    }

    /// <summary>The explicit-copy timeout.</summary>
    [JsonIgnore]
    public TimeSpan CopyTimeout => TimeSpan.FromMilliseconds(CopyTimeoutMs);

    /// <summary>True when Ctrl+C must never be sent to a window of this class: the built-in terminals and the extras.</summary>
    public bool IsTerminalWindowClass(string? windowClass) =>
        windowClass is not null &&
        (TerminalWindowClasses.BuiltIn.Contains(windowClass.Trim()) ||
         (ExtraTerminalWindowClasses ?? []).Any(c => string.Equals(c?.Trim(), windowClass.Trim(), StringComparison.OrdinalIgnoreCase)));
}

/// <summary>The built-in list of console and terminal window classes, shipped as data with Core.</summary>
public static class TerminalWindowClasses
{
    private static readonly Lazy<IReadOnlySet<string>> builtIn = new(Load);

    /// <summary>Window classes compared ignoring case.</summary>
    public static IReadOnlySet<string> BuiltIn => builtIn.Value;

    private static HashSet<string> Load()
    {
        using var stream = typeof(TerminalWindowClasses).Assembly.GetManifestResourceStream("ContextCompanion.Core.Capturing.no-copy-window-classes.json")
            ?? throw new InvalidOperationException("The terminal window class resource is missing.");
        using var document = JsonDocument.Parse(stream, PacketJson.DocumentOptions);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in document.RootElement.GetProperty("window_classes").EnumerateArray())
            set.Add(entry.GetProperty("class").GetString() ?? throw new InvalidOperationException("A terminal window class entry has no class."));
        return set;
    }
}
