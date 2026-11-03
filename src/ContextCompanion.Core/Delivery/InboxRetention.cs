using ContextCompanion.Core.Packets;

namespace ContextCompanion.Core.Delivery;

/// <summary>Limits on what an inbox keeps. A null limit means no limit.</summary>
public sealed record RetentionLimits(int? MaxPackets, long? MaxBytes, TimeSpan? MaxAge)
{
    public static RetentionLimits None { get; } = new(null, null, null);
}

/// <summary>Which packets <c>ccomp purge</c> removes.</summary>
public enum PurgeKind
{
    /// <summary>Apply the retention limits.</summary>
    Retention,
    /// <summary>Every packet that is not queued or being delivered.</summary>
    All,
    /// <summary>Packets created before a cut-off.</summary>
    OlderThan,
    /// <summary>One packet.</summary>
    Id,
}

/// <summary>A packet chosen for removal and why. Holds ids, sizes and reason tokens only.</summary>
public sealed record PacketRemoval(string Id, string Reason, long Bytes);

/// <summary>
/// Retention and purge for an inbox. Packets are chosen by their id's timestamp, size and acknowledgement only,
/// never by content. Consumed packets go first, then the oldest; a packet that the outbox still has as
/// <c>queued</c> or <c>delivering</c> is never removed. Every removal is recorded in the audit log as the packet id,
/// destination and a reason token. Deleting files does not make their bytes unrecoverable (see PRIVACY.md).
/// </summary>
public static class InboxRetention
{
    /// <summary>The inbox's own audit log, used by <c>ccomp purge</c> unless told otherwise. Consumers ignore it.</summary>
    public const string AuditFileName = ".audit.jsonl";

    public const string ReasonMaxAge = "retention-max-age";
    public const string ReasonMaxPackets = "retention-max-packets";
    public const string ReasonMaxBytes = "retention-max-bytes";
    public const string ReasonPurgeAll = "purge-all";
    public const string ReasonPurgeOlderThan = "purge-older-than";
    public const string ReasonPurgeId = "purge-id";

    public static string DefaultAuditPath(string inboxRoot) => Path.Combine(Path.GetFullPath(inboxRoot), AuditFileName);

    private sealed record Entry(string Id, DateTimeOffset Created, long Bytes, bool Consumed, bool Protected);

    /// <summary>Which packets the retention limits remove, in the order they would be removed.</summary>
    public static IReadOnlyList<PacketRemoval> Plan(string inboxRoot, RetentionLimits limits, DateTimeOffset now,
        Func<string, DeliveryState?>? stateOf = null)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var entries = Scan(inboxRoot, stateOf);
        var plan = new List<PacketRemoval>();
        var kept = new List<Entry>(entries);

        if (limits.MaxAge is { } age)
            foreach (var entry in entries.Where(e => !e.Protected && e.Created < now - age))
            {
                plan.Add(new PacketRemoval(entry.Id, ReasonMaxAge, entry.Bytes));
                kept.Remove(entry);
            }

        // Consumed packets first, then the rest; oldest first within each group.
        var candidates = new Queue<Entry>(kept.Where(e => !e.Protected)
            .OrderBy(e => e.Consumed ? 0 : 1).ThenBy(e => e.Id, StringComparer.Ordinal));
        long bytes = kept.Sum(e => e.Bytes);
        int count = kept.Count;
        while (limits.MaxPackets is { } maxPackets && count > maxPackets && candidates.TryDequeue(out var entry))
        {
            plan.Add(new PacketRemoval(entry.Id, ReasonMaxPackets, entry.Bytes));
            count--;
            bytes -= entry.Bytes;
        }
        while (limits.MaxBytes is { } maxBytes && bytes > maxBytes && candidates.TryDequeue(out var entry))
        {
            plan.Add(new PacketRemoval(entry.Id, ReasonMaxBytes, entry.Bytes));
            bytes -= entry.Bytes;
        }
        return plan;
    }

    /// <summary>
    /// Which packets an explicit purge removes. <paramref name="olderThan"/> is required for <see cref="PurgeKind.OlderThan"/>,
    /// <paramref name="id"/> for <see cref="PurgeKind.Id"/>. Throws <see cref="InvalidOperationException"/> if the named
    /// packet is missing or still queued or being delivered.
    /// </summary>
    public static IReadOnlyList<PacketRemoval> PlanPurge(string inboxRoot, PurgeKind kind, DateTimeOffset now,
        TimeSpan? olderThan = null, string? id = null, RetentionLimits? limits = null, Func<string, DeliveryState?>? stateOf = null)
    {
        if (kind == PurgeKind.Retention) return Plan(inboxRoot, limits ?? RetentionLimits.None, now, stateOf);
        var entries = Scan(inboxRoot, stateOf);
        switch (kind)
        {
            case PurgeKind.All:
                return entries.Where(e => !e.Protected).Select(e => new PacketRemoval(e.Id, ReasonPurgeAll, e.Bytes)).ToArray();
            case PurgeKind.OlderThan:
                var cutoff = now - (olderThan ?? throw new ArgumentNullException(nameof(olderThan)));
                return entries.Where(e => !e.Protected && e.Created < cutoff).Select(e => new PacketRemoval(e.Id, ReasonPurgeOlderThan, e.Bytes)).ToArray();
            case PurgeKind.Id:
                if (!Ulid.IsValid(id)) throw new ArgumentException("Not a packet id.", nameof(id));
                var entry = entries.Find(e => e.Id == id) ?? throw new InvalidOperationException($"Packet {id} is not in the inbox.");
                if (entry.Protected) throw new InvalidOperationException($"Packet {id} is still being delivered and cannot be removed.");
                return [new PacketRemoval(entry.Id, ReasonPurgeId, entry.Bytes)];
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    /// <summary>
    /// Removes the planned packets. Each folder is first moved into the inbox's staging folder in one rename, so a
    /// consumer never sees a half-deleted packet, then deleted; each removal is then written to the audit log.
    /// Returns the removals that succeeded; a packet that cannot be moved (for example because it is open) is skipped.
    /// </summary>
    public static IReadOnlyList<PacketRemoval> Remove(string inboxRoot, IEnumerable<PacketRemoval> plan, AuditLog audit, string? destination = null)
    {
        ArgumentNullException.ThrowIfNull(audit);
        string root = Path.GetFullPath(inboxRoot);
        string staging = Path.Combine(root, PacketFormat.StagingDirectoryName);
        var done = new List<PacketRemoval>();
        foreach (var removal in plan)
        {
            if (!Ulid.IsValid(removal.Id)) continue;
            string folder = Path.Combine(root, removal.Id);
            if (!Directory.Exists(folder)) continue;
            // A ULID-named leftover in .tmp is cleaned up by InboxWriter.RemoveStaleStaging after a crash.
            string doomed = Path.Combine(staging, UlidGenerator.Shared.NewId());
            try
            {
                Directory.CreateDirectory(staging);
                Directory.Move(folder, doomed);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            audit.RecordRemoval(removal.Id, destination, removal.Reason);
            done.Add(removal);
            try
            {
                var info = new DirectoryInfo(doomed);
                if (info.LinkTarget is not null) info.Delete();
                else info.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return done;
    }

    /// <summary>Plans and removes in one step, as done after each delivery.</summary>
    public static IReadOnlyList<PacketRemoval> Apply(string inboxRoot, RetentionLimits limits, AuditLog audit, DateTimeOffset now,
        Func<string, DeliveryState?>? stateOf = null, string? destination = null) =>
        Remove(inboxRoot, Plan(inboxRoot, limits, now, stateOf), audit, destination);

    private static List<Entry> Scan(string inboxRoot, Func<string, DeliveryState?>? stateOf)
    {
        var entries = new List<Entry>();
        foreach (string folder in PacketReader.ListPacketDirectories(inboxRoot))
        {
            string id = Path.GetFileName(folder);
            var state = stateOf?.Invoke(id);
            bool consumed = state == DeliveryState.Consumed || File.Exists(Path.Combine(folder, PacketFormat.AckFileName));
            entries.Add(new Entry(id, DateTimeOffset.FromUnixTimeMilliseconds(Ulid.GetTimestamp(id)), FolderBytes(folder), consumed,
                state is DeliveryState.Queued or DeliveryState.Delivering));
        }
        return entries;
    }

    private static long FolderBytes(string folder)
    {
        try
        {
            var info = new DirectoryInfo(folder);
            if (info.LinkTarget is not null) return 0;
            return info.EnumerateFiles("*", SearchOption.TopDirectoryOnly).Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
}
