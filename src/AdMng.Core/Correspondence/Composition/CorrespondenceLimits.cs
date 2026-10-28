namespace AdMng.Core.Correspondence.Composition;

/// <summary>Limits of Korespondencja messages (design note 3.3; the per-message ones are checked by the renderer).</summary>
public static class CorrespondenceLimits
{
    /// <summary>Inline images of one message.</summary>
    public const int MaxImagesPerMessage = 50;

    /// <summary>Bytes of one image after metadata stripping (10 MiB).</summary>
    public const int MaxImageBytes = 10 * 1024 * 1024;

    /// <summary>Bytes of all inline images of one message (25 MiB).</summary>
    public const long MaxImagesBytesPerMessage = 25L * 1024 * 1024;

    /// <summary>Attachments above this size are warned about (MailerWPiA's warning, 15 MiB).</summary>
    public const long AttachmentWarningBytes = 15L * 1024 * 1024;

    /// <summary>Images and attachments of one message above this size are warned about (20 MiB).</summary>
    public const long MessageWarningBytes = 20L * 1024 * 1024;

    /// <summary>Recipients of one BCC message (the Exchange Online limit).</summary>
    public const int MaxBccPerMessage = 500;

    public const int MaxSubjectLength = 255;

    /// <summary>Rows of the recipient list.</summary>
    public const int MaxRecipients = 100_000;
}
