using System.Text;
using System.Text.Json.Nodes;
using ContextCompanion.Core.Packets;

namespace ContextCompanion.Core.Tests;

/// <summary>A temporary folder deleted after the test.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccomp-tests", Guid.NewGuid().ToString("N"));
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

/// <summary>A clock the test controls.</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

internal static class Samples
{
    public static readonly DateTimeOffset Start = new(2026, 11, 3, 15, 4, 5, 123, TimeSpan.Zero);
    public const string SecretWindowTitle = "Quarterly-plan DO-NOT-LEAK window title";
    public const string SecretText = "payload text 7f3c-unique-marker";

    public static byte[] Png(int variant = 0)
    {
        // Not a decodable image; packets do not inspect payload formats.
        var bytes = new byte[64];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        bytes[20] = (byte)variant;
        return bytes;
    }

    public static PacketBuilder Builder(TimeProvider? time = null) => new(time: time ?? new ManualTime(Start));

    public static PacketDraft Draft(TimeProvider? time = null, int variant = 0, string text = SecretText) =>
        Builder(time)
            .AddItem(PacketItemKind.Image, "capture.png", "image/png", Png(variant))
            .AddText("selection.txt", text)
            .WithSource(new PacketSource
            {
                ProcessName = "notepad.exe",
                WindowTitle = SecretWindowTitle,
                CaptureMode = "region",
                Region = new PacketRegion(-1920, 10, 640, 480),
                Scale = 1.5,
            })
            .AddRedaction(new PacketRedaction { Kind = "secret", Reason = "API key pattern removed", Item = "selection.txt", Count = 1 })
            .Build();

    /// <summary>Writes a valid packet into <paramref name="inbox"/> and returns its folder.</summary>
    public static string WritePacket(string inbox, TimeProvider? time = null, int variant = 0) =>
        new InboxWriter(inbox).Write(Draft(time, variant));

    public static void MutateManifest(string packetDirectory, Action<JsonObject> change)
    {
        string path = System.IO.Path.Combine(packetDirectory, PacketFormat.ManifestFileName);
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        change(root);
        File.WriteAllText(path, root.ToJsonString(), new UTF8Encoding(false));
    }

    public static JsonObject FirstItem(JsonObject manifest) => manifest["items"]![0]!.AsObject();
}
