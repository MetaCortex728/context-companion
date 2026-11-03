using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContextCompanion.Core.Packets;

namespace ContextCompanion.Core.Delivery;

/// <summary>
/// One line of the audit log. By construction it holds only identifiers, state names, sizes and
/// hashes: never payload content, window titles, notes or any other text from a packet.
/// </summary>
public sealed record AuditEntry
{
    [JsonConverter(typeof(UtcTimestampConverter))]
    public required DateTimeOffset Time { get; init; }
    public required string Packet { get; init; }
    public required string Destination { get; init; }
    /// <summary>Previous state, or null when the packet entered the outbox.</summary>
    public string? From { get; init; }
    /// <summary>New state, or <c>discarded</c> when the user removed the packet from the outbox.</summary>
    public required string To { get; init; }
    public required string ContentSha256 { get; init; }
    public required IReadOnlyList<string> ItemSha256 { get; init; }
    public required long Bytes { get; init; }
    /// <summary>The consumer name from <c>ack.json</c>, for <c>consumed</c> only (a restricted identifier, not free text).</summary>
    public string? Consumer { get; init; }
}

/// <summary>
/// Append-only audit log in JSON Lines: one entry per state change, flushed to disk as it is written.
/// The log is never rewritten by context-companion.
/// </summary>
public sealed class AuditLog
{
    public const string Discarded = "discarded";
    private readonly Lock gate = new();
    private readonly TimeProvider time;

    public AuditLog(string path, TimeProvider? time = null)
    {
        Path = System.IO.Path.GetFullPath(path);
        this.time = time ?? TimeProvider.System;
    }

    public string Path { get; }

    internal void Record(OutboxRecord record, DeliveryState? from, string to)
    {
        var entry = new AuditEntry
        {
            Time = PacketJson.TruncateToMilliseconds(time.GetUtcNow()),
            Packet = record.Id,
            Destination = record.Destination,
            From = from is { } f ? DeliveryTransitions.Name(f) : null,
            To = to,
            ContentSha256 = record.ContentSha256,
            ItemSha256 = record.ItemSha256,
            Bytes = record.Bytes,
            Consumer = to == "consumed" ? record.Consumer : null,
        };
        Append(entry);
    }

    private void Append(AuditEntry entry)
    {
        byte[] line = JsonSerializer.SerializeToUtf8Bytes(entry, PacketJson.Compact);
        lock (gate)
        {
            string? directory = System.IO.Path.GetDirectoryName(Path);
            if (directory is not null) Directory.CreateDirectory(directory);
            using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
            if (stream.Position > 0 && !EndsWithNewline(Path))
                stream.WriteByte((byte)'\n'); // isolate a line truncated by an earlier crash
            stream.Write(line);
            stream.WriteByte((byte)'\n');
            stream.Flush(flushToDisk: true);
        }
    }

    private static bool EndsWithNewline(string path)
    {
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (reader.Length == 0) return true;
        reader.Seek(-1, SeekOrigin.End);
        return reader.ReadByte() == '\n';
    }

    /// <summary>Reads all complete entries. A line cut short by a crash is skipped.</summary>
    public static IReadOnlyList<AuditEntry> Read(string path)
    {
        if (!File.Exists(path)) return [];
        var entries = new List<AuditEntry>();
        foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
        {
            if (line.Length == 0) continue;
            try
            {
                if (JsonSerializer.Deserialize<AuditEntry>(line, PacketJson.Compact) is { } entry) entries.Add(entry);
            }
            catch (JsonException) { }
        }
        return entries;
    }
}
