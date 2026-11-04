using System.Text;
using AdMng.Core.Messaging.Images;
using AdMng.Testing;

namespace AdMng.Core.Tests.Messaging.Images;

/// <summary>Metadata removed by lossless surgery: the pixel data stays byte-identical.</summary>
public class ImageMetadataStripperTests
{
    [Fact]
    public void Png_LosesTextExifAndTime_KeepsThePixels()
    {
        var clean = MessageImageSamples.Png(5, 4);
        var tagged = MessageImageSamples.Png(5, 4, withMetadata: true);
        Assert.Contains("Jan Kowalski", Encoding.Latin1.GetString(tagged), StringComparison.Ordinal);

        var structure = ImageFormats.Inspect(tagged);
        Assert.Equal(["tEXt", "eXIf", "tIME"], structure.Removed);
        var stripped = ImageMetadataStripper.Strip(tagged);
        Assert.Equal(clean, stripped);
        Assert.DoesNotContain("Kowalski", Encoding.Latin1.GetString(stripped), StringComparison.Ordinal);
    }

    [Fact]
    public void Jpeg_LosesExifXmpIptcAndComments_KeepsJfifIccAndTheScan()
    {
        var clean = MessageImageSamples.Jpeg();
        var tagged = MessageImageSamples.Jpeg(withMetadata: true);
        var text = Encoding.Latin1.GetString(tagged);
        Assert.Contains("GPS", text, StringComparison.Ordinal);
        Assert.Contains("xmpmeta", text, StringComparison.Ordinal);

        var structure = ImageFormats.Inspect(tagged);
        Assert.Equal(["APP1", "APP1", "APP13", "COM"], structure.Removed);
        var stripped = ImageMetadataStripper.Strip(tagged);
        Assert.Equal(clean, stripped);
        var strippedText = Encoding.Latin1.GetString(stripped);
        Assert.Contains("JFIF", strippedText, StringComparison.Ordinal);
        Assert.Contains("ICC_PROFILE", strippedText, StringComparison.Ordinal);
        Assert.DoesNotContain("GPS", strippedText, StringComparison.Ordinal);
        Assert.DoesNotContain("Photoshop", strippedText, StringComparison.Ordinal);
        Assert.DoesNotContain("telefonem", strippedText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public void Jpeg_KeepsOnlyItsExifOrientation_InAMinimalApp1_AndStrippingIsIdempotent(int orientation)
    {
        // A phone photo in an Outlook draft: Exif with the camera model, a GPS-like text and Orientation 6 (rotated 90°).
        var photo = WithExif(MessageImageSamples.Jpeg(), PhoneExif(orientation));
        Assert.Contains("Telefon Łukasza", Encoding.UTF8.GetString(photo), StringComparison.Ordinal);

        var stripped = ImageMetadataStripper.Strip(photo);

        Assert.DoesNotContain("Telefon", Encoding.Latin1.GetString(stripped), StringComparison.Ordinal);
        byte[] minimal =
        [
            0xFF, 0xE1, 0x00, 0x22, .. "Exif\0\0"u8, .. "MM\0*"u8, 0x00, 0x00, 0x00, 0x08,
            0x00, 0x01, 0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, (byte)orientation, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        ];
        Assert.Equal(WithExif(MessageImageSamples.Jpeg(), minimal[4..]), stripped);
        Assert.Equal(stripped, ImageMetadataStripper.Strip(stripped));
        Assert.Equal(MessageImage.Create(photo).Sha256, MessageImage.Create(stripped).Sha256);
    }

    [Fact]
    public void Jpeg_WithTheDefaultOrientation_LosesTheWholeExif() =>
        Assert.Equal(MessageImageSamples.Jpeg(), ImageMetadataStripper.Strip(WithExif(MessageImageSamples.Jpeg(), PhoneExif(1))));

    /// <summary>An Exif APP1 payload (little-endian TIFF): Model "Telefon Łukasza" and the orientation.</summary>
    private static byte[] PhoneExif(int orientation)
    {
        var model = Encoding.UTF8.GetBytes("Telefon Łukasza\0");
        using var tiff = new MemoryStream();
        tiff.Write("II*\0"u8);
        tiff.Write([0x08, 0x00, 0x00, 0x00, 0x02, 0x00]);
        var modelOffset = 8 + 2 + (2 * 12) + 4;
        tiff.Write([0x10, 0x01, 0x02, 0x00, (byte)model.Length, 0x00, 0x00, 0x00, (byte)modelOffset, 0x00, 0x00, 0x00]);
        tiff.Write([0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, (byte)orientation, 0x00, 0x00, 0x00]);
        tiff.Write([0x00, 0x00, 0x00, 0x00]);
        tiff.Write(model);
        return [.. "Exif\0\0"u8, .. tiff.ToArray()];
    }

    /// <summary>The JPEG with an APP1 segment of <paramref name="payload"/> right after its APP0 (JFIF).</summary>
    private static byte[] WithExif(byte[] jpeg, byte[] payload)
    {
        var afterApp0 = 4 + ((jpeg[4] << 8) | jpeg[5]);
        var length = payload.Length + 2;
        return [.. jpeg[..afterApp0], 0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. payload, .. jpeg[afterApp0..]];
    }

    [Fact]
    public void Gif_LosesCommentAndXmp_KeepsTheLoopExtension()
    {
        var clean = MessageImageSamples.Gif();
        var tagged = MessageImageSamples.Gif(withMetadata: true);
        var structure = ImageFormats.Inspect(tagged);
        Assert.Equal(["Comment", "XMP"], structure.Removed);
        var stripped = ImageMetadataStripper.Strip(tagged);
        Assert.Equal(clean, stripped);
        Assert.Contains("NETSCAPE2.0", Encoding.ASCII.GetString(stripped), StringComparison.Ordinal);
    }

    [Fact]
    public void CleanImages_AreUnchanged()
    {
        foreach (var image in new[] { MessageImageSamples.Png(), MessageImageSamples.Jpeg(), MessageImageSamples.Gif(), MessageImageSamples.Gif1x1 })
        {
            Assert.Equal(image, ImageMetadataStripper.Strip(image));
        }
    }

    [Fact]
    public void TheInputIsNotModified()
    {
        var tagged = MessageImageSamples.Jpeg(withMetadata: true);
        var copy = tagged.ToArray();
        _ = ImageMetadataStripper.Strip(tagged);
        Assert.Equal(copy, tagged);
    }
}
