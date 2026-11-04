using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ContextCompanion.Core.Packets;

/// <summary>A complete packet held in memory, ready to be written.</summary>
public sealed class PacketDraft
{
    internal PacketDraft(PacketManifest manifest, IReadOnlyDictionary<string, byte[]> contents)
    {
        Manifest = manifest;
        Contents = contents;
        ContentSha256 = ComputeContentHash(manifest);
    }

    public PacketManifest Manifest { get; }
    public string Id => Manifest.Id;
    /// <summary>Payload bytes keyed by the name of the file in the packet folder (the stored form for encrypted items).</summary>
    public IReadOnlyDictionary<string, byte[]> Contents { get; }
    public long TotalBytes => Manifest.TotalBytes;

    /// <summary>
    /// Hash identifying the packet's content for duplicate suppression: SHA-256 over each item's kind,
    /// media type and SHA-256, in order. Identifier, time, file names and source details are excluded,
    /// so recapturing the same pixels or text is recognised as the same content.
    /// </summary>
    public string ContentSha256 { get; }

    public static string ComputeContentHash(PacketManifest manifest)
    {
        var text = new StringBuilder();
        foreach (var item in manifest.Items)
            text.Append(item.Kind.ToString().ToLowerInvariant()).Append(' ')
                .Append(item.MediaType).Append(' ').Append(item.Sha256).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>Loads a validated packet's files back into memory, for example to deliver a stored copy.</summary>
    public static PacketDraft FromPacket(Packet packet)
    {
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var item in packet.Manifest.Items)
            contents[item.StoredFile] = File.ReadAllBytes(Path.Combine(packet.Directory, item.StoredFile));
        return new PacketDraft(packet.Manifest, contents);
    }

    /// <summary>True when at least one item is stored encrypted.</summary>
    public bool IsProtected => Manifest.Items.Any(i => i.Stored is not null);
}

/// <summary>
/// Builds a packet: adds items (hashing them), source details and redaction records, assigns a ULID and
/// creation time, and checks the result against the format rules before returning it.
/// </summary>
public sealed class PacketBuilder
{
    private readonly UlidGenerator ids;
    private readonly TimeProvider time;
    private readonly List<PacketItem> items = [];
    private readonly Dictionary<string, byte[]> contents = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PacketRedaction> redactions = [];
    private PacketSource? source;

    public PacketBuilder(UlidGenerator? ids = null, TimeProvider? time = null)
    {
        this.time = time ?? TimeProvider.System;
        this.ids = ids ?? (time is null ? UlidGenerator.Shared : new UlidGenerator(time));
    }

    public PacketBuilder AddItem(PacketItemKind kind, string fileName, string mediaType, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        string? reason = PacketNames.CheckItemFileName(fileName);
        if (reason is not null) throw new ArgumentException($"Invalid item file name: {reason}.", nameof(fileName));
        if (contents.ContainsKey(fileName)) throw new ArgumentException($"'{fileName}' is already in the packet.", nameof(fileName));
        if (!PacketNames.IsValidMediaType(mediaType) || !PacketNames.MediaTypeMatchesKind(kind, mediaType))
            throw new ArgumentException($"'{mediaType}' is not a valid media type for a {kind} item.", nameof(mediaType));
        byte[] copy = content.ToArray();
        items.Add(new PacketItem
        {
            Kind = kind, File = fileName, MediaType = mediaType, Bytes = copy.LongLength,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(copy)),
        });
        contents[fileName] = copy;
        return this;
    }

    public PacketBuilder AddText(string fileName, string text, PacketItemKind kind = PacketItemKind.Text) =>
        AddItem(kind, fileName, "text/plain", new UTF8Encoding(false).GetBytes(text));

    public PacketBuilder WithSource(PacketSource? value) { source = value; return this; }

    public PacketBuilder AddRedaction(PacketRedaction redaction) { redactions.Add(redaction); return this; }

    /// <summary>Creates the draft. Throws <see cref="InvalidPacketException"/> if the result breaks a format rule.</summary>
    public PacketDraft Build()
    {
        var created = PacketJson.TruncateToMilliseconds(time.GetUtcNow());
        var manifest = new PacketManifest
        {
            Id = ids.NewId(),
            Created = created,
            Items = items.ToArray(),
            Source = source,
            Redactions = redactions.ToArray(),
        };
        using var document = JsonSerializer.SerializeToDocument(manifest, PacketJson.Options);
        var problems = PacketValidator.ValidateManifest(document.RootElement);
        if (problems.Count > 0) throw new InvalidPacketException(problems);
        return new PacketDraft(manifest, contents.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
    }
}

/// <summary>A packet that failed validation.</summary>
public sealed class InvalidPacketException : Exception
{
    public InvalidPacketException(IReadOnlyList<PacketProblem> problems)
        : base("Invalid packet: " + string.Join("; ", problems)) => Problems = problems;

    public IReadOnlyList<PacketProblem> Problems { get; }
}
