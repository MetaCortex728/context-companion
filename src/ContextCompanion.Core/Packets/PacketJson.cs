using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ContextCompanion.Core.Packets;

/// <summary>JSON settings shared by manifests, acknowledgements, the outbox state and the audit log.</summary>
public static class PacketJson
{
    public static JsonSerializerOptions Options { get; } = Create(indented: true);
    public static JsonSerializerOptions Compact { get; } = Create(indented: false);

    public static JsonDocumentOptions DocumentOptions { get; } = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16,
        AllowDuplicateProperties = false,
    };

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = indented,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false,
            NewLine = "\n",
            // Source-generated metadata: no reflection, so Core also works trimmed or AOT-compiled.
            TypeInfoResolver = CoreJsonContext.Default,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>UTC timestamp text with millisecond precision, for example <c>2026-11-03T15:04:05.123Z</c>.</summary>
    public static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Parses an ISO 8601 UTC timestamp that ends in <c>Z</c>.</summary>
    public static bool TryParseTimestamp(string? text, out DateTimeOffset value)
    {
        value = default;
        if (text is null || text.Length < 20 || text.Length > 40 || text[^1] != 'Z' || text[10] != 'T') return false;
        return DateTimeOffset.TryParseExact(text,
            ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
    }

    /// <summary>Truncates a time to whole milliseconds, the precision that packets record.</summary>
    public static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value) =>
        DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds());
}

/// <summary>Source-generated JSON metadata for every type Core serialises.</summary>
[JsonSerializable(typeof(PacketManifest))]
[JsonSerializable(typeof(PacketAcknowledgement))]
[JsonSerializable(typeof(Delivery.OutboxStateFile))]
[JsonSerializable(typeof(Delivery.AuditEntry))]
internal sealed partial class CoreJsonContext : JsonSerializerContext;

/// <summary>Writes and reads <see cref="DateTimeOffset"/> as UTC ISO 8601 ending in <c>Z</c>.</summary>
public sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        return PacketJson.TryParseTimestamp(text, out var value)
            ? value
            : throw new JsonException("Expected a UTC ISO 8601 timestamp ending in 'Z'.");
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(PacketJson.FormatTimestamp(value));
}

/// <summary>Same as <see cref="UtcTimestampConverter"/>, for nullable values.</summary>
public sealed class NullableUtcTimestampConverter : JsonConverter<DateTimeOffset?>
{
    private static readonly UtcTimestampConverter Inner = new();
    public override bool HandleNull => false;

    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Inner.Read(ref reader, typeof(DateTimeOffset), options);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is { } v) Inner.Write(writer, v, options);
        else writer.WriteNullValue();
    }
}
