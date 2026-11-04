using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContextCompanion.Core.Packets;

namespace ContextCompanion.Core.Tests;

public class PacketRoundTripTests
{
    [Fact]
    public void WrittenPacketReadsBackIdentically()
    {
        using var temp = new TempDirectory();
        string inbox = temp.Combine("inbox");
        var draft = Samples.Draft();

        string folder = new InboxWriter(inbox).Write(draft);
        var packet = PacketReader.Read(folder);

        Assert.Equal(Path.Combine(Path.GetFullPath(inbox), draft.Id), folder);
        Assert.Equal(JsonSerializer.Serialize(draft.Manifest, PacketJson.Options), JsonSerializer.Serialize(packet.Manifest, PacketJson.Options));
        Assert.Null(packet.Acknowledgement);
        foreach (var item in packet.Manifest.Items)
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(folder, item.File));
            Assert.Equal(draft.Contents[item.File], bytes);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), item.Sha256);
            Assert.Equal(bytes.LongLength, item.Bytes);
        }
        Assert.Equal(Samples.SecretText, Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(folder, "selection.txt"))));
        Assert.Equal(new PacketRegion(-1920, 10, 640, 480), packet.Manifest.Source!.Region);
        Assert.Equal(Samples.Start, packet.Manifest.Created);
        Assert.Equal([draft.Id], PacketReader.ListPacketDirectories(inbox).Select(Path.GetFileName));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(inbox, PacketFormat.StagingDirectoryName)));
    }

    [Fact]
    public void ManifestUsesTheDocumentedJsonShape()
    {
        using var temp = new TempDirectory();
        string folder = Samples.WritePacket(temp.Path);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")));
        var root = json.RootElement;

        Assert.Equal("context-companion.packet", root.GetProperty("format").GetString());
        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        Assert.Equal("2026-11-03T15:04:05.123Z", root.GetProperty("created").GetString());
        var item = root.GetProperty("items")[0];
        Assert.Equal("image", item.GetProperty("kind").GetString());
        Assert.Equal("capture.png", item.GetProperty("file").GetString());
        Assert.Equal("image/png", item.GetProperty("media_type").GetString());
        Assert.Equal(64, item.GetProperty("bytes").GetInt64());
        Assert.Equal(64, item.GetProperty("sha256").GetString()!.Length);
        Assert.Equal("notepad.exe", root.GetProperty("source").GetProperty("process_name").GetString());
        Assert.Equal("region", root.GetProperty("source").GetProperty("capture_mode").GetString());
        Assert.Equal("secret", root.GetProperty("redactions")[0].GetProperty("kind").GetString());
    }

    [Fact]
    public void SourceAndRedactionsMayBeEmpty()
    {
        using var temp = new TempDirectory();
        var draft = Samples.Builder().AddText("note.txt", "hello", PacketItemKind.Note).Build();
        string folder = new InboxWriter(temp.Path).Write(draft);
        string manifest = File.ReadAllText(Path.Combine(folder, "manifest.json"));

        Assert.DoesNotContain("source", manifest, StringComparison.Ordinal);
        Assert.Contains("\"redactions\": []", manifest, StringComparison.Ordinal);
        Assert.True(PacketValidator.ValidateDirectory(folder).IsValid);

        var partial = Samples.Builder().AddText("t.txt", "x").WithSource(new PacketSource { CaptureMode = "window" }).Build();
        Assert.True(PacketValidator.ValidateDirectory(new InboxWriter(temp.Path).Write(partial)).IsValid);
    }

    [Fact]
    public void CrashBetweenStagingAndRenameLeavesNoVisiblePacket()
    {
        using var temp = new TempDirectory();
        string inbox = temp.Combine("inbox");
        var writer = new InboxWriter(inbox);
        var staged = writer.Stage(Samples.Draft());
        // Simulated crash: the process stops here, so Commit never runs and nothing cleans up.

        Assert.False(Directory.Exists(Path.Combine(inbox, staged.Id)));
        Assert.Empty(PacketReader.ListPacketDirectories(inbox));
        Assert.True(Directory.Exists(staged.StagingDirectory));

        var output = new StringWriter();
        Assert.Equal(0, Tool.Cli.Run(["validate", inbox], output, new StringWriter()));
        Assert.Contains("0 packets checked, 0 invalid.", output.ToString(), StringComparison.Ordinal);

        // On the next start the stale staging folder is cleared and writing works again.
        // A consumer's acknowledgement in progress is not the writer's to remove.
        string ackInProgress = Path.Combine(writer.StagingRoot, "ack-in-progress.json");
        File.WriteAllText(ackInProgress, "{}");
        var restarted = new InboxWriter(inbox);
        Assert.Equal(1, restarted.RemoveStaleStaging());
        Assert.False(Directory.Exists(staged.StagingDirectory));
        Assert.True(File.Exists(ackInProgress));
        string written = restarted.Write(Samples.Draft(variant: 1));
        Assert.Equal([written], PacketReader.ListPacketDirectories(inbox));
    }

    [Fact]
    public void CommittedPacketAppearsComplete()
    {
        using var temp = new TempDirectory();
        var writer = new InboxWriter(temp.Path);
        var staged = writer.Stage(Samples.Draft());
        string folder = writer.Commit(staged);
        Assert.True(PacketValidator.ValidateDirectory(folder).IsValid);
        Assert.False(Directory.Exists(staged.StagingDirectory));
    }

    [Fact]
    public void ExistingPacketIsNeverOverwritten()
    {
        using var temp = new TempDirectory();
        var writer = new InboxWriter(temp.Path);
        var draft = Samples.Draft();
        writer.Write(draft);
        Assert.Throws<IOException>(() => writer.Write(draft));
        Assert.Empty(Directory.EnumerateFileSystemEntries(writer.StagingRoot));
    }

    [Fact]
    public void BuilderRejectsUnsafeInput()
    {
        var builder = Samples.Builder();
        Assert.Throws<ArgumentException>(() => builder.AddItem(PacketItemKind.Image, "../x.png", "image/png", Samples.Png()));
        Assert.Throws<ArgumentException>(() => builder.AddItem(PacketItemKind.Image, "x.png", "text/plain", Samples.Png()));
        builder.AddItem(PacketItemKind.Image, "x.png", "image/png", Samples.Png());
        Assert.Throws<ArgumentException>(() => builder.AddItem(PacketItemKind.Image, "X.PNG", "image/png", Samples.Png()));
        Assert.Throws<InvalidPacketException>(() => Samples.Builder().Build()); // no items
        var badSource = Samples.Builder().AddText("t.txt", "x").WithSource(new PacketSource { WindowTitle = "a\u0007b" });
        Assert.Throws<InvalidPacketException>(badSource.Build);
    }

    [Fact]
    public void ContentHashIgnoresIdentityTimeAndSource()
    {
        var a = Samples.Draft();
        var b = Samples.Builder(new ManualTime(Samples.Start.AddHours(1)))
            .AddItem(PacketItemKind.Image, "other-name.png", "image/png", Samples.Png())
            .AddText("selection.txt", Samples.SecretText)
            .Build();
        var c = Samples.Draft(variant: 1);
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(a.ContentSha256, b.ContentSha256);
        Assert.NotEqual(a.ContentSha256, c.ContentSha256);
    }
}
