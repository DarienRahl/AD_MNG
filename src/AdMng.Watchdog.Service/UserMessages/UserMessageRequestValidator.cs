using AdMng.Core.Messaging;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Service.Notifications;

namespace AdMng.Watchdog.Service.UserMessages;

/// <summary>
/// The service's own check of a message to a user (README U1b) – the same security rules AD Manager applied
/// (<see cref="UserMessageRules"/>), never trusting the client: strict single-mailbox addresses, no CR/LF or other control
/// characters in anything that ends in a header (rejected, not repaired), <see cref="UserMessageLimits"/>, the restricted
/// HTML, a known language and – the service decides who the message comes from – From / Reply-To only from
/// <c>smtp.from</c>, <c>smtp.replyTo</c> and <c>userMessages.allowedSenders</c>. Attachments cannot be expressed (the
/// request type rejects unknown members). Issue paths are camelCase JSON paths; messages never quote a value.
/// </summary>
public static class UserMessageRequestValidator
{
    /// <summary>The issues of <paramref name="request"/> (empty = valid).</summary>
    public static IReadOnlyList<ConfigIssue> Validate(UserMessageRequest request, SmtpSettings smtp, UserMessagingSettings settings)
    {
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
        }

        if (MessageLanguages.FromCode(request.Language) is null || request.Language.Trim().Length > 5)
        {
            Add(issues, "language", UserMessageIssueCodes.InvalidLanguage, "The language is pl or en.");
        }

        return issues;
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
