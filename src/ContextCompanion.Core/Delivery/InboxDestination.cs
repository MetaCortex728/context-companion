using ContextCompanion.Core.Packets;

namespace ContextCompanion.Core.Delivery;

/// <summary>Somewhere packets are delivered.</summary>
public interface IDestination
{
    /// <summary>Stable name recorded with each packet: 1-64 characters from A-Z, a-z, 0-9, '.', '_', '-'.</summary>
    string Name { get; }

    /// <summary>
    /// Delivers the packet stored in <paramref name="packetDirectory"/>. Must report only what it knows:
    /// <see cref="DeliveryOutcome.Uncertain"/> whenever the packet might have arrived but this is not certain.
    /// </summary>
    Task<DeliveryOutcome> DeliverAsync(string packetDirectory, CancellationToken cancellationToken);

    /// <summary>The consumer's acknowledgement of a delivered packet, or null if there is none (or it is invalid).</summary>
    PacketAcknowledgement? ReadAcknowledgement(string packetId);
}

/// <summary>
/// Delivers packets into a local inbox folder with the atomic <see cref="InboxWriter"/>. A packet is
/// reported delivered only once it is visible in the inbox and validates.
/// </summary>
public sealed class InboxDestination : IDestination
{
    private readonly InboxWriter writer;

    /// <summary>
    /// Opens the inbox and removes packets left half-staged by an earlier crash. context-companion
    /// must be the only program writing packets into this inbox.
    /// </summary>
    public InboxDestination(string inboxRoot, string name = "inbox")
    {
        if (!PacketNames.IsValidAgentName(name)) throw new ArgumentException("Invalid destination name.", nameof(name));
        Name = name;
        writer = new InboxWriter(inboxRoot);
        Directory.CreateDirectory(writer.Root);
        writer.RemoveStaleStaging();
    }

    public string Name { get; }
    public string Root => writer.Root;

    public Task<DeliveryOutcome> DeliverAsync(string packetDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PacketDraft draft;
        try
        {
            // Re-validate the stored copy: a damaged packet is never delivered.
            draft = PacketDraft.FromPacket(PacketReader.Read(packetDirectory));
        }
        catch (InvalidPacketException) { return Task.FromResult(DeliveryOutcome.NotDelivered); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Task.FromResult(DeliveryOutcome.NotDelivered); }

        string final = writer.PacketDirectory(draft.Id);
        if (Directory.Exists(final))
            return Task.FromResult(IsSamePacket(final, draft) ? DeliveryOutcome.Delivered : DeliveryOutcome.Uncertain);

        StagedPacket staged;
        try { staged = writer.Stage(draft); }
        catch (InvalidPacketException) { return Task.FromResult(DeliveryOutcome.NotDelivered); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Staging cleans up after itself, and nothing is visible before the rename.
            return Task.FromResult(Directory.Exists(final) ? DeliveryOutcome.Uncertain : DeliveryOutcome.Unavailable);
        }

        // Past this point the packet may become visible; a cancellation no longer applies.
        try
        {
            writer.Commit(staged);
            return Task.FromResult(DeliveryOutcome.Delivered);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (Directory.Exists(final))
                return Task.FromResult(IsSamePacket(final, draft) ? DeliveryOutcome.Delivered : DeliveryOutcome.Uncertain);
            try { if (Directory.Exists(staged.StagingDirectory)) Directory.Delete(staged.StagingDirectory, recursive: true); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return Task.FromResult(DeliveryOutcome.NotDelivered);
        }
    }

    public PacketAcknowledgement? ReadAcknowledgement(string packetId)
    {
        if (!Ulid.IsValid(packetId)) return null;
        try { return PacketAcknowledgements.Read(writer.PacketDirectory(packetId)); }
        catch (InvalidPacketException) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool IsSamePacket(string directory, PacketDraft draft)
    {
        var result = PacketValidator.ValidateDirectory(directory);
        return result.IsValid && result.Manifest is { } manifest && manifest.Id == draft.Id &&
            PacketDraft.ComputeContentHash(manifest) == draft.ContentSha256 &&
            manifest.Items.Select(i => i.File).SequenceEqual(draft.Manifest.Items.Select(i => i.File));
    }
}
