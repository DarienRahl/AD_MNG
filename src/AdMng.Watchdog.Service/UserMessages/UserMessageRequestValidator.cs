using System.Security.Cryptography;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Html;
using AdMng.Core.Messaging.Images;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Service.Notifications;

namespace AdMng.Watchdog.Service.UserMessages;

/// <summary>What the service sends for a valid request (U1c): the sanitized HTML document and the checked, stripped images.</summary>
public sealed record UserMessageContent(string? HtmlBody, IReadOnlyList<MessageImage> Images)
{
    public override string ToString() => $"UserMessageContent({Images.Count} images)";
}

/// <summary>
/// The service's own check of a message to a user (README U1b) – the same security rules AD Manager applied
/// (<see cref="UserMessageRules"/>), never trusting the client: strict single-mailbox addresses, no CR/LF or other control
/// characters in anything that ends in a header (rejected, not repaired), <see cref="UserMessageLimits"/>, the restricted
/// HTML, a known language and – the service decides who the message comes from – From / Reply-To only from
/// <c>smtp.from</c>, <c>smtp.replyTo</c> and <c>userMessages.allowedSenders</c>. Attachments cannot be expressed (the
/// request type rejects unknown members). Issue paths are camelCase JSON paths; messages never quote a value.
/// <para>U1c (design note section 9.3): the HTML body is checked by the shared <see cref="MessageHtmlSanitizer"/> in
/// document mode – security findings are <c>forbiddenHtml</c>, a <c>cid:</c> without its image <c>missingImage</c> – and
/// inline images must be allowed (<c>userMessages.allowInlineImages</c>, else <c>imagesDisabled</c>), within the limits
/// (<c>tooLarge</c>), valid PNG / JPEG / GIF (<c>invalidImage</c>), identified by the SHA-256 of their bytes
/// (<c>imageIdMismatch</c>) and referenced by the HTML (<c>unreferencedImage</c>). The service then sends the sanitized
/// document and the stripped images, so cosmetic policy differences between versions never fail a run, security
/// differences always do.</para>
/// </summary>
public static class UserMessageRequestValidator
{
    /// <summary>The issues of <paramref name="request"/> (empty = valid).</summary>
    public static IReadOnlyList<ConfigIssue> Validate(UserMessageRequest request, SmtpSettings smtp, UserMessagingSettings settings) =>
        Validate(request, smtp, settings, out _);

    /// <summary>The issues of <paramref name="request"/> (empty = valid) and, when valid, what the service sends.</summary>
    public static IReadOnlyList<ConfigIssue> Validate(UserMessageRequest request, SmtpSettings smtp, UserMessagingSettings settings, out UserMessageContent? content)
    {
        content = null;
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(smtp);
        ArgumentNullException.ThrowIfNull(settings);
        var issues = new List<ConfigIssue>();
        if (request.Id == Guid.Empty)
        {
            Add(issues, "id", UserMessageIssueCodes.Required, "The message id is required.");
        }

        var to = request.To ?? [];
        var cc = request.Cc ?? [];
        if (to.Count is < 1 or > UserMessageLimits.MaxTo)
        {
            Add(issues, "to", UserMessageIssueCodes.Range, "A message has one or two To recipients.");
        }

        if (to.Count + cc.Count > UserMessageLimits.MaxRecipients)
        {
            Add(issues, "cc", UserMessageIssueCodes.Range, "A message has at most 10 recipients.");
        }

        for (var i = 0; i < to.Count; i++)
        {
            Mailbox(issues, $"to[{i}]", to[i]);
        }

        for (var i = 0; i < cc.Count; i++)
        {
            Mailbox(issues, $"cc[{i}]", cc[i]);
        }

        if (request.From is { } from && Mailbox(issues, "from", from) && !IsAllowedSender(from.Address, smtp, settings))
        {
            Add(issues, "from", UserMessageIssueCodes.SenderNotAllowed, "The sender is not allowed by the service (smtp.from or userMessages.allowedSenders).");
        }

        if (request.ReplyTo is { } replyTo && Mailbox(issues, "replyTo", replyTo) && !IsAllowedSender(replyTo.Address, smtp, settings)
            && !string.Equals(replyTo.Address.Trim(), request.From?.Address.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            Add(issues, "replyTo", UserMessageIssueCodes.SenderNotAllowed, "The reply-to address is not allowed by the service.");
        }

        if (string.IsNullOrWhiteSpace(request.Subject))
        {
            Add(issues, "subject", UserMessageIssueCodes.Required, "The subject is required.");
        }
        else if (HasControl(request.Subject))
        {
            Add(issues, "subject", UserMessageIssueCodes.HeaderInjection, "The subject contains line breaks or control characters.");
        }
        else if (request.Subject.Length > UserMessageLimits.MaxSubjectLength)
        {
            Add(issues, "subject", UserMessageIssueCodes.TooLong, "The subject is longer than 250 characters.");
        }

        if (string.IsNullOrWhiteSpace(request.TextBody))
        {
            Add(issues, "textBody", UserMessageIssueCodes.Required, "The text body is required.");
        }
        else if (request.TextBody.Length > UserMessageLimits.MaxTextLength)
        {
            Add(issues, "textBody", UserMessageIssueCodes.TooLong, "The text body is too long.");
        }

        var images = Images(request, settings, issues);
        string? sanitized = null;
        if (request.HtmlBody is { Length: > 0 } html)
        {
            if (html.Length > UserMessageLimits.MaxHtmlLength)
            {
                Add(issues, "htmlBody", UserMessageIssueCodes.TooLong, "The HTML body is too long.");
            }
            else if (UserMessageRules.FindForbiddenHtml(html).Count > 0)
            {
                Add(issues, "htmlBody", UserMessageIssueCodes.ForbiddenHtml, "The HTML body contains forbidden elements, handlers or URL schemes.");
            }
            else
            {
                sanitized = CheckHtml(html, request, issues);
            }
        }
        else if ((request.InlineImages ?? []).Count > 0)
        {
            Add(issues, "htmlBody", UserMessageIssueCodes.Required, "Inline images need an HTML body that references them.");
        }

        if (MessageLanguages.FromCode(request.Language) is null || request.Language.Trim().Length > 5)
        {
            Add(issues, "language", UserMessageIssueCodes.InvalidLanguage, "The language is pl or en.");
        }

        if (issues.Count == 0)
        {
            content = new UserMessageContent(sanitized, [.. images.Values]);
        }

        return issues;
    }

    /// <summary>The checked images by content id (U1c); every problem is an issue.</summary>
    private static Dictionary<string, MessageImage> Images(UserMessageRequest request, UserMessagingSettings settings, List<ConfigIssue> issues)
    {
        var result = new Dictionary<string, MessageImage>(StringComparer.OrdinalIgnoreCase);
        var images = request.InlineImages ?? [];
        if (images.Count == 0)
        {
            return result;
        }

        if (!settings.AllowInlineImages)
        {
            Add(issues, "inlineImages", UserMessageIssueCodes.ImagesDisabled, "Inline images are switched off in the service (userMessages.allowInlineImages).");
            return result;
        }

        if (images.Count > UserMessageLimits.MaxInlineImages
            || images.Sum(i => (long)(i?.Data?.Length ?? 0)) > UserMessageLimits.MaxInlineImagesTotalBytes)
        {
            Add(issues, "inlineImages", UserMessageIssueCodes.TooLarge, "The message has too many images or too many image bytes.");
            return result;
        }

        for (var i = 0; i < images.Count; i++)
        {
            var path = $"inlineImages[{i}]";
            var image = images[i];
            if (image?.Data is not { Length: > 0 } data || string.IsNullOrWhiteSpace(image.ContentId) || string.IsNullOrWhiteSpace(image.MediaType))
            {
                Add(issues, path, UserMessageIssueCodes.InvalidImage, "An image needs a content id, a media type and its bytes.");
                continue;
            }

            if (data.Length > UserMessageLimits.MaxInlineImageBytes)
            {
                Add(issues, path + ".data", UserMessageIssueCodes.TooLarge, "An image is larger than 1 MiB.");
                continue;
            }

            if (!string.Equals(image.ContentId.Trim(), MessageImage.ContentIdFor(Convert.ToHexStringLower(SHA256.HashData(data))), StringComparison.OrdinalIgnoreCase))
            {
                Add(issues, path + ".contentId", UserMessageIssueCodes.ImageIdMismatch, "The content id is not derived from the image's SHA-256.");
                continue;
            }

            if (image.FileName is { } name && (name.Length > 64 || name.Any(c => char.IsControl(c) || c is '/' or '\\')))
            {
                Add(issues, path + ".fileName", UserMessageIssueCodes.InvalidImage, "A file name is at most 64 characters without a path.");
                continue;
            }

            try
            {
                var created = MessageImage.Create(data, image.FileName, image.MediaType, i + 1);
                if (!result.TryAdd(image.ContentId.Trim(), created with { ContentId = image.ContentId.Trim().ToLowerInvariant() }))
                {
                    Add(issues, path + ".contentId", UserMessageIssueCodes.InvalidImage, "The same image is sent twice.");
                }
            }
            catch (MessageImageException)
            {
                Add(issues, path, UserMessageIssueCodes.InvalidImage, "An image is not a valid PNG, JPEG or GIF within the limits.");
            }
        }

        return result;
    }

    /// <summary>The shared sanitizer's security findings and the image references of the HTML; returns the sanitized
    /// document the service sends.</summary>
    private static string CheckHtml(string html, UserMessageRequest request, List<ConfigIssue> issues)
    {
        var declared = (request.InlineImages ?? []).Where(i => !string.IsNullOrWhiteSpace(i?.ContentId)).Select(i => i.ContentId.Trim());
        var result = MessageHtmlSanitizer.Sanitize(html, new HtmlSanitizeOptions
        {
            Mode = HtmlSanitizeMode.Document,
            InlineImageIds = declared.ToHashSet(StringComparer.OrdinalIgnoreCase),
        });
        if (result.Changes.Any(c => c.IsSecurity && c.Kind == HtmlChangeKind.MissingImage))
        {
            Add(issues, "htmlBody", UserMessageIssueCodes.MissingImage, "The HTML body references an image the message does not carry.");
        }

        if (result.Changes.Any(c => c.Kind == HtmlChangeKind.TooDeep))
        {
            Add(issues, "htmlBody", UserMessageIssueCodes.ForbiddenHtml, "The HTML body is nested too deeply.");
        }
        else if (result.Changes.Any(c => c.IsSecurity && c.Kind != HtmlChangeKind.MissingImage))
        {
            Add(issues, "htmlBody", UserMessageIssueCodes.ForbiddenHtml, "The HTML body contains active content, remote resources or hidden content.");
        }

        var referenced = result.ImageIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = request.InlineImages ?? [];
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i]?.ContentId is { } id && !referenced.Contains(id.Trim()))
            {
                Add(issues, $"inlineImages[{i}]", UserMessageIssueCodes.UnreferencedImage, "The HTML body does not reference this image.");
            }
        }

        return result.Html;
    }

    /// <summary>The addresses of a valid request as the outbox stores them: From / Reply-To default to the channel's.</summary>
    /// <exception cref="FormatException">The channel's sender is not a valid address (checked by the status first).</exception>
    public static UserMessageEnvelope Envelope(UserMessageRequest request, SmtpSettings smtp)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(smtp);
        var channelFrom = MimeMessageFactory.ParseMailbox(smtp.From ?? string.Empty);
        var channelReplyTo = string.IsNullOrWhiteSpace(smtp.ReplyTo) ? null : MimeMessageFactory.ParseMailbox(smtp.ReplyTo);
        return new UserMessageEnvelope
        {
            From = request.From is { } from ? Mailbox(from) : new UserMessageMailbox { Address = channelFrom.Address, DisplayName = Blank(channelFrom.Name) },
            ReplyTo = request.ReplyTo is { } replyTo ? Mailbox(replyTo)
                : channelReplyTo is null ? null : new UserMessageMailbox { Address = channelReplyTo.Address, DisplayName = Blank(channelReplyTo.Name) },
            To = [.. (request.To ?? []).Select(Mailbox)],
            Cc = [.. (request.Cc ?? []).Select(Mailbox)],
        };
    }

    /// <summary>True when <paramref name="address"/> may appear as From / Reply-To: the address of <c>smtp.from</c> or
    /// <c>smtp.replyTo</c>, an address of <c>userMessages.allowedSenders</c> or an address in one of its <c>@domain</c>
    /// entries (that domain exactly, case-insensitive).</summary>
    public static bool IsAllowedSender(string address, SmtpSettings smtp, UserMessagingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(smtp);
        ArgumentNullException.ThrowIfNull(settings);
        if (!UserMessageRules.TryParseAddress(address, out var parsed))
        {
            return false;
        }

        if (string.Equals(parsed, AddressOf(smtp.From), StringComparison.OrdinalIgnoreCase)
            || string.Equals(parsed, AddressOf(smtp.ReplyTo), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var domain = UserMessageRules.DomainOf(parsed);
        return (settings.AllowedSenders ?? []).Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).Any(entry => entry.StartsWith('@')
            ? string.Equals(entry[1..], domain, StringComparison.OrdinalIgnoreCase)
            : string.Equals(entry, parsed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The bare address of a configured mailbox ("AD Watchdog &lt;watchdog@contoso.com&gt;"), or null.</summary>
    public static string? AddressOf(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        try
        {
            return MimeMessageFactory.ParseMailbox(configured).Address;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool Mailbox(List<ConfigIssue> issues, string path, UserMessageAddressDto? mailbox)
    {
        if (mailbox is null || string.IsNullOrWhiteSpace(mailbox.Address))
        {
            Add(issues, path + ".address", UserMessageIssueCodes.Required, "An address is required.");
            return false;
        }

        var valid = true;
        if (mailbox.Address.Length > UserMessageLimits.MaxAddressLength || HasControl(mailbox.Address) || !UserMessageRules.TryParseAddress(mailbox.Address, out _))
        {
            Add(issues, path + ".address", HasControl(mailbox.Address) ? UserMessageIssueCodes.HeaderInjection : UserMessageIssueCodes.InvalidAddress,
                "An address is not a single valid mailbox.");
            valid = false;
        }

        if (mailbox.DisplayName is { } name)
        {
            if (HasControl(name))
            {
                Add(issues, path + ".displayName", UserMessageIssueCodes.HeaderInjection, "A display name contains line breaks or control characters.");
                valid = false;
            }
            else if (name.Length > UserMessageLimits.MaxDisplayNameLength)
            {
                Add(issues, path + ".displayName", UserMessageIssueCodes.TooLong, "A display name is longer than 200 characters.");
                valid = false;
            }
        }

        return valid;
    }

    private static UserMessageMailbox Mailbox(UserMessageAddressDto mailbox) =>
        new() { Address = mailbox.Address.Trim(), DisplayName = Blank(mailbox.DisplayName) };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>CR, LF, every other control character and the Unicode line / paragraph separators.</summary>
    private static bool HasControl(string value) => value.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029');

    private static void Add(List<ConfigIssue> issues, string path, string code, string message) =>
        issues.Add(new ConfigIssue { Path = path, Code = code, Severity = IssueSeverity.Error, Message = message });
}
