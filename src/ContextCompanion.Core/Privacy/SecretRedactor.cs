using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ContextCompanion.Core.Packets;

namespace ContextCompanion.Core.Privacy;

/// <summary>
/// One secret pattern. If the regular expression defines a group named <c>secret</c>, only that group is replaced
/// (so <c>password=hunter2</c> keeps its name); otherwise the whole match is.
/// </summary>
public sealed record SecretPattern
{
    /// <summary>Name used in <c>[REDACTED:&lt;name&gt;]</c> and in redaction records: a lower-case token.</summary>
    public string Name { get; set; } = "";
    /// <summary>A .NET regular expression. Inline options such as <c>(?i)</c> are allowed.</summary>
    public string Regex { get; set; } = "";
    /// <summary>What the pattern is for, for the person editing the list. Never written anywhere else.</summary>
    public string? Description { get; set; }
}

/// <summary>A versioned list of secret patterns, the format of the built-in list.</summary>
public sealed record SecretPatternFile
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; }
    public List<SecretPattern> Patterns { get; set; } = [];

    /// <summary>The built-in patterns, shipped as data with Core.</summary>
    public static SecretPatternFile BuiltIn() => Parse(BuiltInBytes.Value);

    private static readonly Lazy<byte[]> BuiltInBytes = new(ReadResource);

    public static SecretPatternFile Parse(ReadOnlySpan<byte> json)
    {
        SecretPatternFile? file;
        try { file = JsonSerializer.Deserialize<SecretPatternFile>(json, PacketJson.Options); }
        catch (JsonException ex) { throw new PrivacySettingsException(["the pattern list is not valid: " + ex.Message]); }
        if (file is null) throw new PrivacySettingsException(["the pattern list is empty"]);
        var problems = new List<string>();
        if (file.SchemaVersion != CurrentSchemaVersion) problems.Add($"the pattern list's schema_version must be {CurrentSchemaVersion}");
        problems.AddRange(SecretRedactor.Validate(file.Patterns, "patterns"));
        if (problems.Count > 0) throw new PrivacySettingsException(problems);
        return file;
    }

    private static byte[] ReadResource()
    {
        using var stream = typeof(SecretPatternFile).Assembly.GetManifestResourceStream("ContextCompanion.Core.Privacy.secret-patterns.json")
            ?? throw new InvalidOperationException("The built-in secret pattern resource is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}

/// <summary>How many matches of one pattern were replaced.</summary>
public sealed record SecretPatternCount(string Pattern, int Count);

/// <summary>Redacted text and what was replaced. Never holds the removed text.</summary>
public sealed record SecretRedactionResult(string Text, IReadOnlyList<SecretPatternCount> Counts)
{
    public int Total => Counts.Sum(c => c.Count);
}

/// <summary>A pattern ran longer than its timeout, so the text cannot be shown to be free of that pattern.</summary>
public sealed class SecretScanTimeoutException(string pattern)
    : Exception($"The secret pattern '{pattern}' did not finish in time.")
{
    public string Pattern { get; } = pattern;
}

/// <summary>
/// Replaces secret-shaped text with <c>[REDACTED:&lt;pattern&gt;]</c>. Matches of all patterns are found in the original
/// text; overlapping matches are merged into one replacement named after the pattern that starts first (or, for equal
/// starts, the longer match, then the earlier pattern in the list). Text already inside a <c>[REDACTED:…]</c> marker is
/// left alone, so redacting twice changes nothing more.
/// </summary>
public sealed partial class SecretRedactor
{
    /// <summary>How long one pattern may run over one text.</summary>
    public static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(250);
    private const string SecretGroup = "secret";

    private readonly (string Name, Regex Regex, bool HasSecretGroup)[] patterns;

    public SecretRedactor(IEnumerable<SecretPattern> patterns)
    {
        var list = patterns.ToList();
        var problems = Validate(list, "patterns");
        if (problems.Count > 0) throw new PrivacySettingsException(problems);
        this.patterns = list.Select(p =>
        {
            var regex = Compile(p.Regex, compiled: true);
            return (p.Name, regex, regex.GetGroupNames().Contains(SecretGroup));
        }).ToArray();
    }

    /// <summary>The built-in patterns plus the settings' extra patterns, minus the disabled ones.</summary>
    public static SecretRedactor FromSettings(RedactionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var disabled = settings.DisabledPatterns.ToHashSet(StringComparer.Ordinal);
        return new SecretRedactor(SecretPatternFile.BuiltIn().Patterns.Where(p => !disabled.Contains(p.Name)).Concat(settings.ExtraPatterns));
    }

    public IReadOnlyList<string> PatternNames => patterns.Select(p => p.Name).ToArray();

    [GeneratedRegex(@"\[REDACTED:[a-z][a-z0-9-]{0,31}\]", RegexOptions.CultureInvariant)]
    private static partial Regex Marker();

    /// <summary>Redacts <paramref name="text"/>. Throws <see cref="SecretScanTimeoutException"/> if a pattern times out.</summary>
    public SecretRedactionResult Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var markers = Marker().Matches(text).Select(m => (Start: m.Index, End: m.Index + m.Length)).ToArray();
        var spans = new List<(int Start, int End, int Pattern)>();
        for (int p = 0; p < patterns.Length; p++)
        {
            var (name, regex, hasGroup) = patterns[p];
            try
            {
                foreach (Match match in regex.Matches(text))
                {
                    if (hasGroup && match.Groups[SecretGroup] is { Success: true } group)
                    {
                        foreach (Capture capture in group.Captures) Add(capture.Index, capture.Length, p);
                    }
                    else Add(match.Index, match.Length, p);
                }
            }
            catch (RegexMatchTimeoutException) { throw new SecretScanTimeoutException(name); }
        }
        if (spans.Count == 0) return new SecretRedactionResult(text, []);

        spans.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start)
            : a.End != b.End ? b.End.CompareTo(a.End) : a.Pattern.CompareTo(b.Pattern));
        var counts = new int[patterns.Length];
        var output = new StringBuilder(text.Length);
        int written = 0;
        int i = 0;
        while (i < spans.Count)
        {
            var (start, end, pattern) = spans[i++];
            while (i < spans.Count && spans[i].Start < end) end = Math.Max(end, spans[i++].End);
            output.Append(text, written, start - written).Append("[REDACTED:").Append(patterns[pattern].Name).Append(']');
            written = end;
            counts[pattern]++;
        }
        output.Append(text, written, text.Length - written);
        var result = counts.Select((c, p) => new SecretPatternCount(patterns[p].Name, c)).Where(c => c.Count > 0).ToArray();
        return new SecretRedactionResult(output.ToString(), result);

        // Adds the parts of a match that lie outside existing markers: text already redacted stays as it is, and
        // anything around it is still replaced.
        void Add(int start, int length, int pattern)
        {
            int end = start + length;
            foreach (var marker in markers)
            {
                if (marker.End <= start || marker.Start >= end) continue;
                if (marker.Start > start) spans.Add((start, marker.Start, pattern));
                start = Math.Max(start, marker.End);
                if (start >= end) return;
            }
            if (end > start) spans.Add((start, end, pattern));
        }
    }

    /// <summary>Problems with a list of patterns; empty when it is valid. Messages name patterns, never text.</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<SecretPattern>? patterns, string at)
    {
        var problems = new List<string>();
        if (patterns is null) { problems.Add($"{at} must be an array"); return problems; }
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < patterns.Count; i++)
        {
            var pattern = patterns[i];
            string where = $"{at}[{i}]";
            if (pattern is null) { problems.Add($"{where} must be an object"); continue; }
            if (!PacketNames.IsValidToken(pattern.Name))
                problems.Add($"{where}.name must be a lower-case token of 1 to 32 characters (a-z, 0-9, '-')");
            else if (!names.Add(pattern.Name))
                problems.Add($"{where}.name '{pattern.Name}' is used more than once");
            if (string.IsNullOrEmpty(pattern.Regex)) { problems.Add($"{where}.regex must not be empty"); continue; }
            try
            {
                var regex = Compile(pattern.Regex, compiled: false);
                if (regex.IsMatch("")) problems.Add($"{where}.regex must not match empty text");
            }
            catch (ArgumentException) { problems.Add($"{where}.regex is not a valid .NET regular expression"); }
            catch (RegexMatchTimeoutException) { problems.Add($"{where}.regex timed out on empty text"); }
        }
        return problems;
    }

    private static Regex Compile(string pattern, bool compiled) =>
        new(pattern, RegexOptions.CultureInvariant | (compiled ? RegexOptions.Compiled : RegexOptions.None), PatternTimeout);
}
