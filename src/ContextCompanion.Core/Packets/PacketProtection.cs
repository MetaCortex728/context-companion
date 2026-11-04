using System.Security.Cryptography;
using System.Text.Json;

namespace ContextCompanion.Core.Packets;

/// <summary>
/// Encrypts a packet's payload files at rest and turns an encrypted packet back into plaintext. The manifest keeps the
/// plaintext <c>bytes</c> and <c>sha256</c> of every item and adds a <c>stored</c> object describing the encrypted file.
/// </summary>
public static class PacketProtection
{
    /// <summary>Encrypts every item of a plaintext draft with <paramref name="protector"/>.</summary>
    public static PacketDraft Protect(PacketDraft draft, IPayloadProtector protector)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(protector);
        if (!PacketNames.IsValidToken(protector.Protection)) throw new ArgumentException("The protector's name is not a valid token.", nameof(protector));
        if (draft.IsProtected) throw new ArgumentException("The packet is already encrypted.", nameof(draft));
        var items = new List<PacketItem>();
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var item in draft.Manifest.Items)
        {
            byte[] stored = protector.Protect(draft.Contents[item.File]);
            string file = item.File + "." + protector.FileExtension;
            items.Add(item with
            {
                Stored = new PacketStoredForm
                {
                    File = file, Bytes = stored.LongLength,
                    Sha256 = Convert.ToHexStringLower(SHA256.HashData(stored)), Protection = protector.Protection,
                },
            });
            contents[file] = stored;
        }
        return Checked(draft.Manifest with { Items = items }, contents);
    }

    /// <summary>
    /// Decrypts a packet into a plaintext draft with the same id, verifying every plaintext size and SHA-256 against the
    /// manifest. Throws <see cref="InvalidPacketException"/> if an item cannot be decrypted or does not match, and
    /// <see cref="MissingProtectorException"/> if no protector for an item's protection is available.
    /// </summary>
    public static PacketDraft Unprotect(Packet packet, IEnumerable<IPayloadProtector> protectors)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var available = protectors.ToArray();
        var items = new List<PacketItem>();
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var item in packet.Manifest.Items)
        {
            byte[] stored = File.ReadAllBytes(Path.Combine(packet.Directory, item.StoredFile));
            byte[] plaintext = stored;
            if (item.Stored is { } form)
            {
                var protector = available.FirstOrDefault(p => p.Protection == form.Protection)
                    ?? throw new MissingProtectorException(form.Protection);
                try { plaintext = protector.Unprotect(stored); }
                catch (CryptographicException)
                {
                    throw Problem($"'{item.StoredFile}' cannot be decrypted with {form.Protection}");
                }
            }
            if (plaintext.LongLength != item.Bytes || Convert.ToHexStringLower(SHA256.HashData(plaintext)) != item.Sha256)
                throw Problem($"'{item.File}' does not match the plaintext size and SHA-256 in the manifest");
            items.Add(item with { Stored = null });
            contents[item.File] = plaintext;
        }
        return Checked(packet.Manifest with { Items = items }, contents);
    }

    private static PacketDraft Checked(PacketManifest manifest, Dictionary<string, byte[]> contents)
    {
        using var document = JsonSerializer.SerializeToDocument(manifest, PacketJson.Options);
        var problems = PacketValidator.ValidateManifest(document.RootElement);
        if (problems.Count > 0) throw new InvalidPacketException(problems);
        return new PacketDraft(manifest, contents);
    }

    private static InvalidPacketException Problem(string message) =>
        new([new PacketProblem(PacketProblemKind.Protection, message)]);
}

/// <summary>An item is encrypted with a protection this program cannot decrypt (for example DPAPI outside Windows).</summary>
public sealed class MissingProtectorException(string protection)
    : Exception($"No decryptor for '{protection}' is available here.")
{
    public string Protection { get; } = protection;
}
