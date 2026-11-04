using ContextCompanion.Core.Capturing;
using ContextCompanion.Core.Delivery;
using ContextCompanion.Core.Packets;
using ContextCompanion.Core.Privacy;

namespace ContextCompanion.Core.Tests;

internal sealed class FakeCapturer(string method = "wgc") : IScreenCapturer
{
    public int Calls { get; private set; }
    public bool? LastCursor { get; private set; }
    public CaptureFailedException? Failure { get; set; }
    /// <summary>The frame size; by default the region's size.</summary>
    public (int Width, int Height)? Size { get; set; }

    public CapturedFrame CaptureWindow(nint window, bool includeCursor) =>
        Take(new ScreenRect(100, 200, 40, 30), includeCursor);

    public CapturedFrame CaptureRegion(ScreenRect region, bool includeCursor) => Take(region, includeCursor);

    private CapturedFrame Take(ScreenRect bounds, bool cursor)
    {
        Calls++;
        LastCursor = cursor;
        if (Failure is not null) throw Failure;
        var (w, h) = Size ?? (bounds.Width, bounds.Height);
        return new CapturedFrame(TestImages.Pattern(w, h), bounds, method, 1.5);
    }
}

internal sealed class FakeInventory(params WindowInfo[] windows) : IWindowInventory
{
    public nint Foreground { get; set; } = windows.Length > 0 ? windows[0].Handle : 0;
    public WindowInfo? Describe(nint window) => windows.FirstOrDefault(w => w.Handle == window);
    public nint ForegroundWindow() => Foreground;
    public IReadOnlyList<WindowInfo> VisibleWindows(ScreenRect area) =>
        windows.Where(w => !w.IsMinimized && !w.Bounds.Intersect(area).IsEmpty).ToArray();
}

public class CaptureServiceTests
{
    private const string VaultTitle = "Family Vault - TITLE-USED-ONLY-FOR-MATCHING-4e1d";

    private static readonly WindowInfo Editor = new()
    {
        Handle = 0x10, Bounds = new ScreenRect(0, 0, 400, 300), ProcessName = "editor.exe", WindowClass = Captures.WindowClass,
        Title = Captures.Title, ExecutablePath = Captures.ExecutablePath, Scale = 1.5,
    };

    /// <summary>A browser window recognised only by its title, on top of the editor.</summary>
    private static readonly WindowInfo VaultTab = new()
    {
        Handle = 0x20, Bounds = new ScreenRect(50, 40, 100, 80), ProcessName = "browser.exe", WindowClass = "BrowserWindow",
        Title = VaultTitle, ExecutablePath = @"C:\Program Files\Browser\browser.exe", Scale = 1.5,
    };

    private static PrivacySettings TitleRuleSettings(bool keepTitles = false, int maxLongEdge = 1568) => new()
    {
        Metadata = new MetadataSettings { WindowTitle = keepTitles },
        Denylist = [new DenylistRule { Name = "vault-tab", ProcessName = "browser.exe", TitlePattern = @"\bvault\b" }],
        Capture = new CaptureSettings { MaxLongEdge = maxLongEdge },
    };

    private static void AssertNoTitleAnywhere(PipelineHarness harness, CaptureOutcome outcome)
    {
        foreach (string secret in new[] { VaultTitle, "TITLE-USED-ONLY-FOR-MATCHING", Captures.Title, "CONFIDENTIAL-TITLE-MARKER" })
        {
            Assert.DoesNotContain(secret, harness.EverythingWritten(), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, harness.AuditText(), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, outcome.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RegionCaptureBlacksOutAWindowMatchedByTitleAndTheTitleIsWrittenNowhere()
    {
        using var harness = new PipelineHarness(TitleRuleSettings());
        var capturer = new FakeCapturer();
        var service = new CaptureService(harness.Pipeline, new FakeInventory(VaultTab, Editor), capturer);

        // The centre (150, 130) is on the editor; the vault tab (50,40)-(150,120) lies inside the region.
        var outcome = service.CaptureRegion(new ScreenRect(0, 0, 300, 260));

        Assert.Equal(CaptureOutcomeStatus.Queued, outcome.Status);
        Assert.Equal("region-wgc", outcome.Mode);
        var blackout = Assert.Single(outcome.Redactions);
        Assert.Equal(("blackout", "vault-tab", 1), (blackout.Kind, blackout.Rule, blackout.Count));

        var packet = PacketReader.Read(harness.Stored(outcome.PacketId!));
        var source = packet.Manifest.Source!;
        Assert.Null(source.WindowTitle);
        Assert.Equal("editor.exe", source.ProcessName); // the window at the region's centre
        Assert.Equal("region-wgc", source.CaptureMode);
        Assert.Equal(new PacketRegion(0, 0, 300, 260), source.Region);
        Assert.Equal(1.5, source.Scale);

        var image = PngReader.Decode(File.ReadAllBytes(Path.Combine(harness.Stored(outcome.PacketId!), "capture.png")));
        Assert.True(TestImages.IsOpaqueBlack(image, 60, 50));
        Assert.True(TestImages.IsOpaqueBlack(image, 149, 119));
        Assert.False(TestImages.IsOpaqueBlack(image, 150, 120));
        Assert.False(TestImages.IsOpaqueBlack(image, 10, 10));

        AssertNoTitleAnywhere(harness, outcome);
    }

    [Fact]
    public void WindowMatchedByTitleIsRefusedBeforeAnyPixelsAreTakenAndTheTitleIsWrittenNowhere()
    {
        using var harness = new PipelineHarness(TitleRuleSettings());
        var capturer = new FakeCapturer();
        var service = new CaptureService(harness.Pipeline, new FakeInventory(VaultTab, Editor), capturer);

        var outcome = service.CaptureWindow(VaultTab.Handle);

        Assert.Equal(CaptureOutcomeStatus.Blocked, outcome.Status);
        Assert.Equal("vault-tab", outcome.Rule);
        Assert.Null(outcome.PacketId);
        Assert.Equal(0, capturer.Calls);
        Assert.Empty(harness.Outbox.Snapshot());
        var entry = Assert.Single(AuditLog.Read(harness.Audit.Path, out _));
        Assert.Equal(("blocked", "vault-tab"), (entry.To, entry.Rule));
        AssertNoTitleAnywhere(harness, outcome);
    }

    [Fact]
    public void RegionWhoseCentreIsADenylistedWindowIsRefused()
    {
        using var harness = new PipelineHarness(TitleRuleSettings());
        var capturer = new FakeCapturer();
        var service = new CaptureService(harness.Pipeline, new FakeInventory(VaultTab, Editor), capturer);

        var outcome = service.CaptureRegion(new ScreenRect(60, 50, 80, 60));

        Assert.Equal(CaptureOutcomeStatus.Blocked, outcome.Status);
        Assert.Equal(0, capturer.Calls);
        AssertNoTitleAnywhere(harness, outcome);
    }

    [Fact]
    public void WindowCaptureKeepsTheTitleOnlyWhenTheSwitchIsOn()
    {
        using var harness = new PipelineHarness(TitleRuleSettings(keepTitles: true));
        var service = new CaptureService(harness.Pipeline, new FakeInventory(Editor), new FakeCapturer("gdi"));

        var outcome = service.CaptureForegroundWindow();

        Assert.Equal(CaptureOutcomeStatus.Queued, outcome.Status);
        Assert.Equal("window-gdi", outcome.Mode);
        var source = PacketReader.Read(harness.Stored(outcome.PacketId!)).Manifest.Source!;
        Assert.Equal(Captures.Title, source.WindowTitle);
        Assert.Null(source.ExecutablePath);
        Assert.DoesNotContain(Captures.Title, harness.AuditText(), StringComparison.Ordinal);
        Assert.DoesNotContain(Captures.Title, outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ImagesAreDownscaledToTheConfiguredLongEdge()
    {
        using var harness = new PipelineHarness(TitleRuleSettings(maxLongEdge: 20));
        var capturer = new FakeCapturer { Size = (40, 30) };
        var service = new CaptureService(harness.Pipeline, new FakeInventory(Editor), capturer);

        var outcome = service.CaptureWindow(Editor.Handle);

        var image = PngReader.Decode(File.ReadAllBytes(Path.Combine(harness.Stored(outcome.PacketId!), "capture.png")));
        Assert.Equal((20, 15), (image.Width, image.Height));
        Assert.Equal(new PacketRegion(100, 200, 40, 30), PacketReader.Read(harness.Stored(outcome.PacketId!)).Manifest.Source!.Region);
        Assert.False(capturer.LastCursor);
    }

    [Fact]
    public void MinimisedProtectedAndMissingWindowsAreRefusedWithoutCapturing()
    {
        using var harness = new PipelineHarness(TitleRuleSettings());
        var capturer = new FakeCapturer();
        var minimised = Editor with { Handle = 0x30, IsMinimized = true };
        var protectedWindow = Editor with { Handle = 0x40, IsCaptureProtected = true };
        var service = new CaptureService(harness.Pipeline, new FakeInventory(minimised, protectedWindow), capturer);

        var a = service.CaptureWindow(minimised.Handle);
        var b = service.CaptureWindow(protectedWindow.Handle);
        var c = service.CaptureWindow(0x99);

        Assert.All(new[] { a, b, c }, o => Assert.Equal(CaptureOutcomeStatus.Refused, o.Status));
        Assert.Contains("minimised", a.Message, StringComparison.Ordinal);
        Assert.Contains("protects this window", b.Message, StringComparison.Ordinal);
        Assert.Equal(0, capturer.Calls);
        Assert.Empty(harness.Outbox.Snapshot());
        AssertNoTitleAnywhere(harness, a);
        AssertNoTitleAnywhere(harness, b);
    }

    [Fact]
    public void FailedCaptureQueuesNothingAndNamesNoTitle()
    {
        using var harness = new PipelineHarness(TitleRuleSettings());
        var capturer = new FakeCapturer { Failure = new CaptureFailedException("PrintWindow failed.") };
        var service = new CaptureService(harness.Pipeline, new FakeInventory(Editor), capturer);

        var outcome = service.CaptureWindow(Editor.Handle);

        Assert.Equal(CaptureOutcomeStatus.Failed, outcome.Status);
        Assert.Equal("Not captured: PrintWindow failed.", outcome.Message);
        Assert.Empty(harness.Outbox.Snapshot());
        AssertNoTitleAnywhere(harness, outcome);
    }

    [Fact]
    public void CursorSettingIsPassedToTheCapturer()
    {
        var settings = TitleRuleSettings();
        settings.Capture.IncludeCursor = true;
        using var harness = new PipelineHarness(settings);
        var capturer = new FakeCapturer();
        new CaptureService(harness.Pipeline, new FakeInventory(Editor), capturer).CaptureRegion(new ScreenRect(0, 0, 10, 10));
        Assert.True(capturer.LastCursor);
    }
}
