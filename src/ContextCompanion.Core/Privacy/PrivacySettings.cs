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

    /// <summary>Secret redaction for captured text and notes.</summary>
    public RedactionSettings Redaction { get; set; } = new();

    /// <summary>What each inbox keeps; applied after every delivery and by <c>ccomp purge</c>.</summary>
    public RetentionSettings Retention { get; set; } = new();

    /// <summary>Encryption of payload files at rest. Off by default.</summary>
    public EncryptionSettings Encryption { get; set; } = new();

    /// <summary>How window, region and text captures are taken: image size, the pointer, the explicit copy.</summary>
    public Capturing.CaptureSettings Capture { get; set; } = new();

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
        if (Retention is null) problems.Add("retention must be an object");
        else problems.AddRange(Retention.Validate());
        if (Encryption is null) problems.Add("encryption must be an object");
        else if (!PacketNames.IsValidToken(Encryption.Protection)) problems.Add("encryption.protection must be a lower-case token such as dpapi-current-user");
        if (Redaction is null) problems.Add("redaction must be an object");
        else problems.AddRange(Redaction.Validate());
        if (Capture is null) problems.Add("capture must be an object");
        else problems.AddRange(Capture.Validate());
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

/// <summary>
/// Secret redaction. The built-in patterns (a versioned JSON list shipped with Core, documented in PRIVACY.md) run on
/// every captured text and note unless switched off; <see cref="ExtraPatterns"/> adds patterns of the same shape.
/// </summary>
public sealed record RedactionSettings
{
    /// <summary>Redact secret-shaped text. Default on.</summary>
    public bool Secrets { get; set; } = true;
    /// <summary>Names of built-in patterns to skip. Default none.</summary>
    public List<string> DisabledPatterns { get; set; } = [];
    /// <summary>Additional patterns, run after the built-in ones. Default none.</summary>
    public List<SecretPattern> ExtraPatterns { get; set; } = [];

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (DisabledPatterns is null || ExtraPatterns is null)
        {
            problems.Add("redaction.disabled_patterns and redaction.extra_patterns must be arrays");
            return problems;
        }
        var builtIn = SecretPatternFile.BuiltIn().Patterns.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (string? name in DisabledPatterns)
            if (name is null || !builtIn.Contains(name))
                problems.Add($"redaction.disabled_patterns names an unknown pattern{(PacketNames.IsValidToken(name) ? $" '{name}'" : "")}");
        problems.AddRange(SecretRedactor.Validate(ExtraPatterns, "redaction.extra_patterns"));
        foreach (var pattern in ExtraPatterns)
            if (pattern is not null && builtIn.Contains(pattern.Name) && !DisabledPatterns.Contains(pattern.Name))
                problems.Add($"redaction.extra_patterns: '{pattern.Name}' is a built-in pattern name; disable the built-in one to replace it");
        return problems;
    }
}

/// <summary>
/// Per-inbox retention limits. Null switches a limit off. Packets still queued or being delivered are never removed;
/// consumed packets go first, then the oldest.
/// </summary>
public sealed record RetentionSettings
{
    /// <summary>Most packets an inbox keeps. Default 200.</summary>
    public int? MaxPackets { get; set; } = 200;
    /// <summary>Most bytes an inbox keeps. Default 536870912 (512 MiB).</summary>
    public long? MaxBytes { get; set; } = 512L * 1024 * 1024;
    /// <summary>Oldest packet an inbox keeps, as a duration such as <c>7d</c>. Default <c>7d</c>.</summary>
    public string? MaxAge { get; set; } = "7d";

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (MaxPackets is < 1) problems.Add("retention.max_packets must be at least 1, or null for no limit");
        if (MaxBytes is < 1) problems.Add("retention.max_bytes must be at least 1, or null for no limit");
        if (MaxAge is not null && !Duration.TryParse(MaxAge, out _))
            problems.Add("retention.max_age must be a duration such as 12h, 7d or 2w, or null for no limit");
        return problems;
    }

    /// <summary>The limits in the form <see cref="Delivery.InboxRetention"/> takes.</summary>
    public Delivery.RetentionLimits ToLimits() =>
        new(MaxPackets, MaxBytes, MaxAge is null ? null : Duration.Parse(MaxAge));
}

/// <summary>
/// Encryption at rest. When enabled, every payload file of a new packet is stored encrypted (for DPAPI as
/// <c>&lt;file&gt;.dpapi</c>) in the outbox and in the inbox it is delivered to. Off by default, because the inbox is the
/// hand-off point that assistants read by path; consumers then need <c>ccomp open</c> to get a plaintext copy.
/// </summary>
public sealed record EncryptionSettings
{
    /// <summary>Encrypt payload files. Default off.</summary>
    public bool Enabled { get; set; }
    /// <summary>Which protector to use. Default <c>dpapi-current-user</c> (Windows only).</summary>
    public string Protection { get; set; } = PacketFormat.DpapiCurrentUser;
}

/// <summary>Invalid privacy settings. Messages name settings and rules, never captured content.</summary>
public sealed class PrivacySettingsException(IReadOnlyList<string> problems)
    : Exception("Invalid privacy settings: " + string.Join("; ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}
