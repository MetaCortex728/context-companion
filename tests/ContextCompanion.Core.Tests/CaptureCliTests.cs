using ContextCompanion.Core.Capturing;
using ContextCompanion.Core.Packets;
using ContextCompanion.Core.Privacy;
using ContextCompanion.Tool;

namespace ContextCompanion.Core.Tests;

public class CaptureCliTests
{
    private const string VaultTitle = "Vault - CLI-TITLE-MARKER-77";

    private static readonly WindowInfo Editor = new()
    {
        Handle = 0x1A2B, Bounds = new ScreenRect(0, 0, 400, 300), ProcessName = "editor.exe", WindowClass = "EditorWindow",
        Title = Captures.Title, ExecutablePath = Captures.ExecutablePath, Scale = 1.25,
    };

    private static readonly WindowInfo Vault = new()
    {
        Handle = 0x2000, Bounds = new ScreenRect(10, 10, 50, 40), ProcessName = "KeePassXC.exe", WindowClass = "Qt5QWindowIcon",
        Title = VaultTitle, Scale = 1.25,
    };

    private static (int Exit, string Output, string Error) Run(CliEnvironment environment, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exit = Cli.Run(args, output, error, environment);
        return (exit, output.ToString(), error.ToString());
    }

    private static CliEnvironment Environment(TempDirectory temp, IWindowInventory windows, IScreenCapturer? screen = null,
        TextAcquisition? text = null, List<TimeSpan>? sleeps = null) => new()
    {
        Capture = new CaptureBackend
        {
            Windows = windows,
            Screen = screen ?? new FakeCapturer(),
            Text = _ => text ?? TextAcquisition.Create(new FakeSelection(), new FakeClipboard(), new FakeKeyboard(), new CaptureSettings()),
        },
        DefaultOutbox = temp.Combine("outbox"),
        Sleep = d => sleeps?.Add(d),
    };

    [Fact]
    public void WithoutACaptureBackendTheCommandsRefuseAndCaptureNothing()
    {
        using var temp = new TempDirectory();
        var environment = new CliEnvironment { Capture = null, DefaultOutbox = temp.Combine("outbox") };
        foreach (var args in new[]
        {
            new[] { "capture", "window", "--foreground", "--inbox", temp.Combine("inbox") },
            new[] { "capture", "region", "--rect", "0,0,10,10", "--inbox", temp.Combine("inbox") },
            new[] { "capture", "text", "--inbox", temp.Combine("inbox") },
        })
        {
            var (exit, output, error) = Run(environment, args);
            Assert.Equal(Cli.Failed, exit);
            Assert.Contains("Nothing was captured.", error, StringComparison.Ordinal);
            Assert.Equal("", output);
        }
        Assert.False(Directory.Exists(temp.Combine("inbox")));
    }

    [Fact]
    public void TheNet10BuildHasNoCaptureBackend() => Assert.Null(new CliEnvironment().Capture);

    [Fact]
    public void RegionCaptureIsDeliveredToTheInboxAndPrintsIdModeAndRedactionsButNoTitle()
    {
        using var temp = new TempDirectory();
        var sleeps = new List<TimeSpan>();
        var environment = Environment(temp, new FakeInventory(Vault, Editor), sleeps: sleeps);

        var (exit, output, error) = Run(environment, "capture", "region", "--rect", "0,0,200,160", "--inbox", temp.Combine("inbox"), "--delay", "1.5");

        Assert.True(exit == Cli.Ok, error);
        Assert.Equal([TimeSpan.FromSeconds(1.5)], sleeps);
        string id = Assert.Single(Directory.EnumerateDirectories(temp.Combine("inbox")).Select(Path.GetFileName), n => !n!.StartsWith('.'))!;
        Assert.Contains($"Packet {id}: mode region-wgc, delivered.", output, StringComparison.Ordinal);
        Assert.Contains("Redactions: blackout keepassxc x1.", output, StringComparison.Ordinal);
        Assert.True(PacketValidator.ValidateDirectory(temp.Combine("inbox", id)).IsValid);
        foreach (string secret in new[] { VaultTitle, Captures.Title, "private-user-marker" })
        {
            Assert.DoesNotContain(secret, output + error, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, File.ReadAllText(temp.Combine("inbox", ".audit.jsonl")), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, File.ReadAllText(temp.Combine("inbox", id, "manifest.json")), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WindowCaptureByHandleUsesTheSettingsFile()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.Combine("settings.json"), """{"schema_version":1,"capture":{"max_long_edge":20}}""");
        var environment = Environment(temp, new FakeInventory(Editor), new FakeCapturer("gdi"));

        var (exit, output, error) = Run(environment, "capture", "window", "--hwnd", "0x1a2b", "--inbox", temp.Combine("inbox"),
            "--settings", temp.Combine("settings.json"));

        Assert.True(exit == Cli.Ok, error);
        Assert.Contains("mode window-gdi, delivered.", output, StringComparison.Ordinal);
        Assert.Contains("Redactions: none.", output, StringComparison.Ordinal);
        string id = Directory.EnumerateDirectories(temp.Combine("inbox")).Select(Path.GetFileName).Single(n => !n!.StartsWith('.'))!;
        var image = PngReader.Decode(File.ReadAllBytes(temp.Combine("inbox", id, "capture.png")));
        Assert.Equal((20, 15), (image.Width, image.Height));
    }

    [Fact]
    public void ARefusedWindowExitsWithFailureAndAnAuditEntry()
    {
        using var temp = new TempDirectory();
        var environment = Environment(temp, new FakeInventory(Vault));

        var (exit, output, error) = Run(environment, "capture", "window", "--foreground", "--inbox", temp.Combine("inbox"));

        Assert.Equal(Cli.Failed, exit);
        Assert.Contains("denylist rule 'keepassxc'", error, StringComparison.Ordinal);
        Assert.DoesNotContain(VaultTitle, output + error, StringComparison.Ordinal);
        Assert.Contains("\"to\":\"blocked\"", File.ReadAllText(temp.Combine("inbox", ".audit.jsonl")), StringComparison.Ordinal);
    }

    [Fact]
    public void TextCaptureInATerminalReportsEachSourceAndFails()
    {
        using var temp = new TempDirectory();
        var terminal = Editor with { ProcessName = "WindowsTerminal.exe", WindowClass = "CASCADIA_HOSTING_WINDOW_CLASS" };
        var environment = Environment(temp, new FakeInventory(terminal));

        var (exit, output, error) = Run(environment, "capture", "text", "--inbox", temp.Combine("inbox"));

        Assert.Equal(Cli.Failed, exit);
        Assert.Contains("text source copy: skipped, not sent to a console or terminal window", output, StringComparison.Ordinal);
        Assert.Contains("No text captured", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TextCaptureFromTheClipboardWhenAsked()
    {
        using var temp = new TempDirectory();
        var text = TextAcquisition.Create(new FakeSelection(), new FakeClipboard("clipboard words"), new FakeKeyboard(), new CaptureSettings());
        var environment = Environment(temp, new FakeInventory(Editor), text: text);

        var (exit, output, error) = Run(environment, "capture", "text", "--clipboard", "--inbox", temp.Combine("inbox"));

        Assert.True(exit == Cli.Ok, error);
        Assert.Contains("mode text-clipboard, delivered.", output, StringComparison.Ordinal);
        Assert.DoesNotContain("clipboard words", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("capture", "screen")]
    [InlineData("capture", "window", "--inbox", "x")]
    [InlineData("capture", "window", "--foreground", "--hwnd", "0x10", "--inbox", "x")]
    [InlineData("capture", "window", "--hwnd", "zz", "--inbox", "x")]
    [InlineData("capture", "region", "--rect", "0,0,0,10", "--inbox", "x")]
    [InlineData("capture", "region", "--rect", "1,2,3", "--inbox", "x")]
    [InlineData("capture", "region", "--rect", "0,0,10,10")]
    [InlineData("capture", "text", "--inbox", "x", "--delay", "61")]
    [InlineData("capture", "text", "--inbox", "x", "--rect", "0,0,1,1")]
    public void UsageErrors(params string[] args)
    {
        using var temp = new TempDirectory();
        var (exit, _, _) = Run(Environment(temp, new FakeInventory(Editor)), args);
        Assert.Equal(Cli.Usage, exit);
    }
}
