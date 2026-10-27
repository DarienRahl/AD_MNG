using System.Buffers.Binary;

namespace AdMng.Core.Messaging.Images;

/// <summary>The raster formats a message may carry. SVG, WebP, TIFF, BMP and EMF are deliberately not supported.</summary>
public enum ImageKind
{
    Png,
    Jpeg,
    Gif,
}

/// <summary>Why an image was refused (<see cref="MessageImageException"/>).</summary>
public enum MessageImageError
{
    /// <summary>No bytes.</summary>
    Empty,

    /// <summary>Not PNG, JPEG or GIF by its magic bytes (SVG, WebP, BMP … included).</summary>
    UnknownFormat,

    /// <summary>The file name extension or the declared media type does not match the content.</summary>
    TypeMismatch,

    /// <summary>The structure is truncated or malformed (chunk CRC, missing segments, no image data).</summary>
    Malformed,

    /// <summary>Larger than <see cref="MessageImageLimits.MaxImageBytes"/>.</summary>
    TooLarge,

    /// <summary>A side is 0 or above <see cref="MessageImageLimits.MaxSide"/>, or more than <see cref="MessageImageLimits.MaxPixels"/> pixels.</summary>
    DimensionsTooLarge,

    /// <summary>The content id does not match the bytes (AdMng Watchdog check).</summary>
    IdMismatch,
}

/// <summary>An image was refused; <see cref="Error"/> says why. The message never contains image bytes or a path.</summary>
public sealed class MessageImageException(MessageImageError error)
    : FormatException($"The image is not accepted ({error}).")
{
    public MessageImageError Error { get; } = error;
}

/// <summary>Kind, dimensions and the byte ranges of an image that are kept when the metadata is stripped.</summary>
/// <param name="Kind">Format by magic bytes.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Kept">Byte ranges (offset, length) of the stripped image, in order.</param>
/// <param name="Removed">Metadata blocks removed (chunk, segment or extension names), for diagnostics and tests.</param>
public sealed record ImageStructure(ImageKind Kind, int Width, int Height, IReadOnlyList<(int Offset, int Length)> Kept, IReadOnlyList<string> Removed);

/// <summary>
/// The raster formats of message images (design note section 7.2): type by magic bytes only, dimensions from the headers
/// without decoding (PNG <c>IHDR</c>, GIF logical screen, JPEG first <c>SOFn</c>), the structure walked to its end (PNG
/// chunks with CRC, JPEG segments to <c>EOI</c>, GIF blocks to the trailer). Bytes after the end marker are not part of
/// the image. Pure and allocation-light; never decodes pixels.
/// </summary>
public static class ImageFormats
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The format of <paramref name="data"/> by its magic bytes, or null.</summary>
    public static ImageKind? Detect(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith(PngSignature))
        {
            return ImageKind.Png;
        }

        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return ImageKind.Jpeg;
        }

        return data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8) ? ImageKind.Gif : null;
    }

    /// <summary>"image/png" / "image/jpeg" / "image/gif".</summary>
    public static string MediaType(ImageKind kind) => kind switch
    {
        ImageKind.Png => "image/png",
        ImageKind.Jpeg => "image/jpeg",
        _ => "image/gif",
    };

    /// <summary>"png" / "jpg" / "gif" (the extension of generated file names and of the image store).</summary>
    public static string Extension(ImageKind kind) => kind switch
    {
        ImageKind.Png => "png",
        ImageKind.Jpeg => "jpg",
        _ => "gif",
    };

    /// <summary>The kind of a media type ("image/png", "image/jpeg", "image/jpg", "image/gif"; parameters ignored), or null.</summary>
    public static ImageKind? FromMediaType(string? mediaType)
    {
        var value = mediaType?.Split(';')[0].Trim().ToLowerInvariant();
        return value switch
        {
            "image/png" => ImageKind.Png,
            "image/jpeg" or "image/jpg" or "image/pjpeg" => ImageKind.Jpeg,
            "image/gif" => ImageKind.Gif,
            _ => null,
        };
    }

    /// <summary>The kind of a file name's extension (".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".gif"), or null.</summary>
    public static ImageKind? FromExtension(string? fileName)
    {
        var dot = fileName?.LastIndexOf('.') ?? -1;
        if (dot < 0)
        {
            return null;
        }

        return fileName![(dot + 1)..].ToLowerInvariant() switch
        {
            "png" => ImageKind.Png,
            "jpg" or "jpeg" or "jpe" or "jfif" => ImageKind.Jpeg,
            "gif" => ImageKind.Gif,
            _ => null,
        };
    }

    /// <summary>Walks the structure of an image: format, dimensions (checked against the limits) and the byte ranges that
    /// remain when the metadata is stripped.</summary>
    /// <exception cref="MessageImageException">Empty, unknown format, malformed or truncated, dimensions out of range.</exception>
    public static ImageStructure Inspect(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            throw new MessageImageException(MessageImageError.Empty);
        }

        var structure = Detect(data) switch
        {
            ImageKind.Png => WalkPng(data),
            ImageKind.Jpeg => WalkJpeg(data),
            ImageKind.Gif => WalkGif(data),
            _ => throw new MessageImageException(MessageImageError.UnknownFormat),
        };
        if (structure.Width is < 1 or > MessageImageLimits.MaxSide || structure.Height is < 1 or > MessageImageLimits.MaxSide
            || (long)structure.Width * structure.Height > MessageImageLimits.MaxPixels)
        {
            throw new MessageImageException(MessageImageError.DimensionsTooLarge);
        }

        return structure;
    }

    // ---- PNG ----------------------------------------------------------------------------------------------------------

    /// <summary>Ancillary chunks with text, Exif or a time stamp (author, GPS position, software …).</summary>
    private static readonly HashSet<string> PngMetadataChunks = new(StringComparer.Ordinal) { "tEXt", "zTXt", "iTXt", "eXIf", "tIME" };

    private static ImageStructure WalkPng(ReadOnlySpan<byte> data)
    {
        var kept = new List<(int, int)> { (0, PngSignature.Length) };
        var removed = new List<string>();
        var position = PngSignature.Length;
        int width = 0, height = 0;
        var first = true;
        var hasData = false;
        while (true)
        {
            if (position + 12 > data.Length)
            {
                throw Malformed();
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
            if (length > int.MaxValue || position + 12 + (long)length > data.Length)
            {
                throw Malformed();
            }

            var typeBytes = data.Slice(position + 4, 4);
            foreach (var b in typeBytes)
            {
                if (!char.IsAsciiLetter((char)b))
                {
                    throw Malformed();
                }
            }

            var type = System.Text.Encoding.ASCII.GetString(typeBytes);
            var body = data.Slice(position + 8, (int)length);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(data[(position + 8 + (int)length)..]);
            if (Crc32.Compute(data.Slice(position + 4, 4 + (int)length)) != crc)
            {
                throw Malformed();
            }

            if (first)
            {
                if (type != "IHDR" || length != 13)
                {
                    throw Malformed();
                }

                width = ToInt(BinaryPrimitives.ReadUInt32BigEndian(body));
                height = ToInt(BinaryPrimitives.ReadUInt32BigEndian(body[4..]));
                first = false;
            }
            else if (type == "IHDR")
            {
                throw Malformed();
            }

            var chunkLength = 12 + (int)length;
            if (PngMetadataChunks.Contains(type))
            {
                removed.Add(type);
            }
            else
            {
                kept.Add((position, chunkLength));
            }

            hasData |= type == "IDAT";
            position += chunkLength;
            if (type == "IEND")
            {
                break;
            }
        }

        return hasData ? new ImageStructure(ImageKind.Png, width, height, Merge(kept), removed) : throw Malformed();
    }

    // ---- JPEG ---------------------------------------------------------------------------------------------------------

    private static ImageStructure WalkJpeg(ReadOnlySpan<byte> data)
    {
        var kept = new List<(int, int)> { (0, 2) };
        var removed = new List<string>();
        var position = 2;
        int width = 0, height = 0;
        var hasFrame = false;
        var hasScan = false;
        while (true)
        {
            if (position >= data.Length || data[position] != 0xFF)
            {
                throw Malformed();
            }

            // Fill bytes (0xFF repeated) before a marker.
            var start = position;
            while (position < data.Length && data[position] == 0xFF)
            {
                position++;
            }

            if (position >= data.Length)
            {
                throw Malformed();
            }

            var marker = data[position++];
            if (marker == 0xD9)
            {
                kept.Add((start, position - start));
                break;
            }

            if (marker is 0x00 or 0xD8)
            {
                throw Malformed();
            }

            if (marker is >= 0xD0 and <= 0xD7 or 0x01)
            {
                kept.Add((start, position - start));
                continue;
            }

            if (position + 2 > data.Length)
            {
                throw Malformed();
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
            if (length < 2 || position + length > data.Length)
            {
                throw Malformed();
            }

            var payload = data.Slice(position + 2, length - 2);
            var end = position + length;
            if (IsStartOfFrame(marker) && !hasFrame)
            {
                if (payload.Length < 6)
                {
                    throw Malformed();
                }

                height = BinaryPrimitives.ReadUInt16BigEndian(payload[1..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(payload[3..]);
                hasFrame = true;
            }

            if (marker == 0xDA)
            {
                if (!hasFrame)
                {
                    throw Malformed();
                }

                hasScan = true;
                end = EndOfEntropyData(data, end);
            }

            // APP1 (Exif, XMP), APP13 (IPTC / Photoshop) and comments carry author, position and software; APP0 (JFIF),
            // APP2 (ICC profile) and APP14 (Adobe colour transform) are needed to show the colours right.
            if (marker is 0xE1 or 0xED or 0xFE)
            {
                removed.Add(marker switch { 0xE1 => "APP1", 0xED => "APP13", _ => "COM" });
            }
            else
            {
                kept.Add((start, end - start));
            }

            position = end;
        }

        return hasFrame && hasScan ? new ImageStructure(ImageKind.Jpeg, width, height, Merge(kept), removed) : throw Malformed();
    }

    /// <summary>SOF0–SOF15 except DHT (C4), JPG (C8) and DAC (CC).</summary>
    private static bool IsStartOfFrame(byte marker) => marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC);

    /// <summary>The offset of the marker after the entropy-coded data that starts at <paramref name="position"/>.</summary>
    private static int EndOfEntropyData(ReadOnlySpan<byte> data, int position)
    {
        for (var i = position; i < data.Length - 1; i++)
        {
            if (data[i] != 0xFF)
            {
                continue;
            }

            var next = data[i + 1];
            if (next == 0x00 || next is >= 0xD0 and <= 0xD7 || next == 0xFF)
            {
                if (next != 0xFF)
                {
                    i++;
                }

                continue;
            }

            return i;
        }

        throw Malformed();
    }

    // ---- GIF ----------------------------------------------------------------------------------------------------------

    private static ImageStructure WalkGif(ReadOnlySpan<byte> data)
    {
        if (data.Length < 13)
        {
            throw Malformed();
        }

        var width = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        var packed = data[10];
        var position = 13;
        if ((packed & 0x80) != 0)
        {
            position += 3 * (1 << ((packed & 0x07) + 1));
        }

        var kept = new List<(int, int)> { (0, Math.Min(position, data.Length)) };
        var removed = new List<string>();
        var frames = 0;
        while (true)
        {
            if (position >= data.Length)
            {
                throw Malformed();
            }

            var start = position;
            var block = data[position++];
            if (block == 0x3B)
            {
                kept.Add((start, 1));
                break;
            }

            if (block == 0x21)
            {
                if (position >= data.Length)
                {
                    throw Malformed();
                }

                var label = data[position++];
                var firstBlock = position < data.Length ? data.Slice(position + 1, Math.Min(data[position], data.Length - position - 1)) : [];
                position = SkipSubBlocks(data, position);
                var isXmp = label == 0xFF && firstBlock.Length >= 8 && firstBlock[..8].SequenceEqual("XMP Data"u8);
                if (label == 0xFE || isXmp)
                {
                    removed.Add(label == 0xFE ? "Comment" : "XMP");
                }
                else
                {
                    kept.Add((start, position - start));
                }

                continue;
            }

            if (block != 0x2C || position + 9 > data.Length)
            {
                throw Malformed();
            }

            var imagePacked = data[position + 8];
            position += 9;
            if ((imagePacked & 0x80) != 0)
            {
                position += 3 * (1 << ((imagePacked & 0x07) + 1));
            }

            // LZW minimum code size, then the image data sub-blocks.
            position++;
            if (position > data.Length)
            {
                throw Malformed();
            }

            position = SkipSubBlocks(data, position);
            kept.Add((start, position - start));
            frames++;
        }

        return frames > 0 ? new ImageStructure(ImageKind.Gif, width, height, Merge(kept), removed) : throw Malformed();
    }

    /// <summary>The offset after the sub-blocks (size byte + data) ending with a 0 size byte.</summary>
    private static int SkipSubBlocks(ReadOnlySpan<byte> data, int position)
    {
        while (true)
        {
            if (position >= data.Length)
            {
                throw Malformed();
            }

            var size = data[position++];
            if (size == 0)
            {
                return position;
            }

            position += size;
            if (position > data.Length)
            {
                throw Malformed();
            }
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private static int ToInt(uint value) => value > int.MaxValue ? int.MaxValue : (int)value;

    private static MessageImageException Malformed() => new(MessageImageError.Malformed);

    /// <summary>Adjacent ranges joined.</summary>
    private static List<(int Offset, int Length)> Merge(List<(int Offset, int Length)> ranges)
    {
        var merged = new List<(int Offset, int Length)>(ranges.Count);
        foreach (var range in ranges)
        {
            if (merged.Count > 0 && merged[^1].Offset + merged[^1].Length == range.Offset)
            {
                merged[^1] = (merged[^1].Offset, merged[^1].Length + range.Length);
            }
            else
            {
                merged.Add(range);
            }
        }

        return merged;
    }

    /// <summary>CRC-32 (ISO 3309, as used by PNG).</summary>
    internal static class Crc32
    {
        private static readonly uint[] Table = CreateTable();

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in data)
            {
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            return crc ^ 0xFFFFFFFFu;
        }

        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }
    }
}
