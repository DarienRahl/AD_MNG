using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace AdMng.Testing;

/// <summary>
/// Images for the U1c tests (inline images of messages to users): real, decodable PNGs and GIFs (pixels in a known
/// pattern) and structurally valid JPEGs with Exif/XMP/IPTC/comment metadata that the stripper must remove. Built in
/// memory, never read from disk.
/// </summary>
public static class MessageImageSamples
{
    /// <summary>The 43-byte 1×1 transparent GIF.</summary>
    public static byte[] Gif1x1 => Convert.FromBase64String("R0lGODlhAQABAIAAAP///wAAACH5BAEAAAAALAAAAAABAAEAAAICRAEAOw==");

    /// <summary>A decodable RGB PNG of <paramref name="width"/>×<paramref name="height"/> (colour from <paramref name="seed"/>),
    /// optionally with metadata chunks (tEXt author, eXIf, tIME) before the image data.</summary>
    public static byte[] Png(int width = 2, int height = 2, byte seed = 0x40, bool withMetadata = false)
    {
        using var stream = new MemoryStream();
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = 8;      // bit depth
        header[9] = 2;      // RGB
        Chunk(stream, "IHDR", header);
        if (withMetadata)
        {
            Chunk(stream, "tEXt", Encoding.Latin1.GetBytes("Author\0Jan Kowalski"));
            Chunk(stream, "eXIf", [0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00]);
            Chunk(stream, "tIME", [0x07, 0xEA, 0x0A, 0x1B, 0x0C, 0x00, 0x00]);
        }

        var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0);   // filter: none
            for (var x = 0; x < width; x++)
            {
                raw.Write([(byte)(seed + x), (byte)(seed + y), (byte)(seed ^ 0xFF)]);
            }
        }

        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw.ToArray());
            }

            Chunk(stream, "IDAT", compressed.ToArray());
        }

        Chunk(stream, "IEND", []);
        return stream.ToArray();
    }

    /// <summary>A structurally valid baseline JPEG (APP0 JFIF, optional APP1 Exif with a GPS tag, APP1 XMP, APP13 IPTC and a
    /// comment, APP2 ICC, DQT, SOF0, DHT, SOS with byte stuffing and a restart marker, EOI).</summary>
    public static byte[] Jpeg(int width = 16, int height = 8, bool withMetadata = false, byte seed = 0x11)
    {
        using var stream = new MemoryStream();
        stream.Write([0xFF, 0xD8]);
        Segment(stream, 0xE0, [.. "JFIF\0"u8, 0x01, 0x01, 0x00, 0x00, 0x48, 0x00, 0x48, 0x00, 0x00]);
        if (withMetadata)
        {
            Segment(stream, 0xE1, [.. "Exif\0\0MM\0*"u8, 0x00, 0x00, 0x00, 0x08, .. "GPS 52.2297N 21.0122E Jan Kowalski"u8]);
            Segment(stream, 0xE1, [.. "http://ns.adobe.com/xap/1.0/\0<x:xmpmeta>autor: Łukasz Żółć</x:xmpmeta>"u8]);
            Segment(stream, 0xED, [.. "Photoshop 3.0\08BIM"u8, 0x04, 0x04, 0x00, 0x00]);
            Segment(stream, 0xFE, [.. "zrobione telefonem"u8]);
        }

        Segment(stream, 0xE2, [.. "ICC_PROFILE\0"u8, 0x01, 0x01, 0x00, 0x00]);
        var quantization = new byte[65];
        for (var i = 1; i < quantization.Length; i++)
        {
            quantization[i] = (byte)(i % 16 + 1);
        }

        Segment(stream, 0xDB, quantization);
        var frame = new byte[9];
        frame[0] = 8;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(1), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(3), (ushort)width);
        frame[5] = 1;           // one component
        frame[6] = 1;
        frame[7] = 0x11;
        frame[8] = 0;
        Segment(stream, 0xC0, frame);
        Segment(stream, 0xC4, [0x00, 0x00, 0x01, .. new byte[15], 0x00]);
        Segment(stream, 0xDA, [0x01, 0x01, 0x00, 0x00, 0x3F, 0x00]);
        stream.Write([seed, 0x22, 0xFF, 0x00, 0x33, 0xFF, 0xD0, 0x44, (byte)(seed ^ 0x5A)]);
        stream.Write([0xFF, 0xD9]);
        return stream.ToArray();
    }

    /// <summary>A GIF89a of <paramref name="width"/>×<paramref name="height"/> with a comment and an XMP extension
    /// (removed by the stripper) and a NETSCAPE loop extension (kept).</summary>
    public static byte[] Gif(int width = 3, int height = 2, bool withMetadata = false)
    {
        using var stream = new MemoryStream();
        stream.Write("GIF89a"u8);
        var screen = new byte[7];
        BinaryPrimitives.WriteUInt16LittleEndian(screen, (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(screen.AsSpan(2), (ushort)height);
        screen[4] = 0x80;       // global colour table, 2 entries
        stream.Write(screen);
        stream.Write([0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF]);
        if (withMetadata)
        {
            stream.Write([0x21, 0xFE, 0x0C, .. "Jan Kowalski"u8, 0x00]);
            stream.Write([0x21, 0xFF, 0x0B, .. "XMP DataXMP"u8, 0x05, .. "<xmp>"u8, 0x00]);
        }

        stream.Write([0x21, 0xFF, 0x0B, .. "NETSCAPE2.0"u8, 0x03, 0x01, 0x00, 0x00, 0x00]);
        var descriptor = new byte[10];
        descriptor[0] = 0x2C;
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(5), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(7), (ushort)height);
        stream.Write(descriptor);
        stream.Write([0x02, 0x02, 0x44, 0x01, 0x00]);
        stream.WriteByte(0x3B);
        return stream.ToArray();
    }

    /// <summary>An SVG (must be refused: scripts, remote references).</summary>
    public static byte[] Svg => "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray();

    /// <summary>The bytes as a <c>data:</c> URI.</summary>
    public static string DataUri(byte[] data, string mediaType) => $"data:{mediaType};base64,{Convert.ToBase64String(data)}";

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);
        var typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        stream.Write(typed);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
        stream.Write(crc);
    }

    private static void Segment(Stream stream, byte marker, byte[] payload)
    {
        stream.Write([0xFF, marker]);
        var length = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)(payload.Length + 2));
        stream.Write(length);
        stream.Write(payload);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
