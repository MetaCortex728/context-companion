using System.Security.Cryptography;
using System.Text;
using ContextCompanion.Core.Delivery;
using ContextCompanion.Core.Packets;
using ContextCompanion.Core.Privacy;
using ContextCompanion.Tool;

namespace ContextCompanion.Core.Tests;

public class EncryptionTests
{
    private const string Text = "plaintext-marker-5d1e";

    private static PrivacySettings Encrypted(string protection = "test-xor") =>
        new() { Encryption = new EncryptionSettings { Enabled = true, Protection = protection } };

    private static (int Exit, string Output, string Error) Run(CliEnvironment environment, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exit = Cli.Run(args, output, error, environment);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public void EncryptionIsOffByDefault()
    {
        Assert.False(PrivacySettings.Default.Encryption.Enabled);
        Assert.Equal("dpapi-current-user", PrivacySettings.Default.Encryption.Protection);
    }

    [Fact]
    public void EncryptedPacketsStoreOnlyTheEncryptedFormAndKeepPlaintextHashesInTheManifest()
    {
        var protector = new TestProtector();
        using var harness = new PipelineHarness(Encrypted(), protector: protector);
        var image = TestImages.Pattern(40, 30);

        var result = harness.Process(Captures.Region(image, Text));

        string folder = harness.Stored(result.PacketId!);
        Assert.Equal(["capture.png.testxor", "manifest.json", "selection.txt.testxor"],
            Directory.EnumerateFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(Text, harness.EverythingWritten(), StringComparison.Ordinal);

        var manifest = PacketReader.Read(folder).Manifest;
        var text = manifest.Items.Single(i => i.File == "selection.txt");
        byte[] plaintext = Encoding.UTF8.GetBytes(Text);
        Assert.Equal(plaintext.LongLength, text.Bytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(plaintext)), text.Sha256);
        byte[] stored = File.ReadAllBytes(Path.Combine(folder, "selection.txt.testxor"));
        Assert.Equal(new PacketStoredForm
        {
            File = "selection.txt.testxor", Bytes = stored.LongLength,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(stored)), Protection = "test-xor",
        }, text.Stored);
        Assert.Equal(plaintext, protector.Unprotect(stored));
    }

    [Fact]
    public void TheValidatorChecksTheStoredFormAndThePlaintextWhenItCanDecrypt()
    {
        var protector = new TestProtector();
        using var harness = new PipelineHarness(Encrypted(), protector: protector);
        string folder = harness.Stored(harness.Process(Captures.Region(text: Text)).PacketId!);

        var withoutKey = PacketValidator.ValidateDirectory(folder);
        Assert.True(withoutKey.IsValid);
        Assert.Equal(2, withoutKey.PlaintextUnchecked);
        var withKey = PacketValidator.ValidateDirectory(folder, [protector]);
        Assert.True(withKey.IsValid);
        Assert.Equal(0, withKey.PlaintextUnchecked);

        // A stored file whose hash matches but whose plaintext does not: only a decrypting check can tell.
        string path = Path.Combine(folder, "selection.txt.testxor");
        byte[] forged = protector.Protect(Encoding.UTF8.GetBytes("something else"));
        File.WriteAllBytes(path, forged);
        Samples.MutateManifest(folder, m =>
        {
            var stored = m["items"]![1]!["stored"]!.AsObject();
            stored["bytes"] = forged.Length;
            stored["sha256"] = Convert.ToHexStringLower(SHA256.HashData(forged));
        });
        Assert.True(PacketValidator.ValidateDirectory(folder).IsValid);
        var problem = Assert.Single(PacketValidator.ValidateDirectory(folder, [protector]).Problems);
        Assert.Equal(PacketProblemKind.Protection, problem.Kind);

        // A changed stored file fails the stored hash without any key.
        forged[^1] ^= 1;
        File.WriteAllBytes(path, forged);
        Assert.Equal(PacketProblemKind.Hash, Assert.Single(PacketValidator.ValidateDirectory(folder).Problems).Kind);
    }

    [Fact]
    public void ADpapiStoredFileMustBeNamedAfterTheItem()
    {
        using var harness = new PipelineHarness(Encrypted("dpapi-current-user"), protector: new TestProtector("dpapi-current-user", "dpapi"));
        string folder = harness.Stored(harness.Process(Captures.Region(text: Text)).PacketId!);
        Assert.True(PacketValidator.ValidateDirectory(folder).IsValid);
        File.Move(Path.Combine(folder, "selection.txt.dpapi"), Path.Combine(folder, "other.dpapi"));
        Samples.MutateManifest(folder, m => m["items"]![1]!["stored"]!["file"] = "other.dpapi");
        Assert.Contains(PacketValidator.ValidateDirectory(folder).Problems,
            p => p.Kind == PacketProblemKind.Schema && p.Message.Contains(".dpapi", StringComparison.Ordinal));
    }

    [Fact]
    public void EnabledEncryptionWithoutAProtectorRefusesToStart()
    {
        using var temp = new TempDirectory();
        var outbox = new Outbox(temp.Combine("outbox"));
        var ex = Assert.Throws<InvalidOperationException>(() => new PrivacyPipeline(Encrypted("dpapi-current-user"), outbox, "inbox"));
        Assert.Contains("dpapi-current-user", ex.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => new PrivacyPipeline(Encrypted("dpapi-current-user"), outbox, "inbox", new TestProtector()));
    }

    [Fact]
    public async Task EncryptedPacketsAreDeliveredEncryptedAndCcompOpenWritesAVerifiedPlaintextCopy()
    {
        var protector = new TestProtector();
        using var harness = new PipelineHarness(Encrypted(), protector: protector);
        var image = TestImages.Pattern(40, 30);
        var result = harness.Process(Captures.Region(image, Text) with { Note = "note-marker" });
        using var queue = new DeliveryQueue(harness.Outbox, [harness.Inbox], time: harness.Time);
        await queue.DeliverQueuedAsync(CancellationToken.None);
        string delivered = Path.Combine(harness.Inbox.Root, result.PacketId!);
        Assert.True(File.Exists(Path.Combine(delivered, "selection.txt.testxor")));
        Assert.False(File.Exists(Path.Combine(delivered, "selection.txt")));

        var environment = new CliEnvironment { Protectors = [protector] };
        var validate = Run(environment, "validate", delivered);
        Assert.Equal(Cli.Ok, validate.Exit);
        Assert.Contains("3 encrypted", validate.Output, StringComparison.Ordinal);
        Assert.Contains("not checked here", Run(new CliEnvironment { Protectors = [] }, "validate", delivered).Output, StringComparison.Ordinal);

        string target = Path.Combine(harness.Root, "opened");
        var open = Run(environment, "open", delivered, "--to", target);
        Assert.Equal(Cli.Ok, open.Exit);
        Assert.Contains("3 decrypted, plaintext sizes and SHA-256 verified", open.Output, StringComparison.Ordinal);
        string copy = Path.Combine(target, result.PacketId!);
        var packet = PacketReader.Read(copy);
        Assert.All(packet.Manifest.Items, i => Assert.Null(i.Stored));
        Assert.Equal(Text, File.ReadAllText(Path.Combine(copy, "selection.txt")));
        Assert.Equal("note-marker", File.ReadAllText(Path.Combine(copy, "note.txt")));
        Assert.Equal(PngReader.Tight(image), PngReader.Decode(File.ReadAllBytes(Path.Combine(copy, "capture.png"))).Pixels);
        Assert.Equal(Cli.Ok, Run(environment, "validate", copy).Exit);

        // Opening again does not overwrite the copy.
        Assert.Equal(Cli.Failed, Run(environment, "open", delivered, "--to", target).Exit);
    }

    [Fact]
    public void CcompOpenRefusesDpapiPacketsWhenItCannotDecryptThem()
    {
        using var harness = new PipelineHarness(Encrypted("dpapi-current-user"), protector: new TestProtector("dpapi-current-user", "dpapi"));
        string folder = harness.Stored(harness.Process(Captures.Region(text: Text)).PacketId!);
        string target = Path.Combine(harness.Root, "opened");

        var result = Run(new CliEnvironment { Protectors = [] }, "open", folder, "--to", target);

        Assert.Equal(Cli.Failed, result.Exit);
        Assert.Contains("Nothing was written", result.Error, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
            Assert.Contains("can be decrypted only on Windows, by the user account that created it", result.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void CcompOpenRefusesAPacketThatDoesNotDecryptToItsHashes()
    {
        var protector = new TestProtector();
        using var harness = new PipelineHarness(Encrypted(), protector: protector);
        string folder = harness.Stored(harness.Process(Captures.Region(text: Text)).PacketId!);
        string path = Path.Combine(folder, "selection.txt.testxor");
        byte[] other = protector.Protect(Encoding.UTF8.GetBytes("different"));
        File.WriteAllBytes(path, other);
        Samples.MutateManifest(folder, m =>
        {
            var stored = m["items"]![1]!["stored"]!.AsObject();
            stored["bytes"] = other.Length;
            stored["sha256"] = Convert.ToHexStringLower(SHA256.HashData(other));
        });
        string target = Path.Combine(harness.Root, "opened");

        var result = Run(new CliEnvironment { Protectors = [protector] }, "open", folder, "--to", target);

        Assert.Equal(Cli.Failed, result.Exit);
        Assert.Contains("Protection", result.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(target, Path.GetFileName(folder))));
    }

    [Fact]
    public void CcompOpenCopiesAPlaintextPacketAnywhere()
    {
        using var temp = new TempDirectory();
        string packet = Samples.WritePacket(temp.Combine("inbox"));
        var result = Run(new CliEnvironment { Protectors = [] }, "open", packet, "--to", temp.Combine("out"));
        Assert.Equal(Cli.Ok, result.Exit);
        Assert.Contains("0 decrypted", result.Output, StringComparison.Ordinal);
        Assert.True(PacketValidator.ValidateDirectory(temp.Combine("out", Path.GetFileName(packet))).IsValid);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("open", "packet")]
    [InlineData("open", "packet", "--to")]
    [InlineData("open", "a", "b", "--to", "c")]
    [InlineData("open", "packet", "--to", "c", "--force")]
    public void OpenUsageErrorsExitWithTwo(params string[] args) => Assert.Equal(Cli.Usage, Run(new CliEnvironment(), args).Exit);
}
