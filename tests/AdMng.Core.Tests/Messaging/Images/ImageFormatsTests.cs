using System.Buffers.Binary;
using AdMng.Core.Messaging.Images;
using AdMng.Testing;

namespace AdMng.Core.Tests.Messaging.Images;

/// <summary>Type by magic bytes, dimensions from the headers, structure walked to the end, decompression bombs.</summary>
public class ImageFormatsTests
{
    [Fact]
    public void MagicBytes_DecideTheType()
    {
        Assert.Equal(ImageKind.Png, ImageFormats.Detect(MessageImageSamples.Png()));
        Assert.Equal(ImageKind.Jpeg, ImageFormats.Detect(MessageImageSamples.Jpeg()));
        Assert.Equal(ImageKind.Gif, ImageFormats.Detect(MessageImageSamples.Gif1x1));
        Assert.Null(ImageFormats.Detect(MessageImageSamples.Svg));
        Assert.Null(ImageFormats.Detect("BM6\0\0\0"u8));
        Assert.Null(ImageFormats.Detect("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.Null(ImageFormats.Detect([]));
    }

    [Fact]
    public void Dimensions_AreReadFromTheHeaders()
    {
        var png = ImageFormats.Inspect(MessageImageSamples.Png(7, 3));
        Assert.Equal((ImageKind.Png, 7, 3), (png.Kind, png.Width, png.Height));
        var jpeg = ImageFormats.Inspect(MessageImageSamples.Jpeg(640, 480));
        Assert.Equal((ImageKind.Jpeg, 640, 480), (jpeg.Kind, jpeg.Width, jpeg.Height));
        var gif = ImageFormats.Inspect(MessageImageSamples.Gif(12, 5));
        Assert.Equal((ImageKind.Gif, 12, 5), (gif.Kind, gif.Width, gif.Height));
        var tiny = ImageFormats.Inspect(MessageImageSamples.Gif1x1);
        Assert.Equal((1, 1), (tiny.Width, tiny.Height));
    }

    [Theory]
    [InlineData(4097, 10)]
    [InlineData(10, 4097)]
    [InlineData(4096, 4097)]
    public void SidesAboveTheLimit_AreRefused(int width, int height)
    {
        var error = Assert.Throws<MessageImageException>(() => ImageFormats.Inspect(MessageImageSamples.Jpeg(width, height)));
        Assert.Equal(MessageImageError.DimensionsTooLarge, error.Error);
    }

    [Fact]
    public void DecompressionBomb_InAPngHeader_IsRefusedWithoutDecoding()
    {
        // 50 000 × 50 000 pixels in a few hundred bytes.
        var bomb = MessageImageSamples.Png(2, 2);
        BinaryPrimitives.WriteUInt32BigEndian(bomb.AsSpan(16), 50_000);
        BinaryPrimitives.WriteUInt32BigEndian(bomb.AsSpan(20), 50_000);
        FixIhdrCrc(bomb);
        Assert.Equal(MessageImageError.DimensionsTooLarge, Assert.Throws<MessageImageException>(() => ImageFormats.Inspect(bomb)).Error);
    }

    [Fact]
    public void ZeroSize_IsRefused() =>
        Assert.Equal(MessageImageError.DimensionsTooLarge, Assert.Throws<MessageImageException>(() => ImageFormats.Inspect(MessageImageSamples.Gif(0, 4))).Error);

    [Theory]
    [InlineData("png")]
    [InlineData("jpeg")]
    [InlineData("gif")]
    public void TruncatedImages_AreMalformed(string kind)
    {
        var data = kind switch
        {
            "png" => MessageImageSamples.Png(),
            "jpeg" => MessageImageSamples.Jpeg(),
            _ => MessageImageSamples.Gif(),
        };
        foreach (var cut in new[] { data.Length - 1, data.Length / 2, 12 })
        {
            var error = Assert.Throws<MessageImageException>(() => ImageFormats.Inspect(data.AsSpan(0, cut)));
            Assert.Equal(MessageImageError.Malformed, error.Error);
        }
    }

    [Fact]
    public void PngWithAWrongChunkCrc_IsMalformed()
    {
        var png = MessageImageSamples.Png();
        png[^5] ^= 0xFF;   // the CRC of IEND
        Assert.Equal(MessageImageError.Malformed, Assert.Throws<MessageImageException>(() => ImageFormats.Inspect(png)).Error);
    }

    [Fact]
    public void PngWithoutImageData_IsMalformed()
    {
        // Signature + IHDR + IEND only.
        var png = MessageImageSamples.Png();
        var withoutData = png[..33].Concat(png[^12..]).ToArray();
        Assert.Equal(MessageImageError.Malformed, Assert.Throws<MessageImageException>(() => ImageFormats.Inspect(withoutData)).Error);
    }

    [Fact]
    public void JpegWithoutAFrame_IsMalformed()
    {
        byte[] noFrame = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xD9];
        Assert.Equal(MessageImageError.Malformed, Assert.Throws<MessageImageException>(() => ImageFormats.Inspect(noFrame)).Error);
    }

    [Fact]
    public void BytesAfterTheEndMarker_AreNotPartOfTheImage()
    {
        var png = MessageImageSamples.Png();
        var withTrailer = png.Concat("<script>alert(1)</script>"u8.ToArray()).ToArray();
        Assert.Equal(png, ImageMetadataStripper.Strip(withTrailer));
        var gif = MessageImageSamples.Gif1x1;
        Assert.Equal(gif, ImageMetadataStripper.Strip(gif.Concat(new byte[] { 1, 2, 3 }).ToArray()));
        var jpeg = MessageImageSamples.Jpeg();
        Assert.Equal(jpeg, ImageMetadataStripper.Strip(jpeg.Concat(new byte[] { 0, 0 }).ToArray()));
    }

    [Theory]
    [InlineData("image/png", ImageKind.Png)]
    [InlineData("IMAGE/JPEG; charset=binary", ImageKind.Jpeg)]
    [InlineData("image/jpg", ImageKind.Jpeg)]
    [InlineData("image/gif", ImageKind.Gif)]
    [InlineData("image/svg+xml", null)]
    [InlineData("image/webp", null)]
    [InlineData(null, null)]
    public void MediaTypes_AreMapped(string? mediaType, ImageKind? expected) => Assert.Equal(expected, ImageFormats.FromMediaType(mediaType));

    [Theory]
    [InlineData("clip_image001.png", ImageKind.Png)]
    [InlineData("zdjęcie.JPEG", ImageKind.Jpeg)]
    [InlineData("a.jpg", ImageKind.Jpeg)]
    [InlineData("logo.gif", ImageKind.Gif)]
    [InlineData("logo.svg", null)]
    [InlineData("logo", null)]
    public void Extensions_AreMapped(string name, ImageKind? expected) => Assert.Equal(expected, ImageFormats.FromExtension(name));

    private static void FixIhdrCrc(byte[] png)
    {
        var crc = ImageFormats.Crc32.Compute(png.AsSpan(12, 17));
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(29), crc);
    }
}
