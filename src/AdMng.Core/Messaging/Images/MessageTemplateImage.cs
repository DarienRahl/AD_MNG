namespace AdMng.Core.Messaging.Images;

/// <summary>
/// An image of a message template as the settings store it (design note section 7.4): metadata only – the bytes live in
/// the image store next to the settings (or, for an unsaved working template, in memory) and are found by
/// <see cref="Sha256"/>. Both language texts reference a subset by <c>cid:</c>.
/// </summary>
public sealed record MessageTemplateImage
{
    public required string ContentId { get; init; }

    public required string MediaType { get; init; }

    /// <summary>Lower-case hex SHA-256 of the (stripped) bytes.</summary>
    public required string Sha256 { get; init; }

    public long Length { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public string FileName { get; init; } = string.Empty;

    /// <summary>The metadata of an image.</summary>
    public static MessageTemplateImage From(MessageImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return new MessageTemplateImage
        {
            ContentId = image.ContentId,
            MediaType = image.MediaType,
            Sha256 = image.Sha256,
            Length = image.Data.Length,
            Width = image.Width,
            Height = image.Height,
            FileName = image.FileName,
        };
    }

    /// <summary>True when the metadata is consistent (a content id derived from the hash, a supported type, sane sizes) –
    /// settings and imported files are checked with it.</summary>
    public bool IsValid => IsValidUpTo(MessageImageLimits.MaxImageBytes);

    /// <summary><see cref="IsValid"/> with another largest image: <c>CorrespondenceLimits.MaxImageBytes</c> for the
    /// templates of Korespondencja (U5).</summary>
    public bool IsValidUpTo(long maxImageBytes) =>
        MessageImage.IsContentId(ContentId) && Sha256.Length == 64 && Sha256.All(char.IsAsciiHexDigitLower)
        && string.Equals(ContentId, MessageImage.ContentIdFor(Sha256), StringComparison.Ordinal)
        && ImageFormats.FromMediaType(MediaType) is not null && Length > 0 && Length <= maxImageBytes;

    public override string ToString() => $"MessageTemplateImage({ContentId})";
}
