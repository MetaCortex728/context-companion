using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContextCompanion.Core.Packets;

namespace ContextCompanion.Core.Delivery;

/// <summary>
/// One line of the audit log. By construction it holds only identifiers, state names, sizes, hashes and the names
/// of rules and reasons chosen by the tool: never payload content, window titles, process names, paths, notes or any
/// other text from a capture or a packet.
/// </summary>
public sealed record AuditEntry
{
    [JsonConverter(typeof(UtcTimestampConverter))]
    public required DateTimeOffset Time { get; init; }
    /// <summary>Packet id. Absent for a refused capture, which never became a packet.</summary>
    public string? Packet { get; init; }
    /// <summary>Destination name. Absent for a refused capture.</summary>
    public string? Destination { get; init; }
    /// <summary>Previous state, or null when the packet entered the outbox.</summary>
    public string? From { get; init; }
    /// <summary>
    /// New state; or <c>discarded</c> (removed from the outbox by the user), <c>blocked</c> (capture refused by a
    /// denylist rule) or <c>removed</c> (deleted from an inbox by retention or purge).
    /// </summary>
    public required string To { get; init; }
    public string? ContentSha256 { get; init; }
    public IReadOnlyList<string>? ItemSha256 { get; init; }
    public long? Bytes { get; init; }
    /// <summary>The consumer name from <c>ack.json</c>, for <c>consumed</c> only (a restricted identifier, not free text).</summary>
    public string? Consumer { get; init; }
    /// <summary>For <c>blocked</c>: the denylist rule's name.</summary>
    public string? Rule { get; init; }
    /// <summary>For <c>removed</c>: a fixed reason token such as <c>retention-max-age</c> or <c>purge-all</c>.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Append-only audit log in JSON Lines: one entry per state change, flushed to disk as it is written.
/// The log is never rewritten by context-companion.
/// </summary>
public sealed class AuditLog
{
    public const string Discarded = "discarded";
    public const string Blocked = "blocked";
    public const string Removed = "removed";
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
            Time = Now(),
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

    /// <summary>Records a capture refused by a denylist rule: the time and the rule's name, nothing else.</summary>
    public void RecordBlocked(string rule)
    {
        if (!PacketNames.IsValidAgentName(rule)) throw new ArgumentException("Invalid rule name.", nameof(rule));
        Append(new AuditEntry { Time = Now(), To = Blocked, Rule = rule });
    }

    /// <summary>Records that a packet was deleted: its id, the destination it was in and a reason token.</summary>
    public void RecordRemoval(string packet, string? destination, string reason)
    {
        if (!Ulid.IsValid(packet)) throw new ArgumentException("Invalid packet id.", nameof(packet));
        if (destination is not null && !PacketNames.IsValidAgentName(destination)) throw new ArgumentException("Invalid destination name.", nameof(destination));
        if (!PacketNames.IsValidToken(reason)) throw new ArgumentException("Invalid reason.", nameof(reason));
        Append(new AuditEntry { Time = Now(), Packet = packet, Destination = destination, To = Removed, Reason = reason });
    }

    private DateTimeOffset Now() => PacketJson.TruncateToMilliseconds(time.GetUtcNow());

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
