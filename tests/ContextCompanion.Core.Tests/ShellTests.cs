using System.Text;
using ContextCompanion.Core.Capturing;
using ContextCompanion.Core.Packets;
using ContextCompanion.Core.Privacy;
using ContextCompanion.Core.Shell;

namespace ContextCompanion.Core.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+W", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x57)]
    [InlineData("ctrl + shift + 5", HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, 0x35)]
    [InlineData("Win+F12", HotkeyModifiers.Win, 0x7B)]
    [InlineData("Alt+PrintScreen", HotkeyModifiers.Alt, 0x2C)]
    [InlineData("Control+Alt+Space", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x20)]
    public void ParsesModifiersAndKey(string text, HotkeyModifiers modifiers, int key)
    {
        var chord = HotkeyChord.Parse(text);
        Assert.Equal(modifiers, chord.Modifiers);
        Assert.Equal(key, chord.VirtualKey);
    }

    [Theory]
    [InlineData("W", "modifiers and a key")]
    [InlineData("Shift+W", "needs Ctrl, Alt or Win")]
    [InlineData("Ctrl+Ctrl+W", "twice")]
    [InlineData("Hyper+W", "not a modifier")]
    [InlineData("Ctrl+Alt+Tab", "not a supported key")]
    [InlineData("Ctrl+Alt+F25", "not a supported key")]
    [InlineData("Ctrl++", "modifiers and a key")]
    public void RejectsWithAReason(string text, string reason)
    {
        Assert.False(HotkeyChord.TryParse(text, out _, out string? problem));
        Assert.Contains(reason, problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Ctrl+Alt+W")]
    [InlineData("Ctrl+Shift+F3")]
    [InlineData("Alt+Win+Delete")]
    public void RoundTripsThroughText(string text) =>
        Assert.Equal(HotkeyChord.Parse(text), HotkeyChord.Parse(HotkeyChord.Parse(text).ToString()));

    [Fact]
    public void DuplicateAndInvalidHotkeysAreReportedEmptyOnesSwitchOff()
    {
        var problems = new HotkeySettings { Window = "Ctrl+Alt+W", Region = "alt+ctrl+w", Text = "" }.Validate();
        Assert.Equal(["app.hotkeys.region is the same as app.hotkeys.window"], problems);
        Assert.Contains(new HotkeySettings { Text = "Q" }.Validate(), p => p.StartsWith("app.hotkeys.text:", StringComparison.Ordinal));
    }
}

public class ShellSettingsTests
{
    [Fact]
    public void DefaultsArePreviewOnGestureOffAndThreeHotkeys()
    {
        var app = PrivacySettings.Default.App;
        Assert.True(app.Preview);
        Assert.False(app.WheelGesture);
        Assert.Null(app.Inbox);
        Assert.Equal(("Ctrl+Alt+W", "Ctrl+Alt+R", "Ctrl+Alt+T"), (app.Hotkeys.Window, app.Hotkeys.Region, app.Hotkeys.Text));
    }

    [Fact]
    public void TheAppSectionIsReadFromTheSettingsFile()
    {
        var settings = PrivacySettings.Parse("""
            { "schema_version": 1,
              "app": { "inbox": "D:\\agents\\inbox", "preview": false, "wheel_gesture": true,
                       "hotkeys": { "window": "Ctrl+Shift+1", "region": "", "text": "Ctrl+Shift+3" } } }
            """);
        Assert.Equal(@"D:\agents\inbox", settings.App.Inbox);
        Assert.False(settings.App.Preview);
        Assert.True(settings.App.WheelGesture);
        Assert.Equal("", settings.App.Hotkeys.Region);
        Assert.Equal(settings.App, PrivacySettings.Parse(settings.ToJson()).App);
    }

    [Fact]
    public void InvalidAppSettingsAreRejected()
    {
        var ex = Assert.Throws<PrivacySettingsException>(() => PrivacySettings.Parse("""
            { "schema_version": 1, "app": { "hotkeys": { "window": "W" }, "pointer_region_fraction": 2 } }
            """));
        Assert.Contains(ex.Problems, p => p.StartsWith("app.hotkeys.window:", StringComparison.Ordinal));
        Assert.Contains("app.pointer_region_fraction must be from 0.1 to 1", ex.Problems);
    }
}

public class WheelGestureTests
{
    private static uint Delta(short delta) => unchecked((uint)(delta << 16));

    [Fact]
    public void TiltLeftIsWindowTiltRightIsPointerRegion()
    {
        Assert.Equal(TriggerAction.Window, WheelGesture.Classify(WheelGesture.WM_MOUSEHWHEEL, Delta(-120), 0));
        Assert.Equal(TriggerAction.PointerRegion, WheelGesture.Classify(WheelGesture.WM_MOUSEHWHEEL, Delta(120), 0));
        Assert.Null(WheelGesture.Classify(WheelGesture.WM_MOUSEHWHEEL, 0, 0));
        Assert.Null(WheelGesture.Classify(0x020A /* vertical wheel */, Delta(120), 0));
    }

    [Theory]
    [InlineData(0x01u)]
    [InlineData(0x02u)]
    public void InjectedEventsAreIgnored(uint flags) =>
        Assert.Null(WheelGesture.Classify(WheelGesture.WM_MOUSEHWHEEL, Delta(-120), flags));

    [Fact]
    public void ABurstTriggersOnceButEveryGestureEventIsSwallowed()
    {
        var gesture = new WheelGesture(TimeSpan.FromMilliseconds(700));
        Assert.Equal(TriggerAction.Window, gesture.Accept(WheelGesture.WM_MOUSEHWHEEL, Delta(-120), 0, 1000, out bool first));
        Assert.True(first);
        Assert.Null(gesture.Accept(WheelGesture.WM_MOUSEHWHEEL, Delta(-120), 0, 1300, out bool second));
        Assert.True(second);
        Assert.Null(gesture.Accept(0x0200 /* mouse move */, 0, 0, 1400, out bool move));
        Assert.False(move);
        Assert.Equal(TriggerAction.PointerRegion, gesture.Accept(WheelGesture.WM_MOUSEHWHEEL, Delta(120), 0, 1700, out _));
    }
}

public class PointerRegionTests
{
    private static readonly ScreenRect Monitor = new(-1920, 0, 1920, 1080);

    [Fact]
    public void CentredOnThePointerWithFortyPercentOfTheMonitor() =>
        Assert.Equal(new ScreenRect(-1344, 324, 768, 432), PointerRegion.Around(Monitor, -960, 540));

    [Fact]
    public void MovedInsideTheMonitorNearAnEdge()
    {
        Assert.Equal(new ScreenRect(-1920, 0, 768, 432), PointerRegion.Around(Monitor, -1915, 5));
        Assert.Equal(new ScreenRect(-768, 648, 768, 432), PointerRegion.Around(Monitor, -1, 1079));
    }

    [Fact]
    public void CornersInAnyOrderGiveTheSameInclusiveRectangle()
    {
        Assert.Equal(new ScreenRect(10, 20, 31, 11), PointerRegion.FromCorners(10, 20, 40, 30));
        Assert.Equal(new ScreenRect(10, 20, 31, 11), PointerRegion.FromCorners(40, 30, 10, 20));
    }
}

public class PreviewTests
{
    [Fact]
    public void PrepareThenQueueGivesTheSamePacketAsProcess()
    {
        using var a = new PipelineHarness();
        using var b = new PipelineHarness();
        var request = Captures.Region(text: "token=" + "abc123SECRETVALUE456");
        var direct = a.Process(request);
        b.Time.Advance(TimeSpan.FromMilliseconds(5));
        var prepared = b.Pipeline.Prepare(request);
        Assert.Null(prepared.Refusal);
        var queued = b.Pipeline.Queue(prepared.Capture!);
        Assert.Equal(CaptureStatus.Queued, queued.Status);
        Assert.Equal(a.Outbox.Find(direct.PacketId!)!.ContentSha256, b.Outbox.Find(queued.PacketId!)!.ContentSha256);
    }

    [Fact]
    public void PrepareShowsTheRedactedCaptureAndWritesNothing()
    {
        using var harness = new PipelineHarness();
        string secret = "password=" + "hunter2-PREVIEW-MARKER";
        var prepared = harness.Pipeline.Prepare(Captures.Region(text: "login with " + secret)).Capture!;
        Assert.DoesNotContain("PREVIEW-MARKER", prepared.Text, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:", prepared.Text, StringComparison.Ordinal);
        Assert.Contains(prepared.Redactions, r => r.Kind == "secret");
        Assert.Null(prepared.Source!.WindowTitle);
        Assert.Empty(harness.Outbox.Snapshot());
        Assert.False(Directory.Exists(Path.Combine(harness.Root, "inbox")) && Directory.EnumerateFileSystemEntries(Path.Combine(harness.Root, "inbox")).Any());
    }

    [Fact]
    public void ANoteAddedInThePreviewIsRedactedAndPackaged()
    {
        using var harness = new PipelineHarness();
        var prepared = harness.Pipeline.Prepare(Captures.Region()).Capture!;
        var result = harness.Pipeline.Queue(prepared, "see the error; api_key=" + "NOTE-SECRET-MARKER-1234567890");
        Assert.Equal(CaptureStatus.Queued, result.Status);
        string written = harness.EverythingWritten();
        Assert.Contains("see the error", written, StringComparison.Ordinal);
        Assert.DoesNotContain("NOTE-SECRET-MARKER", written, StringComparison.Ordinal);
        Assert.Contains(result.Redactions, r => r.Kind == "secret" && r.Item == PrivacyPipeline.NoteFileName);
    }

    [Fact]
    public void ADeniedWindowIsRefusedBeforeThePreview()
    {
        using var harness = new PipelineHarness();
        var prepared = harness.Pipeline.Prepare(Captures.Region() with { Source = Captures.Source(process: "KeePassXC.exe") });
        Assert.Null(prepared.Capture);
        Assert.Equal(CaptureStatus.Blocked, prepared.Refusal!.Status);
    }

    private static readonly WindowInfo Editor = new()
    {
        Handle = 0x10, Bounds = new ScreenRect(0, 0, 400, 300), ProcessName = "editor.exe", WindowClass = Captures.WindowClass,
        Title = Captures.Title, ExecutablePath = Captures.ExecutablePath, Scale = 1.5,
    };

    [Fact]
    public void TheReviewerSeesTheCaptureAndCanDiscardIt()
    {
        using var harness = new PipelineHarness();
        PreparedCapture? seen = null;
        var service = new CaptureService(harness.Pipeline, new FakeInventory(Editor), new FakeCapturer())
        {
            Reviewer = (capture, mode) => { seen = capture; Assert.Equal("window-wgc", mode); return ReviewDecision.Discard; },
        };
        var outcome = service.CaptureWindow(Editor.Handle);
        Assert.Equal(CaptureOutcomeStatus.Discarded, outcome.Status);
        Assert.NotNull(seen!.Image);
        Assert.Empty(harness.Outbox.Snapshot());
        Assert.DoesNotContain(Captures.Title, harness.EverythingWritten(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheReviewerCanSendWithANote()
    {
        using var harness = new PipelineHarness();
        var service = new CaptureService(harness.Pipeline, new FakeInventory(Editor), new FakeCapturer())
        {
            Reviewer = (_, _) => new ReviewDecision(true, "the button is cut off"),
        };
        var outcome = service.CaptureWindow(Editor.Handle);
        Assert.Equal(CaptureOutcomeStatus.Queued, outcome.Status);
        var stored = harness.Stored(outcome.PacketId!);
        Assert.Equal("the button is cut off", File.ReadAllText(Path.Combine(stored, PrivacyPipeline.NoteFileName), Encoding.UTF8));
    }
}
