using System.Text.Json;

namespace ContextCompanion.Core.Packets;

/// <summary>A packet assembled in the staging folder but not yet visible in the inbox.</summary>
public sealed record StagedPacket(string Id, string StagingDirectory);

/// <summary>
/// Writes packets into an inbox atomically. A packet is assembled in <c>inbox/.tmp/&lt;id&gt;/</c>,
/// every file is flushed to disk, the staged folder is validated, and then it is moved to
/// <c>inbox/&lt;id&gt;/</c> with a single rename. Readers therefore see either no packet or a complete one.
/// </summary>
public sealed class InboxWriter
{
    public InboxWriter(string inboxRoot)
    {
        Root = Path.GetFullPath(inboxRoot);
    }

    public string Root { get; }
    public string StagingRoot => Path.Combine(Root, PacketFormat.StagingDirectoryName);

    public string PacketDirectory(string id) =>
        Ulid.IsValid(id) ? Path.Combine(Root, id) : throw new ArgumentException("Not a canonical ULID.", nameof(id));

    /// <summary>Writes the packet and makes it visible. Returns the packet folder.</summary>
    public string Write(PacketDraft draft)
    {
        var staged = Stage(draft);
        try { return Commit(staged); }
        catch { TryDelete(staged.StagingDirectory); throw; }
    }

    /// <summary>Assembles and validates the packet in the staging folder. Nothing appears in the inbox yet.</summary>
    public StagedPacket Stage(PacketDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        string final = PacketDirectory(draft.Id);
        if (Directory.Exists(final)) throw new IOException($"Packet {draft.Id} already exists in the inbox.");
        Directory.CreateDirectory(StagingRoot);
        string staging = Path.Combine(StagingRoot, draft.Id);
        if (Directory.Exists(staging)) throw new IOException($"Packet {draft.Id} is already being staged.");
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var item in draft.Manifest.Items)
                WriteDurably(Path.Combine(staging, item.StoredFile), draft.Contents[item.StoredFile]);
            // The manifest is written last, so a staged folder without one is plainly incomplete.
            WriteDurably(Path.Combine(staging, PacketFormat.ManifestFileName),
                JsonSerializer.SerializeToUtf8Bytes(draft.Manifest, PacketJson.Options));
            var result = PacketValidator.ValidateDirectory(staging);
            if (!result.IsValid) throw new InvalidPacketException(result.Problems);
            return new StagedPacket(draft.Id, staging);
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    /// <summary>Moves a staged packet into the inbox in one rename. Returns the packet folder.</summary>
    public string Commit(StagedPacket staged)
    {
        ArgumentNullException.ThrowIfNull(staged);
        string final = PacketDirectory(staged.Id);
        if (Path.GetFullPath(staged.StagingDirectory) != Path.Combine(StagingRoot, staged.Id))
            throw new ArgumentException("The packet was not staged by this inbox.", nameof(staged));
        if (Directory.Exists(final)) throw new IOException($"Packet {staged.Id} already exists in the inbox.");
        Directory.Move(staged.StagingDirectory, final);
        return final;
    }

    /// <summary>
    /// Deletes staged packet folders left in the staging folder, for example after a crash between
    /// staging and commit. Call only when no other writer is staging packets into this inbox.
    /// Other entries (such as a consumer's acknowledgement being written) are left alone.
    /// Returns the number of folders removed.
    /// </summary>
    public int RemoveStaleStaging()
    {
        var staging = new DirectoryInfo(StagingRoot);
        if (!staging.Exists) return 0;
        int removed = 0;
        foreach (var entry in staging.EnumerateDirectories())
        {
            if (!Ulid.IsValid(entry.Name)) continue;
            if (entry.LinkTarget is not null) entry.Delete();
            else entry.Delete(recursive: true);
            removed++;
        }
        return removed;
    }

    internal static void WriteDurably(string path, ReadOnlySpan<byte> bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
