using System.Text.Json.Serialization;
using AdMng.Core.Messaging;

namespace AdMng.Presentation.Settings;

/// <summary>Which transport sends messages to users.</summary>
public enum UserMessageTransportChoice
{
    /// <summary>Through AdMng Watchdog when it is configured and supports it, else direct SMTP.</summary>
    Automatic,

    DirectSmtp,

    Watchdog,
}

/// <summary>The direct SMTP connection of AD Manager. Never a password (it is asked when needed and kept in memory).</summary>
public sealed class SmtpConnectionSettings
{
    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    public SmtpTlsMode Tls { get; set; } = SmtpTlsMode.StartTlsRequired;

    /// <summary>
    /// The operator confirmed the warning for <see cref="SmtpTlsMode.StartTlsWhenAvailable"/> / <see cref="SmtpTlsMode.None"/>;
    /// without it those modes fall back to STARTTLS required.
    /// </summary>
    public bool InsecureTlsConfirmed { get; set; }

    public SmtpCertificateCheck Certificate { get; set; } = SmtpCertificateCheck.SystemTrust;

    /// <summary>SHA-256 of the server certificate for <see cref="SmtpCertificateCheck.PinnedSha256"/>.</summary>
    public string? PinnedSha256 { get; set; }

    public SmtpAuthentication Authentication { get; set; } = SmtpAuthentication.None;

    /// <summary>User name of <see cref="SmtpAuthentication.Basic"/>.</summary>
    public string? UserName { get; set; }

    /// <summary>EHLO domain (empty = the computer's FQDN).</summary>
    public string? LocalDomain { get; set; }

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);

    /// <summary>The transport options (an unconfirmed insecure TLS mode becomes STARTTLS required).</summary>
    public DirectSmtpOptions ToOptions() => new()
    {
        Host = Host?.Trim() ?? string.Empty,
        Port = Port,
        Tls = EffectiveTls,
        Certificate = Certificate,
        PinnedSha256 = PinnedSha256,
        Authentication = Authentication,
        UserName = UserName?.Trim(),
        LocalDomain = string.IsNullOrWhiteSpace(LocalDomain) ? null : LocalDomain.Trim(),
    };

    private SmtpTlsMode EffectiveTls => Tls is SmtpTlsMode.None or SmtpTlsMode.StartTlsWhenAvailable && !InsecureTlsConfirmed
        ? SmtpTlsMode.StartTlsRequired
        : Tls;

    public void Normalize()
    {
        Host = string.IsNullOrWhiteSpace(Host) ? null : Host.Trim();
        Port = Port is >= 1 and <= 65535 ? Port : 587;
        Tls = Enum.IsDefined(Tls) ? EffectiveTls : SmtpTlsMode.StartTlsRequired;
        Certificate = Enum.IsDefined(Certificate) ? Certificate : SmtpCertificateCheck.SystemTrust;
        Authentication = Enum.IsDefined(Authentication) ? Authentication : SmtpAuthentication.None;
        PinnedSha256 = string.IsNullOrWhiteSpace(PinnedSha256) ? null : PinnedSha256.Trim();
        UserName = string.IsNullOrWhiteSpace(UserName) ? null : UserName.Trim();
        LocalDomain = string.IsNullOrWhiteSpace(LocalDomain) ? null : LocalDomain.Trim();
    }
}

/// <summary>
/// Messages to users (README U1): sender, reply-to, CC, rate limit, confirmation threshold, allowed link domains,
/// default language, time zone, transport and the direct SMTP connection, plus the operator's templates (text with fields
/// only, like saved views). Never directory data and never a password (settings test).
/// </summary>
public sealed class UserMessageSettings
{
    public const int MaxTemplates = 200;

    /// <summary>Sender address; null = <c>noreply@&lt;AD DNS domain&gt;</c>.</summary>
    public string? FromAddress { get; set; }

    /// <summary>Sender display name; null = "Dział IT" / "IT Department" in the default language.</summary>
    public string? FromName { get; set; }

    public string? ReplyTo { get; set; }

    /// <summary>CC of every message (helpdesk), or null.</summary>
    public string? Cc { get; set; }

    /// <summary>Where "send a test message to me" goes.</summary>
    public string? TestAddress { get; set; }

    /// <summary>"pl" or "en": recipients without a usable preferredLanguage.</summary>
    public string DefaultLanguage { get; set; } = "pl";

    /// <summary>Time zone of the dates in messages (IANA or Windows id).</summary>
    public string TimeZone { get; set; } = UserMessageFields.DefaultTimeZoneId;

    public int MessagesPerMinute { get; set; } = MessageRateLimiter.DefaultMessagesPerMinute;

    public int RecipientsPerDay { get; set; } = MessageRateLimiter.DefaultRecipientsPerDay;

    /// <summary>Above this number of messages sending needs the number typed as confirmation.</summary>
    public int ConfirmationThreshold { get; set; } = 10;

    /// <summary>Domains links may point to (subdomains included); the AD domain and the sender's domain are always allowed.</summary>
    public List<string> AllowedLinkDomains { get; set; } = [];

    public UserMessageTransportChoice Transport { get; set; } = UserMessageTransportChoice.Automatic;

    /// <summary>The formatted preview of HTML bodies (embedded WebView2, U1c); off = the HTML source only (environments that
    /// forbid an embedded browser).</summary>
    public bool RenderedPreview { get; set; } = true;

    public SmtpConnectionSettings Smtp { get; set; } = new();

    /// <summary>The operator's templates (built-ins are not stored).</summary>
    public List<MessageTemplate> Templates { get; set; } = [];

    /// <summary>The default language as an enum.</summary>
    [JsonIgnore]
    public MessageLanguage Language => MessageLanguages.FromCode(DefaultLanguage) ?? MessageLanguage.Polish;

    /// <summary>Repairs values of a hand-edited or older settings file.</summary>
    public void Normalize()
    {
        FromAddress = Clean(FromAddress);
        FromName = Clean(FromName);
        ReplyTo = Clean(ReplyTo);
        Cc = Clean(Cc);
        TestAddress = Clean(TestAddress);
        DefaultLanguage = MessageLanguages.FromCode(DefaultLanguage) is { } language ? MessageLanguages.Code(language) : "pl";
        TimeZone = Clean(TimeZone) ?? UserMessageFields.DefaultTimeZoneId;
        MessagesPerMinute = Math.Clamp(MessagesPerMinute, 1, 600);
        RecipientsPerDay = Math.Clamp(RecipientsPerDay, 1, 1_000_000);
        ConfirmationThreshold = Math.Clamp(ConfirmationThreshold, 1, 100_000);
        AllowedLinkDomains = [.. (AllowedLinkDomains ?? []).Select(UserMessageRules.NormalizeDomain).Where(d => d.Length > 0).Distinct(StringComparer.Ordinal)];
        Transport = Enum.IsDefined(Transport) ? Transport : UserMessageTransportChoice.Automatic;
        Smtp ??= new();
        Smtp.Normalize();
        Templates = [.. (Templates ?? [])
            .Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Id) && !string.IsNullOrWhiteSpace(t.Name) && !BuiltInMessageTemplates.IsBuiltIn(t.Id))
            .Select(t => t with
            {
                Polish = t.Polish ?? new(),
                English = t.English ?? new(),
                Images = [.. (t.Images ?? []).Where(i => i is not null && i.IsValid).DistinctBy(i => i.ContentId, StringComparer.OrdinalIgnoreCase).Take(Core.Messaging.Images.MessageImageLimits.MaxImagesPerTemplate)],
                Condition = Enum.IsDefined(t.Condition) ? t.Condition : MessageConditionKind.None,
                Recipients = Enum.IsDefined(t.Recipients) ? t.Recipients : MessageRecipientMode.User,
                ConditionDays = Math.Clamp(t.ConditionDays, 0, 3650),
            })
            .DistinctBy(t => t.Id, StringComparer.Ordinal)
            .Take(MaxTemplates)];
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
