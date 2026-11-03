using System.Text.Json;
using ContextCompanion.Core.Packets;

namespace ContextCompanion.Core.Delivery;

/// <summary>Limits of the outbox.</summary>
public sealed record OutboxOptions
{
    /// <summary>Packets queued, delivering or uncertain at once. Default 4.</summary>
    public int MaxPendingPackets { get; init; } = 4;
    /// <summary>Bytes of packets the outbox holds (every packet not yet delivered or discarded). Default 64 MiB.</summary>
    public long MaxStoredBytes { get; init; } = 64L * 1024 * 1024;
    /// <summary>Records kept in <c>outbox.json</c>. Older finished records are dropped; the audit log keeps the history. Default 256.</summary>
    public int MaxRecords { get; init; } = 256;
}

/// <summary>Result of <see cref="Outbox.Enqueue"/>.</summary>
public sealed record EnqueueResult(bool Queued, OutboxRecord Record)
{
    /// <summary>True when the packet was dropped as a consecutive duplicate of <see cref="Record"/>.</summary>
    public bool Duplicate => !Queued;
}

/// <summary>The outbox is full; nothing was added and existing packets are untouched.</summary>
public sealed class OutboxFullException(string message) : IOException(message);

/// <summary>
/// The persistent, bounded outbox. Each packet is stored in <c>packets/&lt;id&gt;/</c> until it is delivered, and
/// its state is kept in <c>outbox.json</c>, replaced atomically on every change. On start, anything left
/// <c>delivering</c> becomes <c>uncertain</c> and is never re-sent automatically. <c>consumed</c> is set only
/// from a valid <c>ack.json</c>. Every state change is appended to the audit log.
/// </summary>
public sealed class Outbox
{
    public const string StateFileName = "outbox.json";
    public const string PacketsDirectoryName = "packets";
    public const string AuditFileName = "audit.jsonl";
    private const int StateFileVersion = 1;
    private const int MaxStateBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    private readonly Lock gate = new();
    private readonly List<OutboxRecord> records;
    private readonly TimeProvider time;
    private readonly InboxWriter store;

    public Outbox(string root, OutboxOptions? options = null, AuditLog? audit = null, TimeProvider? time = null)
    {
        Root = Path.GetFullPath(root);
        Options = options ?? new OutboxOptions();
        if (Options.MaxPendingPackets < 1 || Options.MaxStoredBytes < 1 || Options.MaxRecords < Options.MaxPendingPackets)
            throw new ArgumentException("Outbox limits must be positive and keep at least as many records as pending packets.", nameof(options));
        this.time = time ?? TimeProvider.System;
        Audit = audit ?? new AuditLog(Path.Combine(Root, AuditFileName), this.time);
        Directory.CreateDirectory(Root);
        store = new InboxWriter(Path.Combine(Root, PacketsDirectoryName));
        records = Load();
        Recover();
    }

    public string Root { get; }
    public OutboxOptions Options { get; }
    public AuditLog Audit { get; }
    private string StatePath => Path.Combine(Root, StateFileName);

    public IReadOnlyList<OutboxRecord> Snapshot() { lock (gate) return records.ToArray(); }

    public OutboxRecord? Find(string id) { lock (gate) return records.Find(r => r.Id == id); }

    /// <summary>Folder of the outbox's stored copy of a packet.</summary>
    public string StoredPacketDirectory(string id) => store.PacketDirectory(id);

    /// <summary>
    /// Adds a packet for <paramref name="destination"/>. A packet whose content equals the previous packet for the
    /// same destination is dropped unless that one failed (exact consecutive duplicate suppression by content hash).
    /// Throws <see cref="OutboxFullException"/> when a limit would be exceeded.
    /// </summary>
    public EnqueueResult Enqueue(PacketDraft draft, string destination)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!PacketNames.IsValidAgentName(destination)) throw new ArgumentException("Invalid destination name.", nameof(destination));
        lock (gate)
        {
            var previous = records.FindLast(r => r.Destination == destination);
            if (previous is not null && previous.State != DeliveryState.Failed && previous.ContentSha256 == draft.ContentSha256)
                return new EnqueueResult(false, previous);

            CheckCapacity(draft.TotalBytes, addsPending: true);
            if (records.Any(r => r.Id == draft.Id)) throw new ArgumentException("A packet with this id is already in the outbox.", nameof(draft));

            var now = Now();
            var record = new OutboxRecord
            {
                Id = draft.Id, Destination = destination, Created = draft.Manifest.Created, State = DeliveryState.Queued,
                Bytes = draft.TotalBytes, ContentSha256 = draft.ContentSha256,
                ItemSha256 = draft.Manifest.Items.Select(i => i.Sha256).ToArray(),
                Note = "Waiting for delivery.", Updated = now, Stored = true,
            };
            // The stored copy is written first; the state file is the commit point.
            string stored = store.Write(draft);
            try
            {
                Commit([.. records, record]);
            }
            catch
            {
                TryDeleteDirectory(stored);
                throw;
            }
            Audit.Record(record, null, DeliveryTransitions.Name(DeliveryState.Queued));
            return new EnqueueResult(true, record);
        }
    }

    /// <summary>queued → delivering. Called by the delivery queue just before handing the packet to its destination.</summary>
    public OutboxRecord BeginDelivery(string id) =>
        Transition(id, DeliveryState.Delivering, r => r with { Note = "Delivery in progress." });

    /// <summary>delivering → delivered, failed, uncertain or queued, according to what the destination reported.</summary>
    public OutboxRecord CompleteDelivery(string id, DeliveryOutcome outcome)
    {
        var now = Now();
        return outcome switch
        {
            DeliveryOutcome.Delivered => Transition(id, DeliveryState.Delivered,
                r => r with { Delivered = now, Stored = false, Note = "Delivered to the destination; not proof that anyone read it." },
                deleteStoredCopy: true),
            DeliveryOutcome.NotDelivered => Transition(id, DeliveryState.Failed,
                r => r with { Note = "Definitely not delivered. Retry only by explicit request." }),
            DeliveryOutcome.Unavailable => Transition(id, DeliveryState.Queued,
                r => r with { Note = "Destination unavailable; definitely not delivered, waiting." }),
            DeliveryOutcome.Uncertain => Transition(id, DeliveryState.Uncertain,
                r => r with { Note = "May have arrived. Not retried automatically." }),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };
    }

    /// <summary>queued → failed, when the packet's destination is not configured.</summary>
    public OutboxRecord FailUndeliverable(string id) =>
        Transition(id, DeliveryState.Failed, r => r with { Note = "Destination is not configured; not delivered." });

    /// <summary>
    /// delivered or uncertain → consumed, if the destination holds a valid <c>ack.json</c> for this packet.
    /// This is the only way to reach <c>consumed</c>. Returns false (and changes nothing) otherwise.
    /// </summary>
    public bool RecordAcknowledgement(string id, IDestination destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var record = Find(id);
        if (record is null || record.Destination != destination.Name ||
            record.State is not (DeliveryState.Delivered or DeliveryState.Uncertain)) return false;
        PacketAcknowledgement? ack = destination.ReadAcknowledgement(id);
        if (ack is null || ack.Id != id || !PacketNames.IsValidAgentName(ack.Consumer) ||
            ack.Consumed < record.Created - ClockSkew || ack.Consumed > Now() + ClockSkew) return false;
        Transition(id, DeliveryState.Consumed,
            r => r with { Consumed = ack.Consumed, Consumer = ack.Consumer, Stored = false,
                Note = "Acknowledged by the consumer; not proof that it was read or answered." },
            deleteStoredCopy: true, expected: record.State);
        return true;
    }

    /// <summary>Checks every delivered or uncertain packet for an acknowledgement. Returns how many became consumed.</summary>
    public int RefreshAcknowledgements(IEnumerable<IDestination> destinations)
    {
        var byName = destinations.ToDictionary(d => d.Name, StringComparer.Ordinal);
        int consumed = 0;
        foreach (var record in Snapshot())
            if (record.State is DeliveryState.Delivered or DeliveryState.Uncertain &&
                byName.TryGetValue(record.Destination, out var destination) && RecordAcknowledgement(record.Id, destination))
                consumed++;
        return consumed;
    }

    /// <summary>failed or uncertain → queued. Only on explicit request by the user; an uncertain packet may then arrive twice.</summary>
    public OutboxRecord Retry(string id)
    {
        lock (gate)
        {
            var record = records.Find(r => r.Id == id) ?? throw new ArgumentException("Unknown packet.", nameof(id));
            if (!record.Stored || !Directory.Exists(StoredPacketDirectory(id)))
                throw new InvalidOperationException("The outbox no longer holds this packet.");
            if (record.State == DeliveryState.Failed) CheckCapacity(0, addsPending: true);
            return Transition(id, DeliveryState.Queued, r => r with { Note = "Queued again by explicit request." });
        }
    }

    /// <summary>Removes a failed or uncertain packet and its stored copy from the outbox. The audit log keeps the record.</summary>
    public void Discard(string id)
    {
        lock (gate)
        {
            var record = records.Find(r => r.Id == id) ?? throw new ArgumentException("Unknown packet.", nameof(id));
            if (record.State is not (DeliveryState.Failed or DeliveryState.Uncertain))
                throw new InvalidOperationException($"A {DeliveryTransitions.Name(record.State)} packet cannot be discarded.");
            Commit(records.Where(r => r.Id != id).ToList());
            if (record.Stored) TryDeleteDirectory(StoredPacketDirectory(id));
            Audit.Record(record, record.State, AuditLog.Discarded);
        }
    }

    private void CheckCapacity(long addedBytes, bool addsPending)
    {
        int pending = records.Count(r => DeliveryTransitions.IsPending(r.State));
        if (addsPending && pending >= Options.MaxPendingPackets)
            throw new OutboxFullException($"The outbox already holds {pending} pending packets (limit {Options.MaxPendingPackets}); nothing was added.");
        long stored = records.Where(r => r.Stored).Sum(r => r.Bytes);
        if (stored + addedBytes > Options.MaxStoredBytes)
            throw new OutboxFullException($"The outbox would hold more than {Options.MaxStoredBytes} bytes; nothing was added.");
    }

    private OutboxRecord Transition(string id, DeliveryState to, Func<OutboxRecord, OutboxRecord> change,
        bool deleteStoredCopy = false, DeliveryState? expected = null)
    {
        lock (gate)
        {
            int index = records.FindIndex(r => r.Id == id);
            if (index < 0) throw new ArgumentException("Unknown packet.", nameof(id));
            var current = records[index];
            if (expected is { } e && current.State != e)
                throw new InvalidOperationException("The packet changed state concurrently.");
            if (!DeliveryTransitions.IsAllowed(current.State, to))
                throw new InvalidOperationException(
                    $"A {DeliveryTransitions.Name(current.State)} packet cannot become {DeliveryTransitions.Name(to)}.");
            var next = change(current) with { State = to, Updated = Now() };
            var updated = records.ToList();
            updated[index] = next;
            Commit(updated);
            if (deleteStoredCopy && current.Stored) TryDeleteDirectory(StoredPacketDirectory(id));
            Audit.Record(next, current.State, DeliveryTransitions.Name(to));
            return next;
        }
    }

    /// <summary>Persists <paramref name="next"/> atomically, then makes it the in-memory state. On failure nothing changes.</summary>
    private void Commit(List<OutboxRecord> next)
    {
        Prune(next);
        var file = new OutboxStateFile { SchemaVersion = StateFileVersion, Records = next };
        AtomicFile.Replace(StatePath, JsonSerializer.SerializeToUtf8Bytes(file, PacketJson.Options));
        records.Clear();
        records.AddRange(next);
    }

    /// <summary>Drops the oldest finished records beyond the record limit, keeping the latest record per destination.</summary>
    private void Prune(List<OutboxRecord> list)
    {
        int excess = list.Count - Options.MaxRecords;
        if (excess <= 0) return;
        var latest = list.GroupBy(r => r.Destination).Select(g => g.Last().Id).ToHashSet();
        for (int i = 0; i < list.Count && excess > 0;)
        {
            var r = list[i];
            if (!r.Stored && r.State is DeliveryState.Delivered or DeliveryState.Consumed && !latest.Contains(r.Id))
            {
                list.RemoveAt(i);
                excess--;
            }
            else i++;
        }
    }

    private List<OutboxRecord> Load()
    {
        var file = new FileInfo(StatePath);
        if (!file.Exists) return [];
        const string invalid = "The outbox state is invalid; stored packets were left untouched.";
        if (file.Length > MaxStateBytes) throw new IOException(invalid + " It exceeds its size limit.");
        OutboxStateFile? state;
        try
        {
            state = JsonSerializer.Deserialize<OutboxStateFile>(File.ReadAllBytes(StatePath), PacketJson.Options);
        }
        catch (JsonException ex) { throw new IOException(invalid, ex); }
        if (state is null || state.SchemaVersion != StateFileVersion || state.Records is null || state.Records.Any(r => r is null) ||
            state.Records.Select(r => r.Id).Distinct().Count() != state.Records.Count ||
            state.Records.Any(r => !Ulid.IsValid(r.Id) || !PacketNames.IsValidAgentName(r.Destination) || r.Bytes < 0 ||
                !Enum.IsDefined(r.State) || r.Note is null || r.ItemSha256 is null ||
                !PacketValidator.IsLowerHex(r.ContentSha256 ?? "", 64) ||
                r.ItemSha256.Any(h => !PacketValidator.IsLowerHex(h ?? "", 64)) ||
                (r.Consumer is not null && !PacketNames.IsValidAgentName(r.Consumer))))
            throw new IOException(invalid);
        return state.Records.ToList();
    }

    /// <summary>Restart recovery. Runs once, from the constructor, before anything else can use the outbox.</summary>
    private void Recover()
    {
        // Interrupted deliveries may or may not have arrived: uncertain, never re-sent automatically.
        foreach (var record in records.Where(r => r.State == DeliveryState.Delivering).ToArray())
            Transition(record.Id, DeliveryState.Uncertain, r => r with { Note = "Interrupted during delivery; not retried automatically." });

        // A stored copy that vanished cannot be delivered.
        foreach (var record in records.Where(r => r.Stored && !Directory.Exists(StoredPacketDirectory(r.Id))).ToArray())
        {
            if (record.State == DeliveryState.Queued)
                Transition(record.Id, DeliveryState.Failed, r => r with { Stored = false, Note = "Stored packet is missing; not delivered." });
            else
            {
                var list = records.ToList();
                list[records.IndexOf(record)] = record with { Stored = false, Updated = Now() };
                Commit(list);
            }
        }

        // Leftovers of interrupted writes: staged folders, and stored copies whose enqueue never committed
        // (the state file is the commit point) or whose deletion after delivery did not finish.
        store.RemoveStaleStaging();
        var held = records.Where(r => r.Stored).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (string directory in PacketReader.ListPacketDirectories(store.Root))
            if (!held.Contains(Path.GetFileName(directory))) TryDeleteDirectory(directory);
    }

    private DateTimeOffset Now() => PacketJson.TruncateToMilliseconds(time.GetUtcNow());

    private static void TryDeleteDirectory(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>The persisted form of the outbox.</summary>
internal sealed record OutboxStateFile
{
    public required int SchemaVersion { get; init; }
    public required List<OutboxRecord> Records { get; init; }
}
