using ContextCompanion.Core.Packets;

namespace ContextCompanion.Core.Tests;

public class AcknowledgementTests
{
    [Fact]
    public void AcknowledgementRoundTrip()
    {
        using var temp = new TempDirectory();
        string packet = Samples.WritePacket(temp.Path);
        var consumedAt = new ManualTime(Samples.Start.AddMinutes(2).AddTicks(12345));

        var written = PacketAcknowledgements.Write(packet, "claude-code.session-1", "Read for the bug report.", consumedAt);
        var read = PacketAcknowledgements.Read(packet);

        Assert.NotNull(read);
        Assert.Equal(written, read);
        Assert.Equal(Path.GetFileName(packet), read.Id);
        Assert.Equal("claude-code.session-1", read.Consumer);
        Assert.Equal(PacketJson.TruncateToMilliseconds(consumedAt.Now), read.Consumed);
        Assert.Equal("Read for the bug report.", read.Note);
        Assert.Equal(read, PacketReader.Read(packet).Acknowledgement);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Combine(".tmp")));
    }

    [Fact]
    public void NoteIsOptional()
    {
        using var temp = new TempDirectory();
        string packet = Samples.WritePacket(temp.Path);
        PacketAcknowledgements.Write(packet, "consumer");
        Assert.DoesNotContain("note", File.ReadAllText(Path.Combine(packet, "ack.json")), StringComparison.Ordinal);
        Assert.Null(PacketAcknowledgements.Read(packet)!.Note);
    }

    [Fact]
    public void UnacknowledgedPacketReadsAsNull()
    {
        using var temp = new TempDirectory();
        Assert.Null(PacketAcknowledgements.Read(Samples.WritePacket(temp.Path)));
    }

    [Fact]
    public void ExistingAcknowledgementIsNeverReplaced()
    {
        using var temp = new TempDirectory();
        string packet = Samples.WritePacket(temp.Path);
        PacketAcknowledgements.Write(packet, "first");
        Assert.Throws<IOException>(() => PacketAcknowledgements.Write(packet, "second"));
        Assert.Equal("first", PacketAcknowledgements.Read(packet)!.Consumer);
    }

    [Fact]
    public void InvalidPacketCannotBeAcknowledged()
    {
        using var temp = new TempDirectory();
        string packet = Samples.WritePacket(temp.Path);
        File.WriteAllText(Path.Combine(packet, "capture.png"), "tampered");
        Assert.Throws<InvalidPacketException>(() => PacketAcknowledgements.Write(packet, "consumer"));
        Assert.False(File.Exists(Path.Combine(packet, "ack.json")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("../x")]
    [InlineData("-leading")]
    public void ConsumerNameIsRestricted(string consumer)
    {
        using var temp = new TempDirectory();
        string packet = Samples.WritePacket(temp.Path);
        Assert.Throws<ArgumentException>(() => PacketAcknowledgements.Write(packet, consumer));
    }

    [Fact]
    public void InvalidAcknowledgementFileIsReportedOnRead()
    {
        using var temp = new TempDirectory();
        string packet = Samples.WritePacket(temp.Path);
        File.WriteAllText(Path.Combine(packet, "ack.json"), "{}");
        Assert.Throws<InvalidPacketException>(() => PacketAcknowledgements.Read(packet));
    }
}
