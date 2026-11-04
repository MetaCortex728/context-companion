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

    [Fact]
    public async Task ADenylistedWindowInTheRegionAndASecretInTheTextBecomeARedactedPacketWithAContentFreeAuditLog()
    {
        // Every source field is switched on except title and path, which are off by default.
        using var harness = new PipelineHarness();
        string token = FakeSecrets.GitHubToken();
        var request = new CaptureRequest
        {
            Image = TestImages.Pattern(40, 30),
            Text = "deploy with GITHUB_TOKEN=" + token + " tonight",
            Note = "see " + FakeSecrets.Jwt(),
            Source = Captures.Source(),
            VisibleWindows =
            [
                new VisibleWindow { Bounds = new ScreenRect(120, 210, 100, 100), ProcessName = "KeePassXC.exe", Title = "Secret vault TITLE-OF-DENYLISTED" },
                new VisibleWindow { Bounds = new ScreenRect(100, 200, 10, 10), ProcessName = "explorer.exe", Title = "Desktop" },
            ],
        };

        var result = harness.Process(request);
        Assert.Equal(CaptureStatus.Queued, result.Status);
        using var queue = new DeliveryQueue(harness.Outbox, [harness.Inbox], time: harness.Time);
        Assert.Equal(1, await queue.DeliverQueuedAsync(CancellationToken.None));

        var packet = PacketReader.Read(Path.Combine(harness.Inbox.Root, result.PacketId!));
        // Pixels: the KeePassXC window covers x >= 20, y >= 10 of the 40x30 image; the rest is untouched.
        var image = PngReader.Decode(File.ReadAllBytes(Path.Combine(packet.Directory, "capture.png")));
        var original = TestImages.Pattern(40, 30);
        for (int y = 0; y < 30; y++)
            for (int x = 0; x < 40; x++)
            {
                bool covered = x >= 20 && y >= 10;
                Assert.Equal(covered, TestImages.IsOpaqueBlack(image, x, y));
                if (!covered)
                    Assert.Equal(original.Pixels.AsSpan(y * original.Stride + x * 4, 4).ToArray(), image.Pixels.AsSpan((y * 40 + x) * 4, 4).ToArray());
            }
        // Text and note are redacted.
        Assert.Equal("deploy with GITHUB_TOKEN=[REDACTED:github-token] tonight", File.ReadAllText(Path.Combine(packet.Directory, "selection.txt")));
        Assert.Equal("see [REDACTED:jwt]", File.ReadAllText(Path.Combine(packet.Directory, "note.txt")));
        // The manifest records every redaction, without content.
        Assert.Equal(
            [("blackout", "capture.png", "keepassxc", null, 1), ("secret", "selection.txt", null, "github-token", 1), ("secret", "note.txt", null, "jwt", 1)],
            packet.Manifest.Redactions.Select(r => (r.Kind, r.Item, r.Rule, r.Pattern, r.Count!.Value)));
        string manifest = File.ReadAllText(Path.Combine(packet.Directory, "manifest.json"));
        Assert.DoesNotContain(token, manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("FAKE0", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("TITLE-OF-DENYLISTED", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("KeePassXC.exe", manifest, StringComparison.Ordinal);

        // Nothing written anywhere (packet, outbox, audit log) holds a title, a path, the secret or the text.
        string everything = harness.EverythingWritten();
        foreach (string hidden in new[] { token, FakeSecrets.Jwt(), "FAKE0", "TITLE-OF-DENYLISTED", "Desktop", "KeePassXC.exe", "explorer.exe", Captures.Title, "private-user-marker" })
            Assert.DoesNotContain(hidden, everything, StringComparison.Ordinal);
        string audit = harness.AuditText();
        Assert.DoesNotContain("deploy", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("editor.exe", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("selection.txt", audit, StringComparison.Ordinal);
        Assert.Equal(["queued", "delivering", "delivered"], AuditLog.Read(harness.Audit.Path).Select(e => e.To));
    }

    [Fact]
    public void SecretRedactionCanBeSwitchedOff()
    {
        using var harness = new PipelineHarness(new PrivacySettings { Redaction = new RedactionSettings { Secrets = false } });
        string token = FakeSecrets.GitHubToken();
        var result = harness.Process(new CaptureRequest { Text = token, Source = Captures.Source() });
        Assert.Equal(token, File.ReadAllText(Path.Combine(harness.Stored(result.PacketId!), "selection.txt")));
        Assert.Empty(result.Redactions);
    }

    [Fact]
    public void TextThatCannotBeScannedInTimeIsWithheld()
    {
        var settings = new PrivacySettings
        {
            Redaction = new RedactionSettings { ExtraPatterns = [new SecretPattern { Name = "slow", Regex = "^(a+)+$" }] },
        };
        using var harness = new PipelineHarness(settings);
        string text = new string('a', 64) + "!";

        var withImage = harness.Process(Captures.Region(text: text));
        Assert.Equal(CaptureStatus.Queued, withImage.Status);
        var packet = PacketReader.Read(harness.Stored(withImage.PacketId!));
        Assert.Equal(["capture.png"], packet.Manifest.Items.Select(i => i.File));
        var withheld = Assert.Single(packet.Manifest.Redactions);
        Assert.Equal(("withheld", "slow", null), (withheld.Kind, withheld.Pattern, withheld.Item));

        var textOnly = harness.Process(new CaptureRequest { Text = text, Source = Captures.Source() });
        Assert.Equal(CaptureStatus.Withheld, textOnly.Status);
        Assert.Null(textOnly.PacketId);
        Assert.Single(harness.Outbox.Snapshot());
    }
}
