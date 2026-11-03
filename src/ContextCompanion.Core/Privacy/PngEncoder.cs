using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace ContextCompanion.Core.Privacy;

/// <summary>Turns pixels into an image file for a packet.</summary>
public interface IImageEncoder
{
    /// <summary>Media type, for example <c>image/png</c>.</summary>
    string MediaType { get; }
    byte[] Encode(BgraImage image);
}

/// <summary>
/// A small, dependency-free PNG encoder: 8-bit RGBA, no interlacing, filter type 0 on every row,
/// zlib-compressed. Platform-neutral, so the privacy pipeline runs and is tested everywhere.
/// </summary>
public sealed class PngEncoder : IImageEncoder
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = BuildCrcTable();

    public string MediaType => "image/png";

    public byte[] Encode(BgraImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        using var output = new MemoryStream();
        output.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], image.Height);
        header[8] = 8;  // bit depth
        header[9] = 6;  // colour type: RGBA
        header[10] = 0; // compression
        header[11] = 0; // filter method
        header[12] = 0; // no interlace
        WriteChunk(output, "IHDR", header);

        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                byte[] row = new byte[1 + image.Width * 4];
                for (int y = 0; y < image.Height; y++)
                {
                    row[0] = 0; // filter: none
                    int source = y * image.Stride;
                    for (int x = 0; x < image.Width; x++)
                    {
                        int s = source + x * 4, d = 1 + x * 4;
                        row[d] = image.Pixels[s + 2];     // R
                        row[d + 1] = image.Pixels[s + 1]; // G
                        row[d + 2] = image.Pixels[s];     // B
                        row[d + 3] = image.Pixels[s + 3]; // A
                    }
                    zlib.Write(row);
                }
            }
            WriteChunk(output, "IDAT", compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        }
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        output.Write(buffer);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        uint crc = Crc(Crc(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(buffer, crc);
        output.Write(buffer);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
