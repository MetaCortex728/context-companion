using ContextCompanion.Core.Capturing;
using ContextCompanion.Core.Packets;
using ContextCompanion.Core.Privacy;

namespace ContextCompanion.Core.Tests;

internal sealed class FakeSelection(string? text = null) : ISelectionReader
{
    public string? Text { get; set; } = text;
    public bool Throws { get; set; }
    public int Calls { get; private set; }

    public string? ReadSelection()
    {
        Calls++;
        if (Throws) throw new TextSourceException("UI Automation is not available");
        return Text;
    }
}

internal sealed class FakeClipboard(string? text = null) : IClipboardAccess
{
    private sealed record Snapshot(string? Text, bool Sensitive) : IClipboardSnapshot { public int FormatCount => Text is null ? 0 : 1; }

    public string? Text { get; set; } = text;
    public bool Sensitive { get; set; }
    public uint SequenceNumber { get; private set; } = 7;
    public int Saves { get; private set; }
    public int Restores { get; private set; }
    public bool FailSave { get; set; }
    public bool FailRestore { get; set; }
    public bool FailReadAfterChange { get; set; }

    /// <summary>What an application does on Ctrl+C: replace the content.</summary>
    public void Put(string? text, bool sensitive = false)
    {
        Text = text; Sensitive = sensitive; SequenceNumber++;
    }

    public ClipboardText ReadText()
    {
        if (FailReadAfterChange && SequenceNumber != 7) throw new TextSourceException("the clipboard is locked by another application");
        return new ClipboardText(Text, Sensitive);
    }

    public IClipboardSnapshot Save()
    {
        if (FailSave) throw new TextSourceException("the clipboard is locked by another application");
        Saves++;
        return new Snapshot(Text, Sensitive);
    }

    public void Restore(IClipboardSnapshot snapshot)
    {
        Restores++;
        if (FailRestore) throw new TextSourceException("the clipboard is locked by another application");
        var saved = (Snapshot)snapshot;
        Put(saved.Text, saved.Sensitive);
    }
}

internal sealed class FakeKeyboard(Action? onCopy = null) : IKeyboardInput
{
    public int Copies { get; private set; }
    public bool Throws { get; set; }

    public void SendCopy()
    {
        if (Throws) throw new TextSourceException("a modifier key is held down");
        Copies++;
        onCopy?.Invoke();
    }
}

public class TextAcquisitionTests
{
    private const string Original = "what the user had on the clipboard";
    private const string Selected = "selected words";

    private sealed class Rig
    {
        public FakeSelection Selection { get; } = new();
        public FakeClipboard Clipboard { get; } = new(Original);
        public FakeKeyboard Keyboard { get; }
        public CaptureSettings Settings { get; } = new() { CopyTimeoutMs = 50 };
        public string? CopiedText { get; set; } = Selected;

        public Rig() => Keyboard = new FakeKeyboard(() => { if (CopiedText is not null) Clipboard.Put(CopiedText); });

        public TextAcquisition Acquisition => TextAcquisition.Create(Selection, Clipboard, Keyboard, Settings);

        public Task<TextAcquisitionResult> Run(string? windowClass = "Notepad", bool clipboard = false) =>
            Acquisition.AcquireAsync(new TextTarget("notepad.exe", windowClass, clipboard));
    }

    private static TextAttempt[] Attempts(TextAcquisitionResult result) => result.Attempts.ToArray();

    [Fact]
    public void TheStandardOrderIsSelectionClipboardCopy() =>
        Assert.Equal(["selection", "clipboard", "copy"], new Rig().Acquisition.Order);

    [Fact]
    public async Task ASelectionAnswersFirstAndNothingElseIsTried()
    {
        var rig = new Rig();
        rig.Selection.Text = Selected;

        var result = await rig.Run(clipboard: true);

        Assert.Equal((Selected, "selection"), (result.Text, result.Source));
        Assert.Equal([new TextAttempt("selection", TextAttemptResult.Answered, "answered")], Attempts(result));
        Assert.Equal(0, rig.Keyboard.Copies);
        Assert.Equal(0, rig.Clipboard.Saves);
    }

    [Fact]
    public async Task WithoutASelectionTheClipboardIsSkippedUnlessAskedAndTheCopyAnswersAndRestores()
    {
        var rig = new Rig();

        var result = await rig.Run();

        Assert.Equal((Selected, "copy"), (result.Text, result.Source));
        Assert.Equal(
            [TextAttemptResult.Empty, TextAttemptResult.Skipped, TextAttemptResult.Answered],
            Attempts(result).Select(a => a.Result));
        Assert.Equal("not asked for", result.Attempts[1].Detail);
        Assert.Equal(1, rig.Keyboard.Copies);
        Assert.Equal((1, 1), (rig.Clipboard.Saves, rig.Clipboard.Restores));
        Assert.Equal(Original, rig.Clipboard.Text);
    }

    [Fact]
    public async Task TheClipboardAnswersWhenAskedForExplicitly()
    {
        var rig = new Rig();
        var result = await rig.Run(clipboard: true);
        Assert.Equal((Original, "clipboard"), (result.Text, result.Source));
        Assert.Equal(0, rig.Keyboard.Copies);
    }

    [Fact]
    public async Task ClipboardContentMarkedSensitiveIsNotRead()
    {
        var rig = new Rig();
        rig.Clipboard.Sensitive = true;
        rig.CopiedText = null;

        var result = await rig.Run(clipboard: true);

        Assert.Null(result.Text);
        Assert.Equal(TextAttemptResult.Skipped, result.Attempts[1].Result);
        Assert.Contains("sensitive", result.Attempts[1].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopiedContentMarkedSensitiveIsDiscardedAndTheClipboardRestored()
    {
        var rig = new Rig();
        var keyboard = new FakeKeyboard(() => rig.Clipboard.Put("hunter2", sensitive: true));
        var acquisition = TextAcquisition.Create(rig.Selection, rig.Clipboard, keyboard, rig.Settings);

        var result = await acquisition.AcquireAsync(new TextTarget("app.exe", "AppWindow", false));

        Assert.Null(result.Text);
        Assert.Equal(TextAttemptResult.Skipped, result.Attempts[2].Result);
        Assert.Equal((Original, false), (rig.Clipboard.Text, rig.Clipboard.Sensitive));
    }

    [Theory]
    [InlineData("ConsoleWindowClass")]
    [InlineData("CASCADIA_HOSTING_WINDOW_CLASS")]
    [InlineData("mintty")]
    public async Task CtrlCIsNeverSentToATerminal(string windowClass)
    {
        var rig = new Rig();

        var result = await rig.Run(windowClass);

        Assert.Null(result.Text);
        var copy = result.Attempts[2];
        Assert.Equal(("copy", TextAttemptResult.Skipped), (copy.Source, copy.Result));
        Assert.Contains("terminal window, where Ctrl+C interrupts the running program", copy.Detail, StringComparison.Ordinal);
        Assert.Equal(0, rig.Keyboard.Copies);
        Assert.Equal(0, rig.Clipboard.Saves);
        Assert.Equal(Original, rig.Clipboard.Text);
    }

    [Fact]
    public async Task ExtraTerminalClassesFromTheSettingsAreSkippedToo()
    {
        var rig = new Rig();
        rig.Settings.ExtraTerminalWindowClasses = ["MyTerminal"];
        var result = await rig.Run("myterminal");
        Assert.Equal(TextAttemptResult.Skipped, result.Attempts[2].Result);
        Assert.Equal(0, rig.Keyboard.Copies);
    }

    [Fact]
    public async Task CopyIsSkippedWhenSwitchedOffOrTheWindowIsUnknown()
    {
        var off = new Rig();
        off.Settings.ExplicitCopy = false;
        var a = await off.Run();
        Assert.Equal(("switched off in the settings", 0), (a.Attempts[2].Detail, off.Keyboard.Copies));

        var unknown = new Rig();
        var b = await unknown.Run(windowClass: null);
        Assert.Equal(TextAttemptResult.Skipped, b.Attempts[2].Result);
        Assert.Equal(0, unknown.Keyboard.Copies);
    }

    [Fact]
    public async Task ATimeoutIsReportedAndTheClipboardIsStillRestored()
    {
        var rig = new Rig { CopiedText = null };
        var started = DateTime.UtcNow;

        var result = await rig.Run();

        Assert.Null(result.Text);
        var copy = result.Attempts[2];
        Assert.Equal(TextAttemptResult.TimedOut, copy.Result);
        Assert.Equal("the clipboard did not change within 50 ms", copy.Detail);
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(45));
        Assert.Equal((1, 1), (rig.Clipboard.Saves, rig.Clipboard.Restores));
    }

    [Fact]
    public async Task TheRestoreRunsAfterTheKeystrokeFails()
    {
        var rig = new Rig();
        rig.Keyboard.Throws = true;

        var result = await rig.Run();

        Assert.Equal(TextAttemptResult.Failed, result.Attempts[2].Result);
        Assert.Equal("a modifier key is held down", result.Attempts[2].Detail);
        Assert.Equal(1, rig.Clipboard.Restores);
    }

    [Fact]
    public async Task TheRestoreRunsAfterReadingTheCopyFails()
    {
        var rig = new Rig();
        rig.Clipboard.FailReadAfterChange = true;

        var result = await rig.Run();

        Assert.Equal(TextAttemptResult.Failed, result.Attempts[2].Result);
        Assert.Equal(1, rig.Clipboard.Restores);
    }

    [Fact]
    public async Task AFailedRestoreIsReported()
    {
        var rig = new Rig();
        rig.Clipboard.FailRestore = true;
        var result = await rig.Run();
        Assert.Equal(Selected, result.Text);
        Assert.Contains("the clipboard could not be restored", result.Attempts[2].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoKeystrokeIsSentWhenTheClipboardCannotBeSaved()
    {
        var rig = new Rig();
        rig.Clipboard.FailSave = true;
        var result = await rig.Run();
        Assert.Equal(TextAttemptResult.Failed, result.Attempts[2].Result);
        Assert.Equal(0, rig.Keyboard.Copies);
        Assert.Equal(0, rig.Clipboard.Restores);
    }

    [Fact]
    public async Task ASelectionReaderFailureFallsThroughToTheNextSource()
    {
        var rig = new Rig();
        rig.Selection.Throws = true;
        var result = await rig.Run();
        Assert.Equal(("selection", TextAttemptResult.Failed, "UI Automation is not available"),
            (result.Attempts[0].Source, result.Attempts[0].Result, result.Attempts[0].Detail));
        Assert.Equal("copy", result.Source);
    }

    [Fact]
    public async Task BlankAnswersCountAsEmpty()
    {
        var rig = new Rig { CopiedText = "   " };
        rig.Selection.Text = " \n ";
        var result = await rig.Run();
        Assert.Null(result.Text);
        Assert.Equal(TextAttemptResult.Empty, result.Attempts[0].Result);
    }
}

public class TextCaptureServiceTests
{
    private static readonly WindowInfo Notepad = new()
    {
        Handle = 0x10, Bounds = new ScreenRect(0, 0, 400, 300), ProcessName = "notepad.exe", WindowClass = "Notepad",
        Title = Captures.Title, Scale = 1.25,
    };

    [Fact]
    public async Task SelectedTextBecomesARedactedPacketWithTheSourceThatAnswered()
    {
        using var harness = new PipelineHarness();
        var selection = new FakeSelection("connect with DB_PASSWORD=hunter2-very-secret now");
        var acquisition = TextAcquisition.Create(selection, new FakeClipboard(), new FakeKeyboard(), harness.Pipeline.Settings.Capture);
        var service = new CaptureService(harness.Pipeline, new FakeInventory(Notepad), text: acquisition);

        var outcome = await service.CaptureTextAsync(includeClipboard: false);

        Assert.Equal(CaptureOutcomeStatus.Queued, outcome.Status);
        Assert.Equal("text-selection", outcome.Mode);
        var packet = PacketReader.Read(harness.Stored(outcome.PacketId!));
        Assert.Equal("text-selection", packet.Manifest.Source!.CaptureMode);
        Assert.Null(packet.Manifest.Source.Region);
        Assert.Null(packet.Manifest.Source.WindowTitle);
        Assert.Equal("secret-assignment", Assert.Single(outcome.Redactions).Pattern);
        string text = File.ReadAllText(Path.Combine(harness.Stored(outcome.PacketId!), "selection.txt"));
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Captures.Title, harness.EverythingWritten(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADenylistedForegroundWindowIsRefusedBeforeAnySourceIsTried()
    {
        var settings = new PrivacySettings
        {
            Denylist = [new DenylistRule { Name = "payroll", TitlePattern = "CONFIDENTIAL-TITLE-MARKER" }],
        };
        using var harness = new PipelineHarness(settings);
        var selection = new FakeSelection("anything");
        var clipboard = new FakeClipboard("old");
        var keyboard = new FakeKeyboard(() => clipboard.Put("copied"));
        var service = new CaptureService(harness.Pipeline, new FakeInventory(Notepad),
            text: TextAcquisition.Create(selection, clipboard, keyboard, settings.Capture));

        var outcome = await service.CaptureTextAsync(includeClipboard: true);

        Assert.Equal(CaptureOutcomeStatus.Blocked, outcome.Status);
        Assert.Equal((0, 0, 0), (selection.Calls, keyboard.Copies, clipboard.Saves));
        Assert.DoesNotContain(Captures.Title, harness.AuditText(), StringComparison.Ordinal);
        Assert.DoesNotContain("CONFIDENTIAL", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoTextGivesAReasonPerSourceAndQueuesNothing()
    {
        using var harness = new PipelineHarness();
        var terminal = Notepad with { ProcessName = "WindowsTerminal.exe", WindowClass = "CASCADIA_HOSTING_WINDOW_CLASS" };
        var service = new CaptureService(harness.Pipeline, new FakeInventory(terminal),
            text: TextAcquisition.Create(new FakeSelection(), new FakeClipboard(), new FakeKeyboard(), harness.Pipeline.Settings.Capture));

        var outcome = await service.CaptureTextAsync(includeClipboard: false);

        Assert.Equal(CaptureOutcomeStatus.NoText, outcome.Status);
        Assert.Equal(3, outcome.TextAttempts.Count);
        Assert.Contains("copy: not sent to a console or terminal window", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Outbox.Snapshot());
    }
}
