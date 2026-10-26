using System.Globalization;
using System.Text.Json.Serialization;
using AdMng.Core.ActiveDirectory;

namespace AdMng.Core.Messaging;

/// <summary>Language of a message to a user (chosen by the recipient's <c>preferredLanguage</c>).</summary>
public enum MessageLanguage
{
    Polish,
    English,
}

/// <summary>Cultures and codes of <see cref="MessageLanguage"/>.</summary>
public static class MessageLanguages
{
    private static readonly CultureInfo PolishCulture = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-US");

    /// <summary>The culture of dates and numbers in a message (pl-PL / en-US).</summary>
    public static CultureInfo Culture(MessageLanguage language) => language == MessageLanguage.English ? EnglishCulture : PolishCulture;

    /// <summary>"pl" / "en".</summary>
    public static string Code(MessageLanguage language) => language == MessageLanguage.English ? "en" : "pl";

    /// <summary>
    /// The language of a <c>preferredLanguage</c> value or a setting ("pl", "pl-PL", "en-GB" …); null for anything else
    /// (the default language applies then).
    /// </summary>
    public static MessageLanguage? FromCode(string? code)
    {
        var value = code?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (value.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return MessageLanguage.English;
        }

        return value.StartsWith("pl", StringComparison.OrdinalIgnoreCase) ? MessageLanguage.Polish : null;
    }
}

/// <summary>Who receives the message of an account. Always one message per account.</summary>
public enum MessageRecipientMode
{
    /// <summary>The user (mail, else the primary SMTP proxy address).</summary>
    User,

    /// <summary>The user's manager instead of the user.</summary>
    Manager,

    /// <summary>The user in To and the manager in CC (one of them alone when the other has no address).</summary>
    UserAndManager,

    /// <summary>The user, else the manager (accounts without a mailbox).</summary>
    UserOrManager,
}

/// <summary>Send condition of a template, re-evaluated when the messages are planned.</summary>
public enum MessageConditionKind
{
    None,

    /// <summary>The password expires within N days (0 = today).</summary>
    PasswordExpiresWithin,

    PasswordExpired,

    /// <summary>The account expires within N days (last day of the account).</summary>
    AccountExpiresWithin,

    AccountExpired,

    Locked,

    NotLocked,

    /// <summary>"User must change password at next logon".</summary>
    MustChangePassword,

    /// <summary>
    /// No logon (lastLogonTimestamp) for N days or more, or never logged on and created N days ago or earlier – the same
    /// boundary as the account review's "Inactive &gt; N days" and the Inactive chip (<c>lastLogonTimestamp &lt;= now − N</c>).
    /// </summary>
    InactiveFor,
}

/// <summary>Evaluation of <see cref="MessageConditionKind"/> against an <see cref="AccountStatus"/>.</summary>
public static class MessageConditions
{
    /// <summary>The condition takes the day parameter N.</summary>
    public static bool UsesDays(MessageConditionKind kind) =>
        kind is MessageConditionKind.PasswordExpiresWithin or MessageConditionKind.AccountExpiresWithin or MessageConditionKind.InactiveFor;

    /// <summary>True when the account meets the condition at the status' reference time.</summary>
    public static bool IsMet(MessageConditionKind kind, int days, AccountStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return kind switch
        {
            MessageConditionKind.None => true,
            MessageConditionKind.PasswordExpiresWithin =>
                status.Password == PasswordState.Expires && status.PasswordDaysLeft is { } left && left <= days,
            MessageConditionKind.PasswordExpired => status.Password == PasswordState.Expired,
            MessageConditionKind.AccountExpiresWithin =>
                !status.IsAccountExpired && status.AccountDaysLeft is { } accountLeft && accountLeft <= days,
            MessageConditionKind.AccountExpired => status.IsAccountExpired,
            MessageConditionKind.Locked => status.IsLocked,
            MessageConditionKind.NotLocked => !status.IsLocked,
            MessageConditionKind.MustChangePassword => status.Password == PasswordState.MustChange,
            MessageConditionKind.InactiveFor => status.DaysSinceLogon is { } since
                ? since >= days
                : status.DaysSinceCreated is { } created && created >= days,
            _ => false,
        };
    }
}

/// <summary>Subject and body of one language of a template (with fields, see <see cref="UserMessageFields"/>).</summary>
public sealed record MessageTemplateText
{
    public string Subject { get; init; } = string.Empty;

    /// <summary>Plain text body.</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>Optional HTML body; null or empty = generated from <see cref="Body"/>.</summary>
    public string? Html { get; init; }

    /// <summary>Neither subject nor body.</summary>
    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Subject) && string.IsNullOrWhiteSpace(Body) && string.IsNullOrWhiteSpace(Html);
}

/// <summary>
/// A message template: name, Polish and English text, send condition with its day parameter and recipient mode. Operator
/// templates are stored in the settings (text only, never directory data); built-ins come from
/// <see cref="BuiltInMessageTemplates"/>.
/// </summary>
public sealed record MessageTemplate
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Name { get; init; } = string.Empty;

    public MessageTemplateText Polish { get; init; } = new();

    public MessageTemplateText English { get; init; } = new();

    public MessageConditionKind Condition { get; init; }

    /// <summary>Default N of the condition (changeable when sending).</summary>
    public int ConditionDays { get; init; }

    public MessageRecipientMode Recipients { get; init; } = MessageRecipientMode.User;

    /// <summary>The text of <paramref name="language"/>; an empty one falls back to the other language.</summary>
    public MessageTemplateText Text(MessageLanguage language)
    {
        var (preferred, other) = language == MessageLanguage.English ? (English, Polish) : (Polish, English);
        return preferred is { IsEmpty: false } || other is null or { IsEmpty: true } ? preferred ?? new() : other;
    }

    /// <summary>The texts of both languages (for validation).</summary>
    public IEnumerable<MessageTemplateText> Texts() => [Polish ?? new(), English ?? new()];
}

/// <summary>An e-mail address with an optional display name.</summary>
public sealed record MessageAddress(string Address, string? DisplayName = null)
{
    public override string ToString() => string.IsNullOrWhiteSpace(DisplayName) ? Address : $"{DisplayName} <{Address}>";
}

/// <summary>
/// A rendered and checked message to one account, as a transport sends it. There is deliberately no attachment field.
/// <see cref="ToString"/> never shows the content (diagnostic logs stay free of directory values).
/// </summary>
public sealed record OutgoingUserMessage
{
    /// <summary>Unique id (Message-ID local part).</summary>
    public required Guid Id { get; init; }

    public required MessageAddress From { get; init; }

    /// <summary>The recipients of this one account (the user and/or the manager).</summary>
    public required IReadOnlyList<MessageAddress> To { get; init; }

    public IReadOnlyList<MessageAddress> Cc { get; init; } = [];

    public MessageAddress? ReplyTo { get; init; }

    /// <summary>Header-safe subject (no CR/LF or control characters).</summary>
    public required string Subject { get; init; }

    public required string TextBody { get; init; }

    public required string HtmlBody { get; init; }

    public MessageLanguage Language { get; init; }

    /// <summary>Recipients of the message (To + CC) – counted by the daily limit.</summary>
    public int RecipientCount => To.Count + Cc.Count;

    public override string ToString() => $"{nameof(OutgoingUserMessage)} {Id:N}";
}
