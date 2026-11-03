using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContextCompanion.Core.Packets;

namespace ContextCompanion.Core.Privacy;

/// <summary>
/// The privacy settings, stored as JSON (snake_case, versioned, unknown properties rejected).
/// A property left out of the file keeps its default. Defaults are documented in PRIVACY.md.
/// </summary>
public sealed record PrivacySettings
{
    public const int CurrentSchemaVersion = 1;
    /// <summary>Settings files larger than this are refused.</summary>
    public const int MaxFileBytes = 1024 * 1024;

    /// <summary>Format version of the settings file. Required; must be 1.</summary>
    [JsonRequired]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Which source details may be written into packets.</summary>
    public MetadataSettings Metadata { get; set; } = new();

    /// <summary>
    /// Windows whose content is never captured: a matching captured window is refused, and a matching window inside a
    /// captured region is blacked out. Left out of the file, the default list of credential-handling applications
    /// applies; an empty array switches the denylist off.
    /// </summary>
    public List<DenylistRule> Denylist { get; set; } = DefaultDenylist();

    /// <summary>The default denylist: common password managers and Windows credential prompts.</summary>
    public static List<DenylistRule> DefaultDenylist()
    {
        using var stream = typeof(PrivacySettings).Assembly.GetManifestResourceStream("ContextCompanion.Core.Privacy.denylist.json")
            ?? throw new InvalidOperationException("The default denylist resource is missing.");
        return JsonSerializer.Deserialize<List<DenylistRule>>(stream, PacketJson.Options)
            ?? throw new InvalidOperationException("The default denylist resource is empty.");
    }

    /// <summary>
    /// The settings used when there is no settings file: a new instance each time, so changing one never changes
    /// another. (Settings use setters rather than init-only properties because the source-generated JSON reader
    /// would otherwise reset properties missing from a file to zero instead of keeping their defaults.)
    /// </summary>
    public static PrivacySettings Default => new();

    /// <summary>Parses and validates settings. Throws <see cref="PrivacySettingsException"/> when they are invalid.</summary>
    public static PrivacySettings Parse(string json) => Parse(Encoding.UTF8.GetBytes(json));

    /// <inheritdoc cref="Parse(string)"/>
    public static PrivacySettings Parse(ReadOnlySpan<byte> json)
    {
        if (json.Length > MaxFileBytes) throw new PrivacySettingsException([$"the settings file is larger than {MaxFileBytes} bytes"]);
        PrivacySettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize<PrivacySettings>(json, PacketJson.Options);
        }
        catch (JsonException ex)
        {
            // System.Text.Json messages name the JSON path and expected type, never the value.
            throw new PrivacySettingsException(["the settings file is not valid: " + ex.Message]);
        }
        if (settings is null) throw new PrivacySettingsException(["the settings file is empty"]);
        settings.ThrowIfInvalid();
        return settings;
    }

    /// <summary>Loads settings from a file, or returns <see cref="Default"/> when the file does not exist.</summary>
    public static PrivacySettings LoadOrDefault(string path) =>
        File.Exists(path) ? Parse(File.ReadAllBytes(path)) : Default;

    /// <summary>The settings as indented JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, PacketJson.Options);

    /// <summary>Problems with these settings; empty when they are valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion) problems.Add($"schema_version must be {CurrentSchemaVersion}");
        if (Metadata is null) problems.Add("metadata must be an object");
        problems.AddRange(Privacy.Denylist.Validate(Denylist));
        return problems;
    }

    public void ThrowIfInvalid()
    {
        var problems = Validate();
        if (problems.Count > 0) throw new PrivacySettingsException(problems);
    }
}

/// <summary>
/// Source details that may be written into a packet's <c>source</c>. A field that is off is dropped before
/// anything else sees the request's metadata: it never appears in a manifest, the audit log or a message.
/// </summary>
public sealed record MetadataSettings
{
    /// <summary>Executable name, for example <c>notepad.exe</c>. Default on.</summary>
    public bool ProcessName { get; set; } = true;
    /// <summary>Window class name. Default off.</summary>
    public bool WindowClass { get; set; }
    /// <summary>Window title, which often names documents, people or conversations. Default off.</summary>
    public bool WindowTitle { get; set; }
    /// <summary>Full executable path, which often contains the user name. Default off.</summary>
    public bool ExecutablePath { get; set; }
    /// <summary>Capture mode such as <c>window</c> or <c>region</c>. Default on.</summary>
    public bool CaptureMode { get; set; } = true;
    /// <summary>Captured rectangle in screen pixels. Default on.</summary>
    public bool Region { get; set; } = true;
    /// <summary>Display scale factor. Default on.</summary>
    public bool Scale { get; set; } = true;
}

/// <summary>Invalid privacy settings. Messages name settings and rules, never captured content.</summary>
public sealed class PrivacySettingsException(IReadOnlyList<string> problems)
    : Exception("Invalid privacy settings: " + string.Join("; ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}
