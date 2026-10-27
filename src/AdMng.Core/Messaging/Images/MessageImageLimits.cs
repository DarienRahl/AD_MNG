namespace AdMng.Core.Messaging.Images;

/// <summary>
/// Limits of inline images in messages to users (U1c, design note <c>docs/dev/u1c-u1d-rich-html-and-salutations.md</c>
/// section 7.3). The AdMng Watchdog service reports its own values in <c>GET /api/v1/user-messages/status</c>.
/// </summary>
public static class MessageImageLimits
{
    /// <summary>Images of one message.</summary>
    public const int MaxImagesPerMessage = 10;

    /// <summary>Bytes of one image after the metadata was stripped (1 MiB).</summary>
    public const int MaxImageBytes = 1024 * 1024;

    /// <summary>Bytes of all images of one message (2 MiB).</summary>
    public const long MaxImagesBytesPerMessage = 2L * 1024 * 1024;

    /// <summary>Longest side of an image in pixels.</summary>
    public const int MaxSide = 4096;

    /// <summary>Pixels of an image (decompression bombs in the recipients' clients).</summary>
    public const long MaxPixels = 16_777_216;

    /// <summary>Images of one template (both languages).</summary>
    public const int MaxImagesPerTemplate = 20;

    /// <summary>Bytes of all images of one template (4 MiB).</summary>
    public const long MaxImagesBytesPerTemplate = 4L * 1024 * 1024;
}
