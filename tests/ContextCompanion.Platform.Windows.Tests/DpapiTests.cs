using System.Security.Cryptography;
using System.Text;
using ContextCompanion.Core.Delivery;
using ContextCompanion.Core.Packets;
using ContextCompanion.Core.Privacy;
using ContextCompanion.Tool;

namespace ContextCompanion.Platform.Windows.Tests;

/// <summary>A fact that runs only on Windows, and says why it is skipped elsewhere.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "DPAPI exists only on Windows; the windows-latest CI job runs this test.";
    }
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccomp-windows-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }
    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

public class DpapiTests
{
    private const string Text = "dpapi-plaintext-marker-91c2";

    [WindowsFact]
    public void RoundTripRestoresTheBytesAndTheStoredFormDiffers()
    {
        var protector = new DpapiCurrentUserProtector();
        byte[] plaintext = Encoding.UTF8.GetBytes(Text);
        byte[] stored = protector.Protect(plaintext);
        Assert.NotEqual(plaintext, stored);
        Assert.DoesNotContain(Text, Encoding.UTF8.GetString(stored), StringComparison.Ordinal);
        Assert.Equal(plaintext, protector.Unprotect(stored));
        Assert.Equal("dpapi-current-user", protector.Protection);
        Assert.Equal("dpapi", protector.FileExtension);
    }

    [WindowsFact]
    public void ChangedDataCannotBeDecrypted()
    {
        var protector = new DpapiCurrentUserProtector();
        byte[] stored = protector.Protect(Encoding.UTF8.GetBytes(Text));
        stored[^1] ^= 0xFF;
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(stored));
    }

    [WindowsFact]
    public async Task EncryptedCaptureIsDeliveredAsDpapiFilesAndCcompOpenDecryptsIt()
    {
        using var temp = new TempDirectory();
        var protector = new DpapiCurrentUserProtector();
        var outbox = new Outbox(temp.Combine("outbox"));
        var inbox = new InboxDestination(temp.Combine("inbox"));
        var settings = new PrivacySettings { Encryption = new EncryptionSettings { Enabled = true } };
        var pipeline = new PrivacyPipeline(settings, outbox, inbox.Name, protector);
        var pixels = new byte[8 * 4 * 4];
        Array.Fill(pixels, (byte)0x80);

        var result = pipeline.Process(new CaptureRequest
        {
            Image = new BgraImage(8, 4, 32, pixels),
            Text = Text,
            Source = new CaptureSource { CaptureMode = "region", Region = new ScreenRect(0, 0, 8, 4) },
        });
        using var queue = new DeliveryQueue(outbox, [inbox]);
        await queue.DeliverQueuedAsync(CancellationToken.None);

        string delivered = Path.Combine(inbox.Root, result.PacketId!);
        Assert.Equal(["capture.png.dpapi", "manifest.json", "selection.txt.dpapi"],
            Directory.EnumerateFiles(delivered).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        var validation = PacketValidator.ValidateDirectory(delivered, [protector]);
        Assert.True(validation.IsValid);
        Assert.Equal(0, validation.PlaintextUnchecked);
        Assert.All(validation.Manifest!.Items, i => Assert.Equal("dpapi-current-user", i.Stored!.Protection));

        // The Windows build of ccomp decrypts with DPAPI by default.
        var output = new StringWriter();
        var error = new StringWriter();
        int exit = Cli.Run(["open", delivered, "--to", temp.Combine("opened")], output, error);
        Assert.True(exit == Cli.Ok, error.ToString());
        Assert.Contains("2 decrypted, plaintext sizes and SHA-256 verified", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(Text, File.ReadAllText(temp.Combine("opened", result.PacketId!, "selection.txt")));
        Assert.True(PacketValidator.ValidateDirectory(temp.Combine("opened", result.PacketId!)).IsValid);
    }
}
