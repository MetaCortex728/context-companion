using System.Text.Json;

namespace ContextCompanion.Core.Packets;

/// <summary>
/// Writes and reads consumer acknowledgements (<c>ack.json</c>). An acknowledgement says that a
/// consumer took the packet; it does not say that anyone read, understood or answered it.
/// </summary>
public static class PacketAcknowledgements
{
    /// <summary>
    /// Acknowledges a valid packet. The file is staged in the inbox's <c>.tmp</c> folder and moved into
    /// the packet without overwriting, so an existing acknowledgement is never replaced.
    /// </summary>
    public static PacketAcknowledgement Write(string packetDirectory, string consumer, string? note = null, TimeProvider? time = null)
    {
        if (!PacketNames.IsValidAgentName(consumer))
            throw new ArgumentException("Consumer must be 1 to 64 characters from A-Z, a-z, 0-9, '.', '_' and '-'.", nameof(consumer));
        var packet = PacketReader.Read(packetDirectory);
        string target = Path.Combine(packet.Directory, PacketFormat.AckFileName);
        if (packet.Acknowledgement is not null || File.Exists(target))
            throw new IOException($"Packet {packet.Id} is already acknowledged.");

        var ack = new PacketAcknowledgement
        {
            Id = packet.Id,
            Consumer = consumer,
            Consumed = PacketJson.TruncateToMilliseconds((time ?? TimeProvider.System).GetUtcNow()),
            Note = note,
        };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(ack, PacketJson.Options);
        if (json.Length > PacketFormat.MaxAckBytes) throw new ArgumentException("The note is too long.", nameof(note));
        using (var document = JsonDocument.Parse(json, PacketJson.DocumentOptions))
        {
            var problems = PacketValidator.ValidateAcknowledgement(document.RootElement, packet.Id);
            if (problems.Count > 0) throw new ArgumentException("Invalid acknowledgement: " + string.Join("; ", problems));
        }

        string staging = Path.Combine(Path.GetDirectoryName(packet.Directory)!, PacketFormat.StagingDirectoryName);
        Directory.CreateDirectory(staging);
        string temp = Path.Combine(staging, $"ack-{packet.Id}-{Guid.NewGuid():N}.json");
        try
        {
            InboxWriter.WriteDurably(temp, json);
            File.Move(temp, target, overwrite: false);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return ack;
    }

    /// <summary>
    /// Reads a packet's acknowledgement: null if there is none. Throws <see cref="InvalidPacketException"/>
    /// if <c>ack.json</c> exists but is invalid. Checks the acknowledgement only, not the payload.
    /// </summary>
    public static PacketAcknowledgement? Read(string packetDirectory)
    {
        string directory = Path.GetFullPath(packetDirectory);
        string path = Path.Combine(directory, PacketFormat.AckFileName);
        if (!File.Exists(path)) return null;
        string id = Path.GetFileName(directory);
        var problems = new List<string>();
        if (!Ulid.IsValid(id)) problems.Add("the packet folder is not named by a ULID");
        var ack = problems.Count == 0 ? PacketValidator.ReadAcknowledgementFile(path, id, problems) : null;
        if (problems.Count > 0 || ack is null)
            throw new InvalidPacketException(problems.Select(p => new PacketProblem(PacketProblemKind.Acknowledgement, p)).ToArray());
        return ack;
    }
}
