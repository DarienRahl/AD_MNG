using System.Security.Cryptography;
using AdMng.Core.Messaging.Images;
using AdMng.Testing;

namespace AdMng.Core.Tests.Messaging.Images;

/// <summary>The only factory of inline images: validation, stripping, content id, safe file names, ToString.</summary>
public class MessageImageTests
{
    [Fact]
    public void ContentId_IsDerivedFromTheStrippedBytes()
    {
        var image = MessageImage.Create(MessageImageSamples.Png(3, 3, withMetadata: true), "clip_image001.png");
        var expectedSha = Convert.ToHexStringLower(SHA256.HashData(MessageImageSamples.Png(3, 3)));
        Assert.Equal(expectedSha, image.Sha256);
        Assert.Equal("img-" + expectedSha[..16] + "@admng", image.ContentId);
        Assert.True(MessageImage.IsContentId(image.ContentId));
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal((3, 3), (image.Width, image.Height));
        Assert.Equal("clip_image001.png", image.FileName);

        // The same picture with other metadata is the same image.
        var again = MessageImage.Create(MessageImageSamples.Png(3, 3), "inny.png");
        Assert.Equal(image.ContentId, again.ContentId);
    }

    [Fact]
    public void ToString_ShowsNoBytesAndNoName()
    {
        var image = MessageImage.Create(MessageImageSamples.Jpeg(), @"C:\Users\JKOWAL~1\AppData\Local\Temp\msohtmlclip1\01\clip_image002.jpg");
        Assert.Equal("clip_image002.jpg", image.FileName);
        var text = image.ToString();
        Assert.Equal($"MessageImage({image.ContentId}, image/jpeg, {image.Data.Length} B)", text);
        Assert.DoesNotContain("JKOWAL", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("logo.png", "image/jpeg")]
    [InlineData("logo.jpg", null)]
    [InlineData("logo.svg", null)]
    public void ExtensionOrMediaTypeMismatch_IsRefused(string fileName, string? mediaType)
    {
        var error = Assert.Throws<MessageImageException>(() => MessageImage.Create(MessageImageSamples.Png(), fileName, mediaType));
        Assert.Equal(MessageImageError.TypeMismatch, error.Error);
    }

    [Fact]
    public void Svg_IsAnUnknownFormat()
    {
        Assert.Equal(MessageImageError.UnknownFormat, Assert.Throws<MessageImageException>(() => MessageImage.Create(MessageImageSamples.Svg, "a.svg", "image/svg+xml")).Error);
        Assert.Equal(MessageImageError.Empty, Assert.Throws<MessageImageException>(() => MessageImage.Create([])).Error);
    }

    [Fact]
    public void ImagesAboveOneMebibyte_AreRefused()
    {
        var big = MessageImageSamples.Png(2, 2).ToList();
        // A large private ancillary chunk after IHDR (kept by the stripper, so the image stays above the limit).
        var payload = new byte[MessageImageLimits.MaxImageBytes];
        var chunk = new List<byte>();
        chunk.AddRange([(byte)(payload.Length >> 24), (byte)(payload.Length >> 16), (byte)(payload.Length >> 8), (byte)payload.Length]);
        var typed = "prVt"u8.ToArray().Concat(payload).ToArray();
        chunk.AddRange(typed);
        var crc = ImageFormats.Crc32.Compute(typed);
        chunk.AddRange([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
        big.InsertRange(33, chunk);
        Assert.Equal(MessageImageError.TooLarge, Assert.Throws<MessageImageException>(() => MessageImage.Create(big.ToArray())).Error);
    }

    [Theory]
    [InlineData(null, "image4.png")]
    [InlineData("", "image4.png")]
    [InlineData("zdjęcie z urlopu.png", "image4.png")]
    [InlineData("logo", "logo.png")]
    [InlineData("../../etc/passwd.png", "passwd.png")]
    [InlineData("logo-działu_IT.png", "image4.png")]
    [InlineData(".png", "image4.png")]
    public void FileNames_AreSafe(string? name, string expected) => Assert.Equal(expected, MessageImage.SafeFileName(name, ImageKind.Png, 4));

    [Fact]
    public void TryCreate_ReportsTheReason()
    {
        Assert.False(MessageImage.TryCreate(MessageImageSamples.Svg, null, null, out var none, out var error));
        Assert.Null(none);
        Assert.Equal(MessageImageError.UnknownFormat, error);
        Assert.True(MessageImage.TryCreate(MessageImageSamples.Gif1x1, "a.gif", "image/gif", out var gif, out _));
        Assert.Equal("image/gif", gif!.MediaType);
    }
}
