using System.Text.Json.Serialization;

namespace ContextCompanion.Core.Packets;

/// <summary>Packet format v1 limits and fixed names. See docs/packet-format.md.</summary>
public static class PacketFormat
{
    public const string FormatName = "context-companion.packet";
    public const string AckFormatName = "context-companion.ack";
    public const int SchemaVersion = 1;
    public const string ManifestFileName = "manifest.json";
    public const string AckFileName = "ack.json";
    /// <summary>Staging folder inside an inbox; never a packet.</summary>
    public const string StagingDirectoryName = ".tmp";

    public const int MaxItems = 32;
    public const long MaxPacketBytes = 64L * 1024 * 1024;
    public const int MaxManifestBytes = 1024 * 1024;
    public const int MaxAckBytes = 64 * 1024;
    public const int MaxFileNameLength = 100;
    public const int MaxProcessNameLength = 260;
    public const int MaxWindowTitleLength = 1024;
    public const int MaxWindowClassLength = 256;
    public const int MaxExecutablePathLength = 32767;
    public const int MaxRedactions = 256;
    public const int MaxRedactionReasonLength = 500;
    public const int MaxNoteLength = 1000;
    public const int MaxConsumerLength = 64;
    public const double MaxScale = 16;
}

[JsonConverter(typeof(JsonStringEnumConverter<PacketItemKind>))]
public enum PacketItemKind
{
    [JsonStringEnumMemberName("image")] Image,
    [JsonStringEnumMemberName("text")] Text,
    [JsonStringEnumMemberName("audio")] Audio,
    [JsonStringEnumMemberName("note")] Note,
}

/// <summary>The <c>manifest.json</c> of a packet.</summary>
public sealed record PacketManifest
{
    public string Format { get; init; } = PacketFormat.FormatName;
    public int SchemaVersion { get; init; } = PacketFormat.SchemaVersion;
    public required string Id { get; init; }
    [JsonConverter(typeof(UtcTimestampConverter))]
    public required DateTimeOffset Created { get; init; }
    public required IReadOnlyList<PacketItem> Items { get; init; }
    public PacketSource? Source { get; init; }
    public IReadOnlyList<PacketRedaction> Redactions { get; init; } = [];

    /// <summary>Total payload size in bytes.</summary>
    [JsonIgnore]
    public long TotalBytes => Items.Sum(i => i.Bytes);
}

/// <summary>One payload file. <see cref="File"/> is a plain file name inside the packet, never a path.</summary>
public sealed record PacketItem
{
    public required PacketItemKind Kind { get; init; }
    public required string File { get; init; }
    public required string MediaType { get; init; }
    public required long Bytes { get; init; }
    /// <summary>Lower-case hexadecimal SHA-256 of the file.</summary>
    public required string Sha256 { get; init; }
}

/// <summary>Where the content came from. Every field is optional and may be switched off.</summary>
public sealed record PacketSource
{
    public string? ProcessName { get; init; }
    public string? WindowClass { get; init; }
    public string? WindowTitle { get; init; }
    public string? ExecutablePath { get; init; }
    public string? CaptureMode { get; init; }
    public PacketRegion? Region { get; init; }
    public double? Scale { get; init; }
}

/// <summary>A captured rectangle in physical screen pixels.</summary>
public sealed record PacketRegion(int X, int Y, int Width, int Height);

/// <summary>
/// What was removed and why. Never carries the removed content itself.
/// </summary>
public sealed record PacketRedaction
{
    /// <summary>Category, for example <c>secret</c> or <c>denylisted-window</c>.</summary>
    public required string Kind { get; init; }
    /// <summary>Human-readable reason, written by the tool, without the removed content.</summary>
    public required string Reason { get; init; }
    /// <summary>The item the redaction applies to, if any.</summary>
    public string? Item { get; init; }
    /// <summary>How many separate removals, if counted.</summary>
    public int? Count { get; init; }
}

/// <summary>A consumer's acknowledgement, <c>ack.json</c>, written into the packet folder.</summary>
public sealed record PacketAcknowledgement
{
    public string Format { get; init; } = PacketFormat.AckFormatName;
    public int SchemaVersion { get; init; } = PacketFormat.SchemaVersion;
    public required string Id { get; init; }
    public required string Consumer { get; init; }
    [JsonConverter(typeof(UtcTimestampConverter))]
    public required DateTimeOffset Consumed { get; init; }
    public string? Note { get; init; }
}
