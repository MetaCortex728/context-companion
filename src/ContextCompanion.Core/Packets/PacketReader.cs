namespace ContextCompanion.Core.Packets;

/// <summary>A packet folder that passed validation.</summary>
public sealed record Packet(string Directory, PacketManifest Manifest, PacketAcknowledgement? Acknowledgement)
{
    public string Id => Manifest.Id;

    /// <summary>Opens an item listed in the manifest for reading.</summary>
    public FileStream OpenItem(string fileName)
    {
        if (!Manifest.Items.Any(i => i.File == fileName)) throw new ArgumentException($"'{fileName}' is not an item of this packet.", nameof(fileName));
        return new FileStream(Path.Combine(Directory, fileName), FileMode.Open, FileAccess.Read, FileShare.Read);
    }
}

/// <summary>Reads packets from an inbox, validating each before returning it.</summary>
public static class PacketReader
{
    /// <summary>Reads and fully validates one packet folder. Throws <see cref="InvalidPacketException"/> if invalid.</summary>
    public static Packet Read(string packetDirectory)
    {
        var result = PacketValidator.ValidateDirectory(packetDirectory);
        if (!result.IsValid || result.Manifest is null) throw new InvalidPacketException(result.Problems);
        return new Packet(result.Directory, result.Manifest, result.Acknowledgement);
    }

    /// <summary>
    /// The packet folders of an inbox: subfolders named by a ULID, in identifier (creation) order.
    /// The staging folder and anything else are skipped.
    /// </summary>
    public static IReadOnlyList<string> ListPacketDirectories(string inboxRoot)
    {
        var root = new DirectoryInfo(inboxRoot);
        if (!root.Exists) return [];
        return root.EnumerateDirectories()
            .Where(d => Ulid.IsValid(d.Name))
            .Select(d => d.FullName)
            .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>True when a folder looks like a single packet rather than an inbox.</summary>
    public static bool IsPacketDirectory(string path) =>
        File.Exists(Path.Combine(path, PacketFormat.ManifestFileName));
}
