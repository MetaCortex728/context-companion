using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ContextCompanion.Core.Delivery;
using ContextCompanion.Core.Privacy;

namespace ContextCompanion.Core.Tests;

internal static class TestImages
{
    /// <summary>A BGRA image where every pixel differs from its neighbours and none is opaque black.</summary>
    public static BgraImage Pattern(int width, int height, int padding = 0)
    {
        int stride = width * 4 + padding;
        var pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * stride + x * 4;
                pixels[i] = (byte)(10 + x);      // B
                pixels[i + 1] = (byte)(20 + y);  // G
                pixels[i + 2] = (byte)(30 + x + y); // R
                pixels[i + 3] = 200;             // A
            }
        for (int y = 0; y < height; y++)
            for (int p = 0; p < padding; p++) pixels[y * stride + width * 4 + p] = 0xEE;
        return new BgraImage(width, height, stride, pixels);
    }

    public static bool IsOpaqueBlack(BgraImage image, int x, int y)
    {
        int i = y * image.Stride + x * 4;
        return image.Pixels[i] == 0 && image.Pixels[i + 1] == 0 && image.Pixels[i + 2] == 0 && image.Pixels[i + 3] == 255;
    }
}

/// <summary>Decodes the PNG files that <see cref="PngEncoder"/> writes (RGBA, filter 0) back into BGRA.</summary>
internal static class PngReader
{
    public static BgraImage Decode(byte[] png)
    {
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        int width = 0, height = 0;
        using var idat = new MemoryStream();
        int at = 8;
        while (at < png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            string type = Encoding.ASCII.GetString(png, at + 4, 4);
            var data = png.AsSpan(at + 8, length);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                Assert.Equal(8, data[8]);
                Assert.Equal(6, data[9]);
            }
            else if (type == "IDAT") idat.Write(data);
            at += 12 + length;
        }
        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        byte[] rows = raw.ToArray();
        Assert.Equal((1 + width * 4) * height, rows.Length);
        var pixels = new byte[width * 4 * height];
        for (int y = 0; y < height; y++)
        {
            int row = y * (1 + width * 4);
            Assert.Equal(0, rows[row]);
            for (int x = 0; x < width; x++)
            {
                int s = row + 1 + x * 4, d = (y * width + x) * 4;
                pixels[d] = rows[s + 2];
                pixels[d + 1] = rows[s + 1];
                pixels[d + 2] = rows[s];
                pixels[d + 3] = rows[s + 3];
            }
        }
        return new BgraImage(width, height, width * 4, pixels);
    }

    /// <summary>The image without row padding, for byte comparisons.</summary>
    public static byte[] Tight(BgraImage image)
    {
        var bytes = new byte[image.Width * 4 * image.Height];
        for (int y = 0; y < image.Height; y++)
            Array.Copy(image.Pixels, y * image.Stride, bytes, y * image.Width * 4, image.Width * 4);
        return bytes;
    }
}

internal static class Captures
{
    public const string Title = "Payroll 2026 - CONFIDENTIAL-TITLE-MARKER";
    public const string ExecutablePath = @"C:\Users\private-user-marker\AppData\Local\editor\editor.exe";
    public const string WindowClass = "EditorWindowClass";

    public static CaptureSource Source(string process = "editor.exe", string mode = "region", ScreenRect? region = null) => new()
    {
        ProcessName = process,
        WindowClass = WindowClass,
        WindowTitle = Title,
        ExecutablePath = ExecutablePath,
        CaptureMode = mode,
        Region = region ?? new ScreenRect(100, 200, 40, 30),
        Scale = 1.25,
    };

    public static CaptureRequest Region(BgraImage? image = null, string? text = null, params VisibleWindow[] windows) => new()
    {
        Image = image ?? TestImages.Pattern(40, 30),
        Text = text,
        Source = Source(),
        VisibleWindows = windows,
    };
}

/// <summary>An outbox, inbox and pipeline in a temporary folder, sharing one audit log.</summary>
internal sealed class PipelineHarness : IDisposable
{
    private readonly TempDirectory temp = new();

    public PipelineHarness(PrivacySettings? settings = null, OutboxOptions? options = null)
    {
        Time = new ManualTime(Samples.Start);
        Audit = new AuditLog(temp.Combine("inbox", ".audit.jsonl"), Time);
        Outbox = new Outbox(temp.Combine("outbox"), options, Audit, Time);
        Inbox = new InboxDestination(temp.Combine("inbox"));
        Pipeline = new PrivacyPipeline(settings ?? PrivacySettings.Default, Outbox, Inbox.Name, time: Time);
    }

    public ManualTime Time { get; }
    public AuditLog Audit { get; }
    public Outbox Outbox { get; }
    public InboxDestination Inbox { get; }
    public PrivacyPipeline Pipeline { get; }
    public string Root => temp.Path;

    public CaptureResult Process(CaptureRequest request)
    {
        Time.Advance(TimeSpan.FromMilliseconds(5));
        return Pipeline.Process(request);
    }

    public string Stored(string id) => Outbox.StoredPacketDirectory(id);

    public string AuditText() => File.Exists(Audit.Path) ? File.ReadAllText(Audit.Path) : "";

    /// <summary>Every file under the test folder, as text, for "appears nowhere" checks.</summary>
    public string EverythingWritten() =>
        string.Join("\n", Directory.EnumerateFiles(temp.Path, "*", SearchOption.AllDirectories)
            .Select(f => Encoding.UTF8.GetString(File.ReadAllBytes(f))));

    public void Dispose() => temp.Dispose();
}
