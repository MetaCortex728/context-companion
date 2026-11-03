using ContextCompanion.Core.Delivery;
using ContextCompanion.Core.Packets;
using ContextCompanion.Core.Privacy;

namespace ContextCompanion.Core.Tests;

public class PrivacyPipelineTests
{
    private static readonly string[] Hidden = [Captures.Title, "CONFIDENTIAL-TITLE-MARKER", "private-user-marker", Captures.WindowClass];

    [Fact]
    public void DefaultSettingsQueueAPacketWithOnlyTheDefaultSourceFields()
    {
        using var harness = new PipelineHarness();
        var image = TestImages.Pattern(40, 30, padding: 8);
        byte[] before = image.Pixels.ToArray();

        var result = harness.Process(Captures.Region(image, text: "hello", windows: []) with { Note = "my note" });

        Assert.Equal(CaptureStatus.Queued, result.Status);
        Assert.Equal(before, image.Pixels); // the caller's buffer is never changed
        var packet = PacketReader.Read(harness.Stored(result.PacketId!));
        Assert.Equal(["capture.png", "selection.txt", "note.txt"], packet.Manifest.Items.Select(i => i.File));
        Assert.Equal(PacketItemKind.Note, packet.Manifest.Items[2].Kind);
        var source = packet.Manifest.Source!;
        Assert.Equal("editor.exe", source.ProcessName);
        Assert.Equal("region", source.CaptureMode);
        Assert.Equal(new PacketRegion(100, 200, 40, 30), source.Region);
        Assert.Equal(1.25, source.Scale);
        Assert.Null(source.WindowTitle);
        Assert.Null(source.ExecutablePath);
        Assert.Null(source.WindowClass);

        var decoded = PngReader.Decode(File.ReadAllBytes(Path.Combine(packet.Directory, "capture.png")));
        Assert.Equal(PngReader.Tight(image), decoded.Pixels);
    }

    [Fact]
    public void DisabledFieldsAppearNowhereNotInTheManifestTheAuditLogOrAnErrorMessage()
    {
        using var harness = new PipelineHarness(options: new OutboxOptions { MaxPendingPackets = 1 });
        var first = harness.Process(Captures.Region(text: "first"));
        Assert.Equal(CaptureStatus.Queued, first.Status);

        // Errors raised further down the pipeline (here: the outbox is full) never carry source details.
        var full = Assert.Throws<OutboxFullException>(() => harness.Process(Captures.Region(text: "second")));
        // Errors about the request itself do not either.
        var bad = Assert.Throws<ArgumentException>(() => harness.Process(
            Captures.Region() with { Source = Captures.Source(mode: "Not A Token") }));
        var empty = Assert.Throws<ArgumentException>(() => harness.Process(
            new CaptureRequest { Source = Captures.Source() }));

        string everything = harness.EverythingWritten();
        foreach (string marker in Hidden)
        {
            Assert.DoesNotContain(marker, everything, StringComparison.Ordinal);
            Assert.DoesNotContain(marker, harness.AuditText(), StringComparison.Ordinal);
            Assert.DoesNotContain(marker, full.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(marker, bad.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(marker, empty.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(marker, first.Message, StringComparison.Ordinal);
        }
        Assert.Contains("editor.exe", everything, StringComparison.Ordinal); // enabled by default
    }

    [Fact]
    public void EnabledFieldsAreWrittenCleanedOfControlCharacters()
    {
        var settings = new PrivacySettings
        {
            Metadata = new MetadataSettings { WindowTitle = true, ExecutablePath = true, WindowClass = true, ProcessName = false, Region = false, Scale = false },
        };
        using var harness = new PipelineHarness(settings);
        var request = Captures.Region(text: "t") with
        {
            Source = Captures.Source() with { WindowTitle = "Tab\tand\nnewline " + new string('x', 2000) },
        };

        var result = harness.Process(request);

        var source = PacketReader.Read(harness.Stored(result.PacketId!)).Manifest.Source!;
        Assert.StartsWith("Tab and newline xxx", source.WindowTitle, StringComparison.Ordinal);
        Assert.Equal(PacketFormat.MaxWindowTitleLength, source.WindowTitle!.Length);
        Assert.Equal(Captures.ExecutablePath, source.ExecutablePath);
        Assert.Equal(Captures.WindowClass, source.WindowClass);
        Assert.Null(source.ProcessName);
        Assert.Null(source.Region);
        Assert.Null(source.Scale);
        Assert.Equal("region", source.CaptureMode);
    }

    [Fact]
    public void AllFieldsOffLeavesNoSourceAtAll()
    {
        var off = new MetadataSettings { ProcessName = false, CaptureMode = false, Region = false, Scale = false };
        using var harness = new PipelineHarness(new PrivacySettings { Metadata = off });
        var result = harness.Process(Captures.Region(text: "t"));
        Assert.Null(PacketReader.Read(harness.Stored(result.PacketId!)).Manifest.Source);
    }

    [Fact]
    public void RecapturingTheSameContentIsReportedAsADuplicate()
    {
        using var harness = new PipelineHarness();
        var first = harness.Process(Captures.Region(text: "same"));
        var second = harness.Process(Captures.Region(text: "same"));
        Assert.Equal(CaptureStatus.Duplicate, second.Status);
        Assert.Equal(first.PacketId, second.PacketId);
    }

    [Fact]
    public void AnImageNeedsARegion()
    {
        using var harness = new PipelineHarness();
        var request = Captures.Region() with { Source = Captures.Source() with { Region = null } };
        Assert.Throws<ArgumentException>(() => harness.Process(request));
    }
}
