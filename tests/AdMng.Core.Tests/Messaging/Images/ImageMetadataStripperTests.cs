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
