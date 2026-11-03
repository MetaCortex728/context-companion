using System.Text.Json.Nodes;
using ContextCompanion.Core.Packets;

namespace ContextCompanion.Core.Tests;

public class PacketValidatorTests : IDisposable
{
    private readonly TempDirectory temp = new();
    private readonly string packet;

    public PacketValidatorTests() => packet = Samples.WritePacket(temp.Path);

    public void Dispose() => temp.Dispose();

    private PacketValidationResult Validate() => PacketValidator.ValidateDirectory(packet);

    private void AssertOnly(PacketProblemKind kind)
    {
        var result = Validate();
        Assert.False(result.IsValid);
        Assert.All(result.Problems, p => Assert.Equal(kind, p.Kind));
    }

    private void AssertHas(PacketProblemKind kind)
    {
        var result = Validate();
        Assert.Contains(result.Problems, p => p.Kind == kind);
    }

    [Fact]
    public void FreshPacketIsValid() => Assert.True(Validate().IsValid, string.Join("; ", Validate().Problems));

    // Manifest-level rules

    [Fact]
    public void MissingFolderIsReported() =>
        Assert.Equal(PacketProblemKind.Manifest, PacketValidator.ValidateDirectory(temp.Combine("nope")).Problems.Single().Kind);

    [Fact]
    public void MissingManifestIsReported()
    {
        File.Delete(Path.Combine(packet, "manifest.json"));
        AssertOnly(PacketProblemKind.Manifest);
    }

    [Fact]
    public void ManifestThatIsNotJsonIsReported()
    {
        File.WriteAllText(Path.Combine(packet, "manifest.json"), "{bad");
        AssertOnly(PacketProblemKind.Manifest);
    }

    [Fact]
    public void ManifestWithDuplicatePropertiesIsReported()
    {
        string path = Path.Combine(packet, "manifest.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"format\":", "\"format\": \"x\", \"format\":", StringComparison.Ordinal));
        AssertOnly(PacketProblemKind.Manifest);
    }

    [Fact]
    public void OversizedManifestIsReported()
    {
        File.WriteAllText(Path.Combine(packet, "manifest.json"), new string(' ', PacketFormat.MaxManifestBytes + 1));
        AssertOnly(PacketProblemKind.Manifest);
    }

    // Schema rules

    public static TheoryData<string> SchemaViolations => new()
    {
        "unknown-top-level", "missing-id", "missing-items", "missing-redactions", "wrong-format", "future-version",
        "string-version", "lowercase-id", "bad-id", "created-with-offset", "created-not-a-date", "items-empty",
        "items-too-many", "item-unknown-property", "item-missing-sha", "kind-unknown", "media-type-invalid",
        "media-type-kind-mismatch", "media-type-uppercase", "bytes-negative", "bytes-fraction", "sha-uppercase",
        "sha-short", "source-not-object", "source-unknown", "process-name-empty", "window-title-control",
        "window-title-too-long", "capture-mode-uppercase", "region-zero-width", "region-missing-y", "region-huge",
        "scale-zero", "scale-too-large", "scale-string", "redactions-not-array", "redaction-missing-reason",
        "redaction-unknown-property", "redaction-item-unlisted", "redaction-count-zero", "redaction-kind-spaces",
        "manifest-array",
    };

    [Theory]
    [MemberData(nameof(SchemaViolations))]
    public void SchemaViolationsAreReported(string violation)
    {
        string path = Path.Combine(packet, "manifest.json");
        if (violation == "manifest-array") { File.WriteAllText(path, "[]"); AssertOnly(PacketProblemKind.Schema); return; }
        Samples.MutateManifest(packet, m =>
        {
            var item = Samples.FirstItem(m);
            var source = m["source"]!.AsObject();
            var region = source["region"]!.AsObject();
            var redaction = m["redactions"]![0]!.AsObject();
            switch (violation)
            {
                case "unknown-top-level": m["extra"] = 1; break;
                case "missing-id": m.Remove("id"); break;
                case "missing-items": m.Remove("items"); break;
                case "missing-redactions": m.Remove("redactions"); break;
                case "wrong-format": m["format"] = "something-else"; break;
                case "future-version": m["schema_version"] = 2; break;
                case "string-version": m["schema_version"] = "1"; break;
                case "lowercase-id": m["id"] = m["id"]!.GetValue<string>().ToLowerInvariant(); break;
                case "bad-id": m["id"] = "not-a-ulid"; break;
                case "created-with-offset": m["created"] = "2026-11-03T15:04:05.123+00:00"; break;
                case "created-not-a-date": m["created"] = "yesterday"; break;
                case "items-empty": m["items"] = new JsonArray(); m["redactions"] = new JsonArray(); break;
                case "items-too-many":
                    var many = new JsonArray();
                    for (int i = 0; i <= PacketFormat.MaxItems; i++)
                    {
                        var copy = item.DeepClone().AsObject();
                        copy["file"] = $"f{i}.png";
                        many.Add(copy);
                    }
                    m["items"] = many; m["redactions"] = new JsonArray();
                    break;
                case "item-unknown-property": item["path"] = "/etc/passwd"; break;
                case "item-missing-sha": item.Remove("sha256"); break;
                case "kind-unknown": item["kind"] = "video"; break;
                case "media-type-invalid": item["media_type"] = "png"; break;
                case "media-type-kind-mismatch": item["media_type"] = "audio/wav"; break;
                case "media-type-uppercase": item["media_type"] = "image/PNG"; break;
                case "bytes-negative": item["bytes"] = -1; break;
                case "bytes-fraction": item["bytes"] = 1.5; break;
                case "sha-uppercase": item["sha256"] = item["sha256"]!.GetValue<string>().ToUpperInvariant(); break;
                case "sha-short": item["sha256"] = "abc"; break;
                case "source-not-object": m["source"] = "notepad"; break;
                case "source-unknown": source["path"] = "C:\\Users"; break;
                case "process-name-empty": source["process_name"] = ""; break;
                case "window-title-control": source["window_title"] = "line\nbreak"; break;
                case "window-title-too-long": source["window_title"] = new string('t', PacketFormat.MaxWindowTitleLength + 1); break;
                case "capture-mode-uppercase": source["capture_mode"] = "Window"; break;
                case "region-zero-width": region["width"] = 0; break;
                case "region-missing-y": region.Remove("y"); break;
                case "region-huge": region["x"] = 1L << 40; break;
                case "scale-zero": source["scale"] = 0; break;
                case "scale-too-large": source["scale"] = 17; break;
                case "scale-string": source["scale"] = "1.5"; break;
                case "redactions-not-array": m["redactions"] = new JsonObject(); break;
                case "redaction-missing-reason": redaction.Remove("reason"); break;
                case "redaction-unknown-property": redaction["removed_text"] = "hunter2"; break;
                case "redaction-item-unlisted": redaction["item"] = "elsewhere.txt"; break;
                case "redaction-count-zero": redaction["count"] = 0; break;
                case "redaction-kind-spaces": redaction["kind"] = "a secret"; break;
                default: throw new ArgumentException(violation);
            }
        });
        var result = Validate();
        Assert.False(result.IsValid);
        Assert.Contains(result.Problems, p => p.Kind is PacketProblemKind.Schema);
        Assert.Null(result.Manifest);
    }

    // File-name rules

    [Theory]
    [InlineData("../escape.png")]
    [InlineData("..")]
    [InlineData("a/b.png")]
    [InlineData("a\\b.png")]
    [InlineData("/etc/passwd")]
    [InlineData("C:x.png")]
    [InlineData("C:\\x.png")]
    [InlineData(".hidden.png")]
    [InlineData("-dash.png")]
    [InlineData("x..png")]
    [InlineData("trailing.")]
    [InlineData("space name.png")]
    [InlineData("caf\u00e9.png")]
    [InlineData("star*.png")]
    [InlineData("")]
    [InlineData("CON")]
    [InlineData("con.png")]
    [InlineData("Nul.txt")]
    [InlineData("aux")]
    [InlineData("PRN.tar.gz")]
    [InlineData("com1.png")]
    [InlineData("LPT9")]
    [InlineData("manifest.json")]
    [InlineData("ACK.JSON")]
    public void UnsafeFileNamesAreRejected(string name)
    {
        Samples.MutateManifest(packet, m => Samples.FirstItem(m)["file"] = name);
        var result = Validate();
        Assert.Contains(result.Problems, p => p.Kind == PacketProblemKind.FileName);
        Assert.Null(result.Manifest);
    }

    [Fact]
    public void OverlongFileNameIsRejected()
    {
        Samples.MutateManifest(packet, m => Samples.FirstItem(m)["file"] = new string('a', 101) + ".png");
        AssertHas(PacketProblemKind.FileName);
    }

    [Theory]
    [InlineData("capture.png")]
    [InlineData("a")]
    [InlineData("console.png")]
    [InlineData("com10.txt")]
    [InlineData("my_file-2.v1.txt")]
    public void SafeFileNamesAreAccepted(string name) => Assert.Null(PacketNames.CheckItemFileName(name));

    [Fact]
    public void DuplicateFileNamesIgnoringCaseAreRejected()
    {
        Samples.MutateManifest(packet, m => m["items"]![1]!["file"] = "CAPTURE.PNG");
        AssertHas(PacketProblemKind.FileName);
    }

    // Folder contents

    [Fact]
    public void MissingFileIsReported()
    {
        File.Delete(Path.Combine(packet, "capture.png"));
        AssertOnly(PacketProblemKind.MissingFile);
    }

    [Fact]
    public void ExtraFileIsReported()
    {
        File.WriteAllText(Path.Combine(packet, "extra.txt"), "x");
        AssertOnly(PacketProblemKind.ExtraFile);
    }

    [Fact]
    public void SubfolderIsReported()
    {
        Directory.CreateDirectory(Path.Combine(packet, "nested"));
        AssertOnly(PacketProblemKind.ExtraFile);
    }

    [Fact]
    public void SubfolderInPlaceOfAnItemIsReported()
    {
        File.Delete(Path.Combine(packet, "capture.png"));
        Directory.CreateDirectory(Path.Combine(packet, "capture.png"));
        AssertOnly(PacketProblemKind.ExtraFile);
    }

    [Fact]
    public void SymbolicLinkInPlaceOfAnItemIsReported()
    {
        string item = Path.Combine(packet, "capture.png");
        string outside = temp.Combine("outside.png");
        File.Move(item, outside);
        try { File.CreateSymbolicLink(item, outside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // Windows without the symbolic-link privilege; the rule is still exercised on Linux CI.
        }
        AssertOnly(PacketProblemKind.ExtraFile);
    }

    [Fact]
    public void SizeMismatchIsReported()
    {
        File.AppendAllText(Path.Combine(packet, "capture.png"), "x");
        AssertOnly(PacketProblemKind.Size);
    }

    [Fact]
    public void HashMismatchIsReported()
    {
        string path = Path.Combine(packet, "capture.png");
        byte[] bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0xff;
        File.WriteAllBytes(path, bytes);
        AssertOnly(PacketProblemKind.Hash);
    }

    [Fact]
    public void ClaimedTotalAboveTheLimitIsReported()
    {
        Samples.MutateManifest(packet, m =>
        {
            Samples.FirstItem(m)["bytes"] = PacketFormat.MaxPacketBytes;
            m["items"]![1]!["bytes"] = 1;
        });
        AssertHas(PacketProblemKind.Size);
    }

    [Fact]
    public void FolderNameMustMatchId()
    {
        string other = Path.Combine(temp.Path, "01ARYZ6S41TSV4RRFFQ69G5FAV");
        Directory.Move(packet, other);
        var result = PacketValidator.ValidateDirectory(other);
        Assert.Equal(PacketProblemKind.Identity, result.Problems.Single().Kind);
    }

    // Acknowledgements inside the packet

    [Fact]
    public void ValidAcknowledgementIsAllowed()
    {
        PacketAcknowledgements.Write(packet, "agent-1");
        var result = Validate();
        Assert.True(result.IsValid);
        Assert.Equal("agent-1", result.Acknowledgement!.Consumer);
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("{}")]
    [InlineData("""{"format":"context-companion.ack","schema_version":1,"id":"01ARYZ6S41TSV4RRFFQ69G5FAV","consumer":"a","consumed":"2026-11-03T15:04:05Z"}""")]
    [InlineData("""{"format":"context-companion.ack","schema_version":1,"id":"@ID","consumer":"has space","consumed":"2026-11-03T15:04:05Z"}""")]
    [InlineData("""{"format":"context-companion.ack","schema_version":1,"id":"@ID","consumer":"a","consumed":"2026-11-03"}""")]
    [InlineData("""{"format":"context-companion.ack","schema_version":1,"id":"@ID","consumer":"a","consumed":"2026-11-03T15:04:05Z","extra":1}""")]
    public void InvalidAcknowledgementIsReported(string json)
    {
        File.WriteAllText(Path.Combine(packet, "ack.json"), json.Replace("@ID", Path.GetFileName(packet), StringComparison.Ordinal));
        AssertOnly(PacketProblemKind.Acknowledgement);
    }
}
