using System.Text.Json.Serialization;

namespace AdMng.Watchdog.Contracts.Api;

/// <summary>An e-mail address with an optional display name (messages to users, README U1b).</summary>
public sealed record UserMessageAddressDto
{
    public required string Address { get; init; }
    public string? DisplayName { get; init; }
    public override string ToString() => "UserMessageAddressDto";
}

/// <summary>
/// An inline image of a message to a user (U1c, design note <c>docs/dev/u1c-u1d-rich-html-and-salutations.md</c> section
/// 9): referenced by <c>cid:</c> from the HTML body and sent as an inline part of <c>multipart/related</c> – never an
/// attachment. The service checks the bytes (PNG / JPEG / GIF by magic bytes, structure, dimensions, size) and that
/// <see cref="ContentId"/> is derived from their SHA-256. Unknown members are rejected like in the request.
/// <see cref="ToString"/> shows the id only.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UserMessageImageDto
{
    /// <summary><c>img-&lt;first 16 hex digits of the SHA-256 of Data&gt;@admng</c>.</summary>
    public required string ContentId { get; init; }
    /// <summary>image/png | image/jpeg | image/gif.</summary>
    public required string MediaType { get; init; }
    /// <summary>The image (base64 in JSON).</summary>
    public required byte[] Data { get; init; }
    /// <summary>File name of the MIME part (<c>[A-Za-z0-9._-]{1,64}</c>); never a path.</summary>
    public string? FileName { get; init; }
    public override string ToString() => $"UserMessageImageDto({ContentId})";
}

/// <summary>
/// <c>POST /api/v1/user-messages</c> (Viewer): ONE message to the recipients of one account, already rendered and checked
/// by AD Manager (design note <c>docs/dev/u1-user-messages.md</c>, section 7). Idempotent by <see cref="Id"/>: the same id
/// with the same content from the same caller returns the state of the first submission. There is deliberately no
/// attachment field and unknown members are rejected (400 <c>invalidJson</c>) – an older service rejects
/// <see cref="InlineImages"/> the same way, so AD Manager sends it only with the <see cref="UserMessagingCapabilities.InlineImages"/>
/// capability. The service re-validates everything (<see cref="UserMessageLimits"/>, <see cref="UserMessageIssueCodes"/>)
/// and chooses the envelope sender itself. <see cref="ToString"/> never shows the content.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UserMessageRequest
{
    /// <summary>Idempotency key and Message-ID local part (a GUID, never empty).</summary>
    public Guid Id { get; init; }
    /// <summary>Header From; null = the sender of the service's SMTP channel. Must be allowed by the service
    /// (<c>smtp.from</c>, <c>smtp.replyTo</c> or <c>userMessages.allowedSenders</c>).</summary>
    public UserMessageAddressDto? From { get; init; }
    public IReadOnlyList<UserMessageAddressDto> To { get; init; } = [];
    public IReadOnlyList<UserMessageAddressDto> Cc { get; init; } = [];
    /// <summary>Reply-To; null = the channel's <c>smtp.replyTo</c>. Same allow-list as <see cref="From"/>.</summary>
    public UserMessageAddressDto? ReplyTo { get; init; }
    public string Subject { get; init; } = string.Empty;
    public string TextBody { get; init; } = string.Empty;
    /// <summary>HTML alternative; empty = text only.</summary>
    public string? HtmlBody { get; init; }
    /// <summary>"pl" / "en".</summary>
    public string Language { get; init; } = "pl";
    /// <summary>Images referenced by <c>cid:</c> from <see cref="HtmlBody"/> (U1c); empty = none, and then the member is not
    /// written at all, so a message without images is still accepted by an older service.</summary>
    [JsonOmitWhenEmpty]
    public IReadOnlyList<UserMessageImageDto> InlineImages { get; init; } = [];
    public override string ToString() => $"UserMessageRequest({Id:N})";
}

/// <summary>State of a message to a user as <c>POST /api/v1/user-messages</c> reports it.</summary>
public enum UserMessageState
{
    /// <summary>The SMTP server accepted the message for the To recipients during the request.</summary>
    Delivered,

    /// <summary>The SMTP server rejected it permanently (5xx) – the message is Dead in the delivery log.</summary>
    Rejected,

    /// <summary>In the service's queue: not attempted yet (rate limit, busy queue) or retried after a temporary failure
    /// (4xx, timeout); the service delivers it (retries per its policy) and the delivery log shows the result. The client
    /// counts it as handed over and must NOT send it again.</summary>
    Queued,

    /// <summary>The service's SMTP channel failed (authentication, TLS, configuration, channel disabled): the message is
    /// given up (Dead) and every further message would fail alike.</summary>
    Failed,
}

/// <summary>Answer of <c>POST /api/v1/user-messages</c>: 200 (Delivered / Rejected / Failed) or 202 (Queued).</summary>
public sealed record UserMessageResult
{
    public required Guid Id { get; init; }
    public required UserMessageState State { get; init; }
    /// <summary><c>ok</c>, <c>queued</c>, <c>rateLimit</c>, the delivery code (<c>smtp.550.5.1.1</c>, <c>auth</c>,
    /// <c>tls</c>, <c>timeout</c>, <c>socket.*</c>, <c>smtp.disabled</c>, <c>userMessages.disabled</c> …). Never the
    /// server's reply text.</summary>
    public required string Code { get; init; }
    /// <summary>Basic SMTP status of the answer, when there was one.</summary>
    public int? SmtpStatus { get; init; }
    /// <summary>A <see cref="DeliveryHints"/> code, when one explains the failure.</summary>
    public string? Hint { get; init; }
    /// <summary>CC addresses the SMTP server rejected while it accepted the To recipients (only in the answer of the
    /// request that delivered the message; never stored).</summary>
    public IReadOnlyList<string> RejectedCopies { get; init; } = [];
    /// <summary>The id was submitted before (same caller, same content): the state of that submission.</summary>
    public bool Duplicate { get; init; }
    /// <summary>The row of the delivery log.</summary>
    public long? OutboxId { get; init; }
    public override string ToString() => $"UserMessageResult({Id:N}, {State}, {Code})";
}

/// <summary><c>GET /api/v1/user-messages/status</c> (Viewer): whether AD Manager may send messages to users through the
/// service, over which channel and with which limits.</summary>
public sealed record UserMessagingStatusDto
{
    /// <summary><c>userMessages.enabled</c> (off by default).</summary>
    public bool Enabled { get; init; }
    /// <summary>The SMTP channel is enabled and has a host and a sender.</summary>
    public bool SmtpConfigured { get; init; }
    /// <summary>Messages can be submitted now (<see cref="Enabled"/> and <see cref="SmtpConfigured"/>).</summary>
    public bool Available => Enabled && SmtpConfigured;
    /// <summary>"smtp.contoso.com:587" (no credentials).</summary>
    public string? Channel { get; init; }
    /// <summary>The envelope sender and default From (<c>smtp.from</c>).</summary>
    public string? Sender { get; init; }
    /// <summary>Header From / Reply-To addresses the service accepts besides <see cref="Sender"/> and <c>smtp.replyTo</c>
    /// (<c>@domain</c> entries allow a whole domain).</summary>
    public IReadOnlyList<string> AllowedSenders { get; init; } = [];
    public int MaxMessagesPerHour { get; init; }
    public int MaxMessagesPerDay { get; init; }
    /// <summary>Messages submitted in the last 60 minutes / 24 hours (all callers).</summary>
    public int SubmittedLastHour { get; init; }
    public int SubmittedLastDay { get; init; }
    public int MaxRecipients { get; init; } = UserMessageLimits.MaxRecipients;
    /// <summary>What the service accepts beyond U1b (<see cref="UserMessagingCapabilities"/>); an older service sends none.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    /// <summary>Inline images per message (0 = no images).</summary>
    public int MaxInlineImages { get; init; }
    public long MaxInlineImageBytes { get; init; }
    public long MaxInlineImagesTotalBytes { get; init; }
    /// <summary>Characters of the HTML body (U1b's 100 000 when an older service does not send it).</summary>
    public int MaxHtmlLength { get; init; } = 100_000;
    /// <summary>Bytes of images the outbox holds now (messages not sent yet or given up) and the cap
    /// (<c>userMessages.maxStoredImageMb</c>).</summary>
    public long StoredImageBytes { get; init; }
    public long MaxStoredImageBytes { get; init; }
}

/// <summary>Capabilities of <see cref="UserMessagingStatusDto.Capabilities"/>.</summary>
public static class UserMessagingCapabilities
{
    /// <summary>The service accepts <see cref="UserMessageRequest.InlineImages"/> (and <c>userMessages.allowInlineImages</c>
    /// is on).</summary>
    public const string InlineImages = "inlineImages";
}

/// <summary>Fixed limits of <c>POST /api/v1/user-messages</c> (the service checks them again).</summary>
public static class UserMessageLimits
{
    /// <summary>To addresses of one message (the user and/or the manager).</summary>
    public const int MaxTo = 2;
    /// <summary>To + CC of one message.</summary>
    public const int MaxRecipients = 10;
    /// <summary>Subject length after rendering (as UserMessageRules.MaxSubjectLength of AD Manager).</summary>
    public const int MaxSubjectLength = 250;
    public const int MaxDisplayNameLength = 200;
    public const int MaxAddressLength = 320;
    /// <summary>Characters of the text body.</summary>
    public const int MaxTextLength = 50_000;
    /// <summary>Characters of the HTML body (U1c: Outlook HTML is verbose once its styles are inlined; equal to AD Manager's
    /// MessageHtmlLimits.MaxHtmlLength).</summary>
    public const int MaxHtmlLength = 400_000;
    /// <summary>Inline images of one message (as AD Manager's MessageImageLimits).</summary>
    public const int MaxInlineImages = 10;
    /// <summary>Bytes of one inline image (1 MiB).</summary>
    public const long MaxInlineImageBytes = 1024 * 1024;
    /// <summary>Bytes of all inline images of one message (2 MiB).</summary>
    public const long MaxInlineImagesTotalBytes = 2L * 1024 * 1024;
}

/// <summary>Issue codes of a rejected message (400, <c>extensions.issues</c>) and of 409/429 answers.</summary>
public static class UserMessageIssueCodes
{
    public const string Required = "required";
    public const string Range = "range";
    public const string InvalidAddress = "invalidAddress";
    public const string HeaderInjection = "headerInjection";
    public const string TooLong = "tooLong";
    public const string ForbiddenHtml = "forbiddenHtml";
    public const string InvalidLanguage = "invalidLanguage";
    /// <summary>From or Reply-To is not an allowed sender of the service – every message of the run would fail alike.</summary>
    public const string SenderNotAllowed = "senderNotAllowed";
    /// <summary>409: the id was used before by another caller or with other content.</summary>
    public const string IdempotencyConflict = "idempotencyConflict";
    /// <summary>409: <c>userMessages.enabled</c> is off.</summary>
    public const string Disabled = "userMessagesDisabled";
    /// <summary>409: the SMTP channel is disabled or incomplete.</summary>
    public const string SmtpNotConfigured = "smtpNotConfigured";
    /// <summary>429: <c>userMessages.maxMessagesPerHour</c> reached.</summary>
    public const string HourlyLimit = "hourlyLimit";
    /// <summary>429: <c>userMessages.maxMessagesPerDay</c> reached.</summary>
    public const string DailyLimit = "dailyLimit";
    /// <summary>An inline image is not a valid PNG / JPEG / GIF within the limits, or its id is used twice.</summary>
    public const string InvalidImage = "invalidImage";
    /// <summary>The HTML references a <c>cid:</c> without an image.</summary>
    public const string MissingImage = "missingImage";
    /// <summary>An image the HTML does not reference.</summary>
    public const string UnreferencedImage = "unreferencedImage";
    /// <summary>The content id is not the one derived from the SHA-256 of the image.</summary>
    public const string ImageIdMismatch = "imageIdMismatch";
    /// <summary>Too many images or too many bytes.</summary>
    public const string TooLarge = "tooLarge";
    /// <summary>Images while <c>userMessages.allowInlineImages</c> is off.</summary>
    public const string ImagesDisabled = "imagesDisabled";
    /// <summary>429: the images in the outbox reached <c>userMessages.maxStoredImageMb</c> (messages without images are not
    /// affected).</summary>
    public const string ImageStorageFull = "imageStorageFull";
}
