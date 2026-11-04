using System.Text.Json;
using ContextCompanion.Core.Delivery;
using ContextCompanion.Core.Packets;
using ContextCompanion.Core.Privacy;

namespace ContextCompanion.Core.Tests;

public class DenylistTests
{
    private static VisibleWindow Window(int x, int y, int width, int height, string process = "other.exe", string? windowClass = null, string? title = null) =>
        new() { Bounds = new ScreenRect(x, y, width, height), ProcessName = process, WindowClass = windowClass, Title = title };

    private static BgraImage DecodeImage(PipelineHarness harness, CaptureResult result) =>
        PngReader.Decode(File.ReadAllBytes(Path.Combine(harness.Stored(result.PacketId!), PrivacyPipeline.ImageFileName)));

    // Matching

    [Theory]
    [InlineData("KeePass.exe", null, null, "keepass")]
    [InlineData("keepass.EXE", null, null, "keepass")]
    [InlineData(@"C:\Program Files\KeePass Password Safe 2\KeePass.exe", null, null, "keepass")]
    [InlineData("KeePass", null, null, "keepass")]
    [InlineData("credentialuibroker.exe", null, null, "windows-credential-prompt")]
    [InlineData("notepad.exe", null, null, null)]
    [InlineData("NotKeePass.exe", null, null, null)]
    [InlineData(null, null, null, null)]
    public void DefaultRulesMatchProcessNamesIgnoringCaseFolderAndExtension(string? process, string? windowClass, string? title, string? expected) =>
        Assert.Equal(expected, Denylist.Compile(PrivacySettings.DefaultDenylist()).Match(process, windowClass, title));

    [Fact]
    public void EveryConditionOfARuleMustMatch()
    {
        var list = Denylist.Compile([
            new DenylistRule { Name = "vault-tab", ProcessName = "browser.exe", TitlePattern = @"\bvault\b" },
            new DenylistRule { Name = "dialog", WindowClass = "SecretDialogClass" },
        ]);
        Assert.Equal("vault-tab", list.Match("BROWSER.exe", null, "My VAULT - Browser"));
        Assert.Null(list.Match("browser.exe", null, "vaulting over things"));
        Assert.Null(list.Match("other.exe", null, "My vault"));
        Assert.Null(list.Match("browser.exe", null, null));
        Assert.Equal("dialog", list.Match(null, "secretdialogclass", null));
        Assert.Null(list.Match(null, "SecretDialogClass2", null));
    }

    [Fact]
    public void ATitlePatternThatTimesOutCountsAsAMatch()
    {
        var list = Denylist.Compile([new DenylistRule { Name = "slow", TitlePattern = "^(a+)+$" }]);
        Assert.Equal("slow", list.Match("x.exe", null, new string('a', 40) + "!"));
    }

    [Theory]
    [InlineData("""[{"name":"no-condition"}]""")]
    [InlineData("""[{"name":"bad name","process_name":"x.exe"}]""")]
    [InlineData("""[{"name":"","process_name":"x.exe"}]""")]
    [InlineData("""[{"name":"dup","process_name":"x.exe"},{"name":"DUP","process_name":"y.exe"}]""")]
    [InlineData("""[{"name":"bad-regex","title_pattern":"("}]""")]
    [InlineData("""[{"name":"blank","process_name":"  "}]""")]
    [InlineData("""[null]""")]
    public void InvalidRulesAreRefused(string rules) =>
        Assert.Throws<PrivacySettingsException>(() => PrivacySettings.Parse($$"""{"schema_version":1,"denylist":{{rules}}}"""));

    [Fact]
    public void TheDefaultListIsUsedUnlessTheFileReplacesIt()
    {
        Assert.Contains(PrivacySettings.Default.Denylist, r => r.ProcessName == "CredentialUIBroker.exe");
        Assert.Contains(PrivacySettings.Default.Denylist, r => r.ProcessName == "KeePass.exe");
        Assert.Equal(PrivacySettings.Default.Denylist.Count, PrivacySettings.Parse("""{"schema_version":1}""").Denylist.Count);
        Assert.Empty(PrivacySettings.Parse("""{"schema_version":1,"denylist":[]}""").Denylist);
        var custom = PrivacySettings.Parse("""{"schema_version":1,"denylist":[{"name":"mine","window_class":"X"}]}""");
        Assert.Equal("mine", Assert.Single(custom.Denylist).Name);
    }

    // Refusal

    [Fact]
    public void ADenylistedCapturedWindowIsRefusedAndOnlyTheRuleIsAudited()
    {
        using var harness = new PipelineHarness(new PrivacySettings { Metadata = new MetadataSettings { WindowTitle = true, ExecutablePath = true } });
        var request = new CaptureRequest
        {
            Image = TestImages.Pattern(40, 30),
            Text = Samples.SecretText,
            Note = "a note",
            Source = Captures.Source(process: "KeePass.exe", mode: "window"),
        };

        var result = harness.Process(request);

        Assert.Equal(CaptureStatus.Blocked, result.Status);
        Assert.Equal("keepass", result.Rule);
        Assert.Null(result.PacketId);
        Assert.Contains("keepass", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("KeePass.exe", result.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Outbox.Snapshot());
        Assert.Empty(PacketReader.ListPacketDirectories(Path.Combine(harness.Outbox.Root, Outbox.PacketsDirectoryName)));

        var entry = Assert.Single(AuditLog.Read(harness.Audit.Path));
        Assert.Equal(AuditLog.Blocked, entry.To);
        Assert.Equal("keepass", entry.Rule);
        Assert.Null(entry.Packet);
        Assert.Null(entry.Destination);
        using var line = JsonDocument.Parse(File.ReadAllText(harness.Audit.Path));
        Assert.Equal(["time", "to", "rule"], line.RootElement.EnumerateObject().Select(p => p.Name));

        string everything = harness.EverythingWritten();
        Assert.DoesNotContain(Samples.SecretText, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(Captures.Title, everything, StringComparison.Ordinal);
        Assert.DoesNotContain("KeePass.exe", everything, StringComparison.Ordinal);
    }

    [Fact]
    public void ATitleRuleRefusesEvenWhenTheTitleIsNotRecorded()
    {
        var settings = new PrivacySettings { Denylist = [new DenylistRule { Name = "payroll", TitlePattern = "payroll" }] };
        using var harness = new PipelineHarness(settings);
        var result = harness.Process(Captures.Region(text: "x"));
        Assert.Equal(CaptureStatus.Blocked, result.Status);
        Assert.DoesNotContain("Payroll", harness.AuditText(), StringComparison.Ordinal);
    }

    // Blackout

    [Fact]
    public void APartiallyOverlappingWindowIsBlackedOutClippedToTheRegion()
    {
        using var harness = new PipelineHarness();
        // Region (100,200) 40x30. The window sticks out to the left and top: visible part is x 100..110, y 200..205.
        var window = Window(90, 190, 20, 15, process: "Bitwarden.exe");
        var image = TestImages.Pattern(40, 30);

        var result = harness.Process(Captures.Region(image, null, window));

        var output = DecodeImage(harness, result);
        for (int y = 0; y < 30; y++)
            for (int x = 0; x < 40; x++)
                Assert.Equal(x < 10 && y < 5, TestImages.IsOpaqueBlack(output, x, y));
        var redaction = Assert.Single(result.Redactions);
        Assert.Equal(new PacketRedaction { Kind = "blackout", Reason = redaction.Reason, Item = "capture.png", Rule = "bitwarden", Count = 1 }, redaction);
        Assert.Equal(redaction, Assert.Single(PacketReader.Read(harness.Stored(result.PacketId!)).Manifest.Redactions));
    }

    [Fact]
    public void SeveralWindowsAreBlackedOutAndCountedPerRule()
    {
        using var harness = new PipelineHarness();
        var windows = new[]
        {
            Window(130, 220, 50, 50, process: "KeePass.exe"),         // bottom-right corner, clipped
            Window(105, 205, 5, 5, process: "keepass.exe"),           // small, inside
            Window(100, 225, 4, 5, process: "CredentialUIBroker.exe"),// bottom-left
            Window(110, 210, 5, 5, process: "notepad.exe"),           // not denylisted
            Window(500, 500, 10, 10, process: "KeePass.exe"),         // outside the region: not counted
        };

        var result = harness.Process(Captures.Region(TestImages.Pattern(40, 30), null, windows));

        var output = DecodeImage(harness, result);
        bool Expected(int x, int y) =>
            (x >= 30 && y >= 20) || (x is >= 5 and < 10 && y is >= 5 and < 10) || (x < 4 && y >= 25);
        for (int y = 0; y < 30; y++)
            for (int x = 0; x < 40; x++)
                Assert.Equal(Expected(x, y), TestImages.IsOpaqueBlack(output, x, y));
        Assert.Equal(
            [("keepass", 2), ("windows-credential-prompt", 1)],
            result.Redactions.Select(r => (r.Rule, r.Count!.Value)));
        Assert.All(result.Redactions, r => Assert.Equal("blackout", r.Kind));
    }

    [Fact]
    public void AScaledImageIsBlackedOutWithEdgesRoundedOutwards()
    {
        // The region is 40x30 screen pixels but the image is 20x15 (half size).
        var image = TestImages.Pattern(20, 15);
        Assert.True(Blackout.Fill(image, new ScreenRect(100, 200, 40, 30), new ScreenRect(103, 203, 4, 4)));
        // Screen x 103..107 -> image 1.5..3.5 -> columns 1..3 inclusive; same for rows.
        for (int y = 0; y < 15; y++)
            for (int x = 0; x < 20; x++)
                Assert.Equal(x is >= 1 and <= 3 && y is >= 1 and <= 3, TestImages.IsOpaqueBlack(image, x, y));
    }

    [Fact]
    public void NoMatchLeavesThePixelsByteIdentical()
    {
        using var harness = new PipelineHarness();
        var image = TestImages.Pattern(40, 30, padding: 12);
        byte[] before = image.Pixels.ToArray();

        var result = harness.Process(Captures.Region(image, null, Window(100, 200, 40, 30, process: "notepad.exe"), Window(90, 190, 20, 20, process: "KeePass2.exe")));

        Assert.Equal(before, image.Pixels);
        Assert.Equal(PngReader.Tight(image), DecodeImage(harness, result).Pixels);
        Assert.Empty(result.Redactions);
    }

    [Fact]
    public void TheCallersBufferIsNotChangedByABlackout()
    {
        using var harness = new PipelineHarness();
        var image = TestImages.Pattern(40, 30);
        byte[] before = image.Pixels.ToArray();
        harness.Process(Captures.Region(image, null, Window(100, 200, 40, 30, process: "KeePass.exe")));
        Assert.Equal(before, image.Pixels);
    }

    [Fact]
    public void AnEmptyDenylistBlocksNothing()
    {
        using var harness = new PipelineHarness(new PrivacySettings { Denylist = [] });
        var result = harness.Process(new CaptureRequest { Text = "x", Source = Captures.Source(process: "KeePass.exe") });
        Assert.Equal(CaptureStatus.Queued, result.Status);
    }
}
