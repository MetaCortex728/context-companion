using System.Text.Json.Serialization;
using ContextCompanion.Core.Packets;

namespace ContextCompanion.Core.Delivery;

/// <summary>
/// Delivery states of a packet in the outbox. Receipts never claim more than is known:
/// delivered is not consumed, and consumed is not read or answered.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeliveryState>))]
public enum DeliveryState
{
    /// <summary>Stored in the outbox, waiting for delivery.</summary>
    [JsonStringEnumMemberName("queued")] Queued,
    /// <summary>A delivery attempt is in progress.</summary>
    [JsonStringEnumMemberName("delivering")] Delivering,
    /// <summary>The destination reported the packet as delivered.</summary>
    [JsonStringEnumMemberName("delivered")] Delivered,
    /// <summary>A consumer acknowledged the packet with <c>ack.json</c>.</summary>
    [JsonStringEnumMemberName("consumed")] Consumed,
    /// <summary>The packet may or may not have arrived. Never re-sent automatically.</summary>
    [JsonStringEnumMemberName("uncertain")] Uncertain,
    /// <summary>The packet was definitely not delivered.</summary>
    [JsonStringEnumMemberName("failed")] Failed,
}

/// <summary>What a destination reports after a delivery attempt.</summary>
public enum DeliveryOutcome
{
    /// <summary>The packet is at the destination.</summary>
    Delivered,
    /// <summary>Definitely not delivered, and retrying will not help without a change (for example, an invalid packet).</summary>
    NotDelivered,
    /// <summary>Definitely not delivered because the destination is unavailable for now; the packet goes back to the queue.</summary>
    Unavailable,
    /// <summary>The packet may or may not have arrived.</summary>
    Uncertain,
}

/// <summary>One packet in the outbox, as persisted in <c>outbox.json</c>. Holds hashes and sizes, never payload or source details.</summary>
public sealed record OutboxRecord
{
    public required string Id { get; init; }
    public required string Destination { get; init; }
    [JsonConverter(typeof(UtcTimestampConverter))]
    public required DateTimeOffset Created { get; init; }
    public required DeliveryState State { get; init; }
    public required long Bytes { get; init; }
    /// <summary>See <see cref="PacketDraft.ContentSha256"/>; used for duplicate suppression.</summary>
    public required string ContentSha256 { get; init; }
    public required IReadOnlyList<string> ItemSha256 { get; init; }
    /// <summary>A fixed explanation written by the outbox, never user or payload text.</summary>
    public required string Note { get; init; }
    [JsonConverter(typeof(UtcTimestampConverter))]
    public required DateTimeOffset Updated { get; init; }
    [JsonConverter(typeof(NullableUtcTimestampConverter))]
    public DateTimeOffset? Delivered { get; init; }
    [JsonConverter(typeof(NullableUtcTimestampConverter))]
    public DateTimeOffset? Consumed { get; init; }
    public string? Consumer { get; init; }
    /// <summary>True while the outbox still holds a copy of the packet (until it is delivered or discarded).</summary>
    public required bool Stored { get; init; }
}

/// <summary>The rules for moving between delivery states.</summary>
public static class DeliveryTransitions
{
    /// <summary>
    /// Allowed transitions:
    /// queued → delivering (attempt starts) or failed (destination not configured);
    /// delivering → delivered, failed, uncertain, or queued (destination unavailable, definitely unsent);
    /// delivered → consumed (only from ack.json);
    /// uncertain → consumed (only from ack.json) or queued (explicit retry by the user);
    /// failed → queued (explicit retry by the user).
    /// Consumed is final. On restart, delivering becomes uncertain.
    /// </summary>
    public static bool IsAllowed(DeliveryState from, DeliveryState to) => (from, to) switch
    {
        (DeliveryState.Queued, DeliveryState.Delivering or DeliveryState.Failed) => true,
        (DeliveryState.Delivering, DeliveryState.Delivered or DeliveryState.Failed or DeliveryState.Uncertain or DeliveryState.Queued) => true,
        (DeliveryState.Delivered, DeliveryState.Consumed) => true,
        (DeliveryState.Uncertain, DeliveryState.Consumed or DeliveryState.Queued) => true,
        (DeliveryState.Failed, DeliveryState.Queued) => true,
        _ => false,
    };

    /// <summary>Pending packets count against the outbox's packet limit. Uncertain ones stay pending until resolved.</summary>
    public static bool IsPending(DeliveryState state) =>
        state is DeliveryState.Queued or DeliveryState.Delivering or DeliveryState.Uncertain;

    public static string Name(DeliveryState state) => state switch
    {
        DeliveryState.Queued => "queued",
        DeliveryState.Delivering => "delivering",
        DeliveryState.Delivered => "delivered",
        DeliveryState.Consumed => "consumed",
        DeliveryState.Uncertain => "uncertain",
        DeliveryState.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };
}
