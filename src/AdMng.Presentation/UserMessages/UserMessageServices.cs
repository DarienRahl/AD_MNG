using AdMng.Core.Audit;
using AdMng.Core.Csv;
using AdMng.Core.Messaging;
using AdMng.Presentation.AccountReview;
using AdMng.Presentation.Settings;

namespace AdMng.Presentation.UserMessages;

/// <summary>
/// What the message window needs besides <see cref="Dialogs.DialogServices"/> (parameter object per connection window, with the window's session log): templates,
/// planner, dispatcher with the rate limiter shared by every send of the application session, transport selection, the
/// report exporter and the session log.
/// </summary>
public sealed class UserMessageServices
{
    public UserMessageServices(
        UserMessageTemplateLibrary templates,
        UserMessageTransportSelector transports,
        ICsvCodec csv,
        IAuditLog auditLog,
        TimeProvider time,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        MessageRateLimiter? rateLimiter = null)
    {
        Templates = templates ?? throw new ArgumentNullException(nameof(templates));
        Transports = transports ?? throw new ArgumentNullException(nameof(transports));
        AuditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        ArgumentNullException.ThrowIfNull(csv);
        ArgumentNullException.ThrowIfNull(time);
        Planner = new UserMessagePlanner(new UserMessageFields());
        Dispatcher = new UserMessageDispatcher(time, delay);
        // Application-wide (registered as a singleton): the daily budget counts the sends of every connection window.
        RateLimiter = rateLimiter ?? new MessageRateLimiter(time, delay);
        Reports = new UserMessageReportExporter(csv);
    }

    public UserMessageTemplateLibrary Templates { get; }

    public UserMessageTransportSelector Transports { get; }

    public IAuditLog AuditLog { get; }

    public UserMessagePlanner Planner { get; }

    public UserMessageDispatcher Dispatcher { get; }

    /// <summary>Shared by every send of the application session (the daily budget counts all of them).</summary>
    public MessageRateLimiter RateLimiter { get; }

    public UserMessageReportExporter Reports { get; }

    /// <summary>Retry policy (tests shorten it).</summary>
    public UserMessageDispatchOptions DispatchOptions { get; set; } = new();
}

/// <summary>What the message window opens with: the accounts and a suggested template with its condition parameter.</summary>
/// <param name="DistinguishedNames">The recipients' accounts (one message each).</param>
/// <param name="TemplateId">Template to preselect (null: the free message).</param>
/// <param name="ConditionDays">N of the template's condition (null: the template's default).</param>
public sealed record UserMessageRequest(IReadOnlyList<string> DistinguishedNames, string? TemplateId = null, int? ConditionDays = null)
{
    /// <summary>
    /// The request of the account review's "Wyślij wiadomość…": the built-in template matching the check (with the review's
    /// N days), or the free message for checks without one.
    /// </summary>
    public static UserMessageRequest For(IReadOnlyList<string> distinguishedNames, AccountCheck? check, AccountReviewParameters? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(distinguishedNames);
        parameters ??= new AccountReviewParameters();
        return check switch
        {
            AccountCheck.Locked => new(distinguishedNames, BuiltInMessageTemplates.AccountLocked),
            AccountCheck.PasswordExpired => new(distinguishedNames, BuiltInMessageTemplates.PasswordExpired),
            AccountCheck.PasswordExpiring => new(distinguishedNames, BuiltInMessageTemplates.PasswordExpiring, parameters.ExpiryWarningDays),
            AccountCheck.MustChangePassword => new(distinguishedNames, BuiltInMessageTemplates.MustChangePassword),
            AccountCheck.AccountExpired => new(distinguishedNames, BuiltInMessageTemplates.AccountExpired),
            AccountCheck.AccountExpiring => new(distinguishedNames, BuiltInMessageTemplates.AccountExpiring, parameters.AccountExpiryWarningDays),
            AccountCheck.Inactive or AccountCheck.NeverLoggedOn => new(distinguishedNames, BuiltInMessageTemplates.InactiveDisable, parameters.InactiveDays),
            null => new(distinguishedNames),
            _ => new(distinguishedNames, BuiltInMessageTemplates.Custom),
        };
    }
}

/// <summary>Builds the sender side of messages from the settings (defaults filled in from the session's domain).</summary>
public static class UserMessageSenders
{
    public static UserMessageSender From(UserMessageSettings settings, string? domain)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var language = settings.Language;
        var from = settings.FromAddress ?? (string.IsNullOrWhiteSpace(domain) ? string.Empty : "noreply@" + domain.Trim().ToLowerInvariant());
        return new UserMessageSender
        {
            From = new MessageAddress(from, settings.FromName ?? BuiltInMessageTemplates.SenderName(language)),
            ReplyTo = Address(settings.ReplyTo),
            Cc = Address(settings.Cc) is { } cc ? [cc] : [],
            AllowedLinkDomains = settings.AllowedLinkDomains,
            DefaultLanguage = language,
            TimeZone = UserMessageFields.ResolveTimeZone(settings.TimeZone),
            Salutations = (settings.Salutations ?? new()).ToOptions(),
        };
    }

    private static MessageAddress? Address(string? text) =>
        UserMessageRules.TryParseAddress(text, out var address) ? new MessageAddress(address) : null;
}
