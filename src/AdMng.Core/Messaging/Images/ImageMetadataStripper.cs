namespace AdMng.Core.Messaging.Images;

/// <summary>
/// Removes metadata from an image by lossless surgery – never by re-encoding, so the pixels stay byte-identical (design
/// note section 7.2): PNG <c>tEXt zTXt iTXt eXIf tIME</c> chunks; JPEG <c>APP1</c> (Exif / XMP), <c>APP13</c> (IPTC) and
/// <c>COM</c> segments (<c>APP0</c> JFIF, <c>APP2</c> ICC and <c>APP14</c> Adobe stay); GIF comment and XMP extensions.
/// Bytes after the image's end marker are dropped too. A photo pasted from a phone loses its GPS position and author.
/// </summary>
public static class ImageMetadataStripper
{
    /// <summary>The image without its metadata (a new array; the input is unchanged).</summary>
    /// <exception cref="MessageImageException">Not a valid PNG, JPEG or GIF (see <see cref="ImageFormats.Inspect"/>).</exception>
    public static byte[] Strip(ReadOnlySpan<byte> data) => Strip(data, ImageFormats.Inspect(data));

    /// <summary>The kept ranges of an inspected image, concatenated.</summary>
    public static byte[] Strip(ReadOnlySpan<byte> data, ImageStructure structure)
    {
        ArgumentNullException.ThrowIfNull(structure);
        var result = new byte[structure.Kept.Sum(r => r.Length)];
        var target = 0;
        foreach (var (offset, length) in structure.Kept)
        {
            data.Slice(offset, length).CopyTo(result.AsSpan(target));
            target += length;
        }

        return result;
    }
}
