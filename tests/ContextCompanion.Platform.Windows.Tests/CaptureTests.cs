using ContextCompanion.Core.Capturing;
using ContextCompanion.Core.Packets;
using ContextCompanion.Core.Privacy;
using ContextCompanion.Platform.Windows.Capture;
using ContextCompanion.Platform.Windows.Text;
using ContextCompanion.Tool;

namespace ContextCompanion.Platform.Windows.Tests;

public class CaptureTests
{
    private static void AssertColour(BgraImage image, int x, int y, Rgb expected)
    {
        int i = y * image.Stride + x * 4;
        var (b, g, r, a) = (image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2], image.Pixels[i + 3]);
        bool close = Math.Abs(b - expected.B) <= 3 && Math.Abs(g - expected.G) <= 3 && Math.Abs(r - expected.R) <= 3 && a == 255;
        Assert.True(close, $"Pixel ({x}, {y}) is B{b} G{g} R{r} A{a}, expected B{expected.B} G{expected.G} R{expected.R} A255.");
    }

    /// <summary>The centre of each 200 x 150 block, relative to the window's top-left corner.</summary>
    private static void AssertBlocks(BgraImage image, int offsetX = 0, int offsetY = 0)
    {
        for (int i = 0; i < 4; i++)
            AssertColour(image, offsetX + i % 2 * 200 + 100, offsetY + i / 2 * 150 + 75, TestWindow.Blocks[i]);
    }

    [WgcFact]
    public void WindowCaptureWithWindowsGraphicsCaptureHasTheBlocksAndTheWindowSize()
    {
        using var window = new TestWindow();
        var frame = new WindowsScreenCapturer(CaptureMethods.WgcOnly).CaptureWindow(window.Handle, includeCursor: false);
        Assert.Equal("wgc", frame.Method);
        Assert.Equal((TestWindow.Width, TestWindow.Height), (frame.Image.Width, frame.Image.Height));
        Assert.Equal((TestWindow.Width, TestWindow.Height), (frame.Bounds.Width, frame.Bounds.Height));
        Assert.NotNull(frame.Scale);
        AssertBlocks(frame.Image);
    }

    [DesktopFact]
    public void WindowCaptureWithPrintWindowHasTheBlocksAndTheWindowSize()
    {
        using var window = new TestWindow();
        var frame = new WindowsScreenCapturer(CaptureMethods.GdiOnly).CaptureWindow(window.Handle, includeCursor: false);
        Assert.Equal("gdi", frame.Method);
        Assert.Equal((TestWindow.Width, TestWindow.Height), (frame.Image.Width, frame.Image.Height));
        AssertBlocks(frame.Image);
    }

    [WgcFact]
    public void RegionCaptureWithWindowsGraphicsCaptureCropsTheMonitorFrame()
    {
        using var window = new TestWindow();
        var bounds = new WindowsWindowInventory().Describe(window.Handle)!.Bounds;
        var region = new ScreenRect(bounds.X + 50, bounds.Y + 25, 300, 250);
        var frame = new WindowsScreenCapturer(CaptureMethods.WgcOnly).CaptureRegion(region, includeCursor: false);
        Assert.Equal(("wgc", region), (frame.Method, frame.Bounds));
        Assert.Equal((300, 250), (frame.Image.Width, frame.Image.Height));
        AssertBlocks(frame.Image, -50, -25);
    }

    [DesktopFact]
    public void RegionCaptureWithBitBltHasTheBlocks()
    {
        using var window = new TestWindow();
        var bounds = new WindowsWindowInventory().Describe(window.Handle)!.Bounds;
        var frame = new WindowsScreenCapturer(CaptureMethods.GdiOnly).CaptureRegion(bounds, includeCursor: false);
        Assert.Equal("gdi", frame.Method);
        Assert.Equal((TestWindow.Width, TestWindow.Height), (frame.Image.Width, frame.Image.Height));
        AssertBlocks(frame.Image);
    }

    [DesktopFact]
    public void TheTestWindowIsListedAmongTheVisibleWindowsWithItsDetails()
    {
        using var window = new TestWindow();
        var inventory = new WindowsWindowInventory();
        var info = inventory.Describe(window.Handle);
        Assert.NotNull(info);
        Assert.Equal((TestWindow.Width, TestWindow.Height), (info.Bounds.Width, info.Bounds.Height));
        Assert.Equal(window.Title, info.Title);
        Assert.Equal(TestWindow.ClassPrefix + "Frame", info.WindowClass);
        Assert.Equal(Path.GetFileName(Environment.ProcessPath), info.ProcessName, ignoreCase: true);
        Assert.False(info.IsMinimized);
        Assert.False(info.IsCaptureProtected);

        var visible = inventory.VisibleWindows(info.Bounds);
        Assert.Contains(visible, w => w.Handle == window.Handle);
        Assert.All(visible, w => Assert.False(w.Bounds.Intersect(info.Bounds).IsEmpty));
    }

    [DesktopFact]
    public void CcompCaptureWindowDeliversAValidPacketAndPrintsNoTitle()
    {
        using var window = new TestWindow();
        using var temp = new TempDirectory();
        var output = new StringWriter();
        var error = new StringWriter();
        var environment = new CliEnvironment { DefaultOutbox = temp.Combine("outbox") };

        int exit = Cli.Run(["capture", "window", "--hwnd", window.Handle.ToString("X"), "--inbox", temp.Combine("inbox")], output, error, environment);

        Assert.True(exit == Cli.Ok, error.ToString());
        Assert.Matches(@"mode window-(wgc|gdi), delivered\.", output.ToString());
        Assert.DoesNotContain(window.Title, output.ToString() + error.ToString(), StringComparison.Ordinal);
        string packet = Directory.EnumerateDirectories(temp.Combine("inbox")).Single(d => !Path.GetFileName(d).StartsWith('.'));
        var validation = PacketValidator.ValidateDirectory(packet);
        Assert.True(validation.IsValid);
        Assert.Null(validation.Manifest!.Source!.WindowTitle);
        Assert.DoesNotContain(window.Title, File.ReadAllText(Path.Combine(temp.Combine("inbox"), ".audit.jsonl")), StringComparison.Ordinal);
    }
}

public class TextTests
{
    [DesktopFact]
    public void UiAutomationReadsTheSelectionOfAFocusedTextBox()
    {
        using var window = new TestWindow(withTextBox: true);
        window.Activate();
        Assert.Equal("beta", new UiaSelectionReader().ReadSelection());
    }

    [DesktopFact]
    public void ClipboardSaveAndRestoreRoundTripsTextAndSetsTheHistoryExclusions()
    {
        var clipboard = new WindowsClipboard();
        TestWindow.SetClipboardText("original-ccomp-text");
        Assert.Equal(new ClipboardText("original-ccomp-text", false), clipboard.ReadText());

        var snapshot = clipboard.Save();
        Assert.True(snapshot.FormatCount >= 1);
        TestWindow.SetClipboardText("something else");
        uint before = clipboard.SequenceNumber;
        clipboard.Restore(snapshot);

        Assert.NotEqual(before, clipboard.SequenceNumber);
        Assert.Equal(new ClipboardText("original-ccomp-text", false), clipboard.ReadText());
        var formats = clipboard.ExclusionFormats();
        Assert.True(formats.ContainsKey(WindowsClipboard.ExcludeFromMonitoringFormat));
        Assert.Equal(0u, formats[WindowsClipboard.HistoryFormat]);
        Assert.Equal(0u, formats[WindowsClipboard.CloudFormat]);
        Assert.Equal(1u, formats[WindowsClipboard.RestoredMarkerFormat]);
    }

    [DesktopFact]
    public void TextTheToolWritesIsExcludedFromHistoryAndReadsAsSensitive()
    {
        var clipboard = new WindowsClipboard();
        clipboard.WriteText("written by the tool");
        var formats = clipboard.ExclusionFormats();
        Assert.True(formats.ContainsKey(WindowsClipboard.ExcludeFromMonitoringFormat));
        Assert.Equal(0u, formats[WindowsClipboard.HistoryFormat]);
        Assert.Equal(0u, formats[WindowsClipboard.CloudFormat]);
        Assert.False(formats.ContainsKey(WindowsClipboard.RestoredMarkerFormat));
        Assert.True(clipboard.ReadText().MarkedSensitive);
    }

    [DesktopFact]
    public async Task ExplicitCopyReadsTheSelectionAndRestoresTheClipboard()
    {
        using var window = new TestWindow(withTextBox: true);
        var clipboard = new WindowsClipboard();
        TestWindow.SetClipboardText("kept-on-the-clipboard");
        window.Activate();
        var source = new ExplicitCopyTextSource(clipboard, new WindowsKeyboard(), new CaptureSettings { CopyTimeoutMs = 2000 });

        var result = await source.ReadAsync(new TextTarget("testhost.exe", TestWindow.ClassPrefix + "Frame", false), CancellationToken.None);

        Assert.True(result.Result == TextAttemptResult.Answered, result.Detail);
        Assert.Equal("beta", result.Text);
        Assert.Equal("kept-on-the-clipboard", clipboard.ReadText().Text);
    }
}
