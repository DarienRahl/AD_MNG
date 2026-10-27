using AdMng.Core.Messaging;

namespace AdMng.Presentation.UserMessages;

/// <summary>Why the planned messages do not fit the chosen transport (U1c, design note u1c-u1d section 9.6).</summary>
public enum TransportFitReason
{
    None,

    /// <summary>Messages carry images, the transport takes none (an older AdMng Watchdog, or images switched off).</summary>
    ImagesNotSupported,

    /// <summary>Images of a message exceed the transport's count or size limits.</summary>
    ImagesOverLimits,

    /// <summary>The service's outbox has no room for the images of a message now.</summary>
    ImageStorage,

    /// <summary>An HTML body is longer than the transport accepts.</summary>
    HtmlTooLong,
}

/// <summary>The outcome of <see cref="TransportFit.Check"/>: the first reason and how many messages it concerns.</summary>
public sealed record TransportFitResult(TransportFitReason Reason, int Messages)
{
    public static TransportFitResult Fits { get; } = new(TransportFitReason.None, 0);

    public bool IsFit => Reason == TransportFitReason.None;

    /// <summary>"Wyślij bez obrazów" can help (every reason about images).</summary>
    public bool OffersWithoutImages => Reason is TransportFitReason.ImagesNotSupported or TransportFitReason.ImagesOverLimits or TransportFitReason.ImageStorage;
}

/// <summary>
/// Compares the messages that will be sent with what the chosen transport accepts, before sending and before the test
/// message (design note section 9.6): images to a transport without images, images over its limits, images the service's
/// outbox has no room for, HTML longer than it takes. The window then shows a blocking bar with the choices that can help
/// (send directly through SMTP for this run, send without images, cancel) – never a silent switch or drop.
/// </summary>
public static class TransportFit
{
    public static TransportFitResult Check(IEnumerable<PlannedUserMessage> messages, UserMessageTransportCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(capabilities);
        var outgoing = messages.Select(m => m.Message).OfType<OutgoingUserMessage>().ToList();
        var withImages = outgoing.Where(m => m.InlineImages.Count > 0).ToList();
        if (withImages.Count > 0)
        {
            if (!capabilities.InlineImages)
            {
                return new TransportFitResult(TransportFitReason.ImagesNotSupported, withImages.Count);
            }

            var over = withImages.Count(m => m.InlineImages.Count > capabilities.MaxInlineImages
                                             || m.InlineImages.Any(i => i.Data.Length > capabilities.MaxInlineImageBytes)
                                             || ImageBytes(m) > capabilities.MaxInlineImagesTotalBytes);
            if (over > 0)
            {
                return new TransportFitResult(TransportFitReason.ImagesOverLimits, over);
            }

            // Each message leaves the service's outbox (with its images) once sent, so one message must fit the room left.
            if (capabilities.ImageStorageAvailableBytes is { } room && withImages.Any(m => ImageBytes(m) > room))
            {
                return new TransportFitResult(TransportFitReason.ImageStorage, withImages.Count(m => ImageBytes(m) > room));
            }
        }

        var tooLong = outgoing.Count(m => (m.HtmlBody?.Length ?? 0) > capabilities.MaxHtmlLength);
        return tooLong > 0 ? new TransportFitResult(TransportFitReason.HtmlTooLong, tooLong) : TransportFitResult.Fits;
    }

    private static long ImageBytes(OutgoingUserMessage message) => message.InlineImages.Sum(i => (long)i.Data.Length);
}
