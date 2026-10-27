using System.Net;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Model;
using AdMng.Core.Schema;

namespace AdMng.Core.Messaging;

/// <summary>How serious a <see cref="MessageIssue"/> is.</summary>
public enum MessageIssueSeverity
{
    /// <summary>For information only (e.g. the address came from proxyAddresses).</summary>
    Info,

    /// <summary>The message can be sent, but the operator should look at it.</summary>
    Warning,

    /// <summary>The message cannot be sent.</summary>
    Error,

    /// <summary>The account is skipped (the condition is not met).</summary>
    Skip,
}

/// <summary>Codes of <see cref="MessageIssue"/> (localization keys <c>UserMessages.Issue.{code}</c>).</summary>
public static class MessageIssueCodes
{
    // Template level (block the plan).
    public const string TemplateSyntax = "TemplateSyntax";
    public const string ForbiddenField = "ForbiddenField";
    public const string ForbiddenInput = "ForbiddenInput";
    public const string MissingInput = "MissingInput";
    public const string HtmlNotAllowed = "HtmlNotAllowed";
    public const string NoSender = "NoSender";

    // Message level.
    public const string NotFound = "NotFound";
    public const string NoAddress = "NoAddress";
    public const string ManagerMissing = "ManagerMissing";
    public const string ManagerNoAddress = "ManagerNoAddress";
    public const string ManagerDisabled = "ManagerDisabled";
    public const string ManagerInstead = "ManagerInstead";
    public const string ConditionNotMet = "ConditionNotMet";
    public const string EmptySubject = "EmptySubject";
    public const string SubjectTooLong = "SubjectTooLong";
    public const string LinkNotAllowed = "LinkNotAllowed";
    public const string AccountDisabled = "AccountDisabled";
    public const string EmptyField = "EmptyField";
    public const string DuplicateAddress = "DuplicateAddress";
    public const string ProxyAddressUsed = "ProxyAddressUsed";

    /// <summary>Every code (for the localization test).</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        TemplateSyntax, ForbiddenField, ForbiddenInput, MissingInput, HtmlNotAllowed, NoSender, NotFound, NoAddress, ManagerMissing,
        ManagerNoAddress, ManagerDisabled, ManagerInstead, ConditionNotMet, EmptySubject, SubjectTooLong, LinkNotAllowed,
        AccountDisabled, EmptyField, DuplicateAddress, ProxyAddressUsed,
    ];
}

/// <summary>One finding of the validation; <see cref="Argument"/> is shown in the text (field, host, input name …).</summary>
public sealed record MessageIssue(string Code, MessageIssueSeverity Severity, string? Argument = null);

/// <summary>Status of a planned message.</summary>
public enum PlannedMessageStatus
{
    Ready,
    Warning,
    Error,
    Skipped,
}

/// <summary>The message of one account after planning and validation.</summary>
public sealed record PlannedUserMessage
{
    /// <summary>Position in the plan (0-based).</summary>
    public required int Index { get; init; }

    public required string DistinguishedName { get; init; }

    /// <summary>Display name (or RDN) of the account.</summary>
    public required string Name { get; init; }

    public string? Login { get; init; }

    public MessageLanguage Language { get; init; }

    public required PlannedMessageStatus Status { get; init; }

    public IReadOnlyList<MessageIssue> Issues { get; init; } = [];

    /// <summary>The rendered message (also for skipped accounts, for the preview); null when it could not be built.</summary>
    public OutgoingUserMessage? Message { get; init; }

    /// <summary>Ready or with warnings, and rendered.</summary>
    public bool CanSend => Message is not null && Status is PlannedMessageStatus.Ready or PlannedMessageStatus.Warning;

    /// <summary>"jan.kowalski@contoso.com" (first To address) or empty.</summary>
    public string Recipient => Message?.To.FirstOrDefault()?.Address ?? string.Empty;

    public override string ToString() => $"{nameof(PlannedUserMessage)} #{Index} {Status}";
}

/// <summary>Result of <see cref="UserMessagePlanner.Plan"/>.</summary>
/// <param name="TemplateIssues">Errors of the template itself; any error blocks the plan (no messages then).</param>
/// <param name="Messages">One message per account, in the order of the request.</param>
public sealed record UserMessagePlan(IReadOnlyList<MessageIssue> TemplateIssues, IReadOnlyList<PlannedUserMessage> Messages)
{
    public bool IsBlocked => TemplateIssues.Any(i => i.Severity == MessageIssueSeverity.Error);

    public int SendableCount => Messages.Count(m => m.CanSend);
}

/// <summary>The sender side of messages (from the settings, the session's domain filled in).</summary>
public sealed record UserMessageSender
{
    public required MessageAddress From { get; init; }

    public MessageAddress? ReplyTo { get; init; }

    /// <summary>CC of every message (helpdesk).</summary>
    public IReadOnlyList<MessageAddress> Cc { get; init; } = [];

    /// <summary>Domains links may point to (subdomains included).</summary>
    public IReadOnlyList<string> AllowedLinkDomains { get; init; } = [];

    /// <summary>Language of recipients without a usable <c>preferredLanguage</c>.</summary>
    public MessageLanguage DefaultLanguage { get; init; } = MessageLanguage.Polish;

    public required TimeZoneInfo TimeZone { get; init; }
}

/// <summary>An account to message, as read from the DC (null <see cref="Account"/>: not found any more).</summary>
public sealed record UserMessageAccount(string DistinguishedName, DirectoryObject? Account, DirectoryObject? Manager = null);

/// <summary>Input of <see cref="UserMessagePlanner.Plan"/>.</summary>
public sealed record UserMessagePlanRequest
{
    public required MessageTemplate Template { get; init; }

    /// <summary>Recipient mode for this run (default: the template's).</summary>
    public MessageRecipientMode? Recipients { get; init; }

    /// <summary>N of the condition for this run (default: the template's).</summary>
    public int? ConditionDays { get; init; }

    /// <summary>Values of <c>{$input:name}</c>.</summary>
    public IReadOnlyDictionary<string, string> Inputs { get; init; } = new Dictionary<string, string>();

    /// <summary>The template's images by content id (U1c; loaded by the composer before planning).</summary>
    public IReadOnlyDictionary<string, Images.MessageImage> Images { get; init; } = new Dictionary<string, Images.MessageImage>(StringComparer.OrdinalIgnoreCase);

    /// <summary>"Send without images" (U1c): every image becomes its alternative text; the preview shows exactly that.</summary>
    public bool OmitImages { get; init; }

    public required UserMessageSender Sender { get; init; }

    public required IReadOnlyList<UserMessageAccount> Accounts { get; init; }

    public required DateTimeOffset Now { get; init; }

    /// <summary>Domain maxPwdAge (estimate when the constructed expiry is missing).</summary>
    public TimeSpan? DomainMaxPasswordAge { get; init; }

    /// <summary>Lock state from lockoutTime when the computed UAC bit is missing.</summary>
    public Func<long, bool>? IsLockedByTime { get; init; }

    /// <summary>DNS domain of the session ({$domain}).</summary>
    public string? Domain { get; init; }

    /// <summary>Schema of the session (confidential attributes are forbidden fields too).</summary>
    public ISchemaCatalog? Schema { get; init; }
}

/// <summary>
/// Plans messages to users (README U1, pipeline steps 1–2): checks the template, evaluates the condition for every account
/// from its fresh read, resolves the recipients, renders subject, text and HTML in the recipient's language and validates
/// every message. Pure and in memory – the caller reads the accounts and managers from the window's DC.
/// </summary>
public sealed class UserMessagePlanner
{
    /// <summary>Attributes always read for an account (plus the ones the template references).</summary>
    public static IReadOnlyList<string> BaseAccountAttributes { get; } =
    [
        .. AccountStatus.Attributes, Attr.Name, Attr.DisplayName, Attr.GivenName, Attr.Sn, Attr.SamAccountName, Attr.UserPrincipalName,
        Attr.Mail, Attr.ProxyAddresses, Attr.Manager, PreferredLanguage, Attr.ObjectClass,
    ];

    /// <summary>Attributes always read for a manager (plus the <c>{$manager:…}</c> ones).</summary>
    public static IReadOnlyList<string> BaseManagerAttributes { get; } =
        [Attr.Name, Attr.DisplayName, Attr.GivenName, Attr.Sn, Attr.Mail, Attr.ProxyAddresses, Attr.UserAccountControl];

    /// <summary>The recipient's language attribute.</summary>
    public const string PreferredLanguage = "preferredLanguage";

    private readonly UserMessageFields _fields;

    public UserMessagePlanner(UserMessageFields fields)
    {
        _fields = fields ?? throw new ArgumentNullException(nameof(fields));
    }

    public UserMessageFields Fields => _fields;

    /// <summary>Names of the <c>{$input:name}</c> fields of a template (both languages, first spelling wins).</summary>
    public IReadOnlyList<string> InputsOf(MessageTemplate template) =>
        [.. TokenArguments(template, UserMessageFields.Input).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Attributes to read for the accounts of <paramref name="template"/>.</summary>
    public IReadOnlyList<string> AccountAttributesFor(MessageTemplate template) =>
        [.. BaseAccountAttributes.Concat(Parts(template).SelectMany(p => _fields.Engine.Parse(p).ReferencedAttributes))
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Attributes to read for the managers.</summary>
    public IReadOnlyList<string> ManagerAttributesFor(MessageTemplate template) =>
        [.. BaseManagerAttributes.Concat(TokenArguments(template, UserMessageFields.Manager)).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Template level validation: syntax, forbidden fields and inputs, disallowed HTML, missing input values.</summary>
    public IReadOnlyList<MessageIssue> CheckTemplate(MessageTemplate template, IReadOnlyDictionary<string, string>? inputs = null, ISchemaCatalog? schema = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        var issues = new List<MessageIssue>();
        foreach (var part in Parts(template))
        {
            var parsed = _fields.Engine.Parse(part);
            issues.AddRange(parsed.Errors.Select(e => new MessageIssue(MessageIssueCodes.TemplateSyntax, MessageIssueSeverity.Error, e)));
            issues.AddRange(parsed.ReferencedAttributes
                .Where(a => UserMessageRules.IsForbiddenAttribute(a, schema))
                .Select(a => new MessageIssue(MessageIssueCodes.ForbiddenField, MessageIssueSeverity.Error, "{" + a + "}")));
            foreach (var token in parsed.SpecialTokens.Where(t => t.Argument is not null))
            {
                if (token.Name == UserMessageFields.Manager && UserMessageRules.IsForbiddenAttribute(token.Argument!.Trim(), schema))
                {
                    issues.Add(new MessageIssue(MessageIssueCodes.ForbiddenField, MessageIssueSeverity.Error, $"{{$manager:{token.Argument}}}"));
                }
                else if (token.Name == UserMessageFields.Input && UserMessageRules.IsForbiddenInputName(token.Argument!))
                {
                    issues.Add(new MessageIssue(MessageIssueCodes.ForbiddenInput, MessageIssueSeverity.Error, $"{{$input:{token.Argument}}}"));
                }
            }
        }

        // U1c: the restricted HTML (first gate + the sanitizer's findings), images, length, link texts, normalization.
        issues.AddRange(Html.MessageHtmlChecks.CheckTemplate(template));

        if (inputs is not null)
        {
            foreach (var input in InputsOf(template).Where(i => !UserMessageRules.IsForbiddenInputName(i)))
            {
                var value = inputs.FirstOrDefault(p => string.Equals(p.Key, input.Trim(), StringComparison.OrdinalIgnoreCase)).Value;
                if (string.IsNullOrWhiteSpace(value))
                {
                    issues.Add(new MessageIssue(MessageIssueCodes.MissingInput, MessageIssueSeverity.Error, input.Trim()));
                }
            }
        }

        return [.. issues.Distinct()];
    }

    /// <summary>Plans one message per account of the request.</summary>
    public UserMessagePlan Plan(UserMessagePlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var templateIssues = CheckTemplate(request.Template, request.Inputs, request.Schema).ToList();
        if (!UserMessageRules.TryParseAddress(request.Sender.From.Address, out _))
        {
            templateIssues.Add(new MessageIssue(MessageIssueCodes.NoSender, MessageIssueSeverity.Error, request.Sender.From.Address));
        }

        if (templateIssues.Any(i => i.Severity == MessageIssueSeverity.Error))
        {
            return new UserMessagePlan(templateIssues, []);
        }

        var allowed = AllowedDomains(request);
        var userAddresses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var messages = new List<PlannedUserMessage>(request.Accounts.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in request.Accounts)
        {
            if (!seen.Add(account.DistinguishedName))
            {
                continue;   // the same account twice → one message
            }

            messages.Add(PlanOne(request, account, messages.Count, allowed, userAddresses));
        }

        // The same user address for several accounts (shared mailbox): every one of them gets a warning.
        var duplicates = userAddresses.Where(p => p.Value > 1).Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (duplicates.Count > 0)
        {
            for (var i = 0; i < messages.Count; i++)
            {
                if (messages[i].Message is { } m && m.To.Any(t => duplicates.Contains(t.Address)) && UsesUserAddress(messages[i]))
                {
                    messages[i] = WithIssue(messages[i], new MessageIssue(MessageIssueCodes.DuplicateAddress, MessageIssueSeverity.Warning, messages[i].Recipient));
                }
            }
        }

        return new UserMessagePlan(templateIssues, messages);
    }

    private PlannedUserMessage PlanOne(
        UserMessagePlanRequest request,
        UserMessageAccount entry,
        int index,
        IReadOnlyList<string> allowedDomains,
        Dictionary<string, int> userAddresses)
    {
        var fallbackName = Ldap.DistinguishedName.TryParse(entry.DistinguishedName, out var dn) ? dn.Name : entry.DistinguishedName;
        if (entry.Account is not { } account)
        {
            return new PlannedUserMessage
            {
                Index = index,
                DistinguishedName = entry.DistinguishedName,
                Name = fallbackName,
                Status = PlannedMessageStatus.Error,
                Issues = [new MessageIssue(MessageIssueCodes.NotFound, MessageIssueSeverity.Error)],
            };
        }

        var issues = new List<MessageIssue>();
        var sender = request.Sender;
        var status = AccountStatus.Evaluate(account, request.Now, sender.TimeZone, request.DomainMaxPasswordAge, request.IsLockedByTime);
        var template = request.Template;

        // The recipient's language, unless the template has no text in it: then the whole message (dates, footer) is in
        // the language of the text it falls back to.
        var language = template.TextLanguage(MessageLanguages.FromCode(account.GetString(PreferredLanguage)) ?? sender.DefaultLanguage);
        var condition = template.Condition;
        var days = request.ConditionDays ?? template.ConditionDays;
        if (!MessageConditions.IsMet(condition, days, status))
        {
            issues.Add(new MessageIssue(MessageIssueCodes.ConditionNotMet, MessageIssueSeverity.Skip, condition.ToString()));
        }

        if (status.IsDisabled)
        {
            issues.Add(new MessageIssue(MessageIssueCodes.AccountDisabled, MessageIssueSeverity.Warning));
        }

        var (to, cc, usesUser) = ResolveRecipients(request.Recipients ?? template.Recipients, account, entry.Manager, issues);
        if (usesUser && to.FirstOrDefault() is { } userAddress)
        {
            userAddresses[userAddress.Address] = userAddresses.GetValueOrDefault(userAddress.Address) + 1;
        }

        var data = new UserMessageFieldData
        {
            Account = account,
            Status = status,
            Manager = entry.Manager,
            Language = language,
            TimeZone = sender.TimeZone,
            Inputs = request.Inputs,
            Domain = request.Domain,
        };
        var text = template.Text(language);
        var empty = new List<string>();
        void Empty(string token)
        {
            if (!empty.Contains(token, StringComparer.Ordinal))
            {
                empty.Add(token);
            }
        }

        var subject = UserMessageRules.HeaderSafe(_fields.Render(text.Subject, data, UserMessageRules.HeaderSafe, Empty));
        var body = _fields.Render(text.Body, data, null, Empty);
        var htmlBody = string.IsNullOrWhiteSpace(text.Html)
            ? UserMessageRules.TextToHtml(body)
            : _fields.Render(text.Html, data, WebUtility.HtmlEncode, Empty);

        // U1c: images (or "send without images"), the text part from the HTML, the whole document checked again.
        var rich = Html.MessageHtmlChecks.Message(request, text, language, ref htmlBody, ref body, issues);
        if (empty.Count > 0)
        {
            issues.Add(new MessageIssue(MessageIssueCodes.EmptyField, MessageIssueSeverity.Warning, string.Join(", ", empty)));
        }

        if (subject.Length == 0)
        {
            issues.Add(new MessageIssue(MessageIssueCodes.EmptySubject, MessageIssueSeverity.Error));
        }
        else if (subject.Length > UserMessageRules.MaxSubjectLength)
        {
            issues.Add(new MessageIssue(MessageIssueCodes.SubjectTooLong, MessageIssueSeverity.Error, subject.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        foreach (var link in UserMessageRules.FindLinks(subject).Concat(UserMessageRules.FindLinks(body)).Concat(UserMessageRules.FindLinks(htmlBody)).Distinct(StringComparer.Ordinal))
        {
            if (!UserMessageRules.IsLinkAllowed(link, allowedDomains))
            {
                issues.Add(new MessageIssue(MessageIssueCodes.LinkNotAllowed, MessageIssueSeverity.Error, UserMessageRules.LinkHost(link)));
            }
        }

        var footer = BuiltInMessageTemplates.Footer(language);
        var message = to.Count == 0
            ? null
            : new OutgoingUserMessage
            {
                Id = Guid.NewGuid(),
                From = sender.From,
                To = to,
                Cc = [.. cc.Concat(sender.Cc).DistinctBy(a => a.Address, StringComparer.OrdinalIgnoreCase)
                    .Where(c => !to.Any(t => string.Equals(t.Address, c.Address, StringComparison.OrdinalIgnoreCase)))],
                ReplyTo = sender.ReplyTo,
                Subject = subject,
                TextBody = UserMessageRules.TextDocument(body, footer),
                HtmlBody = rich.HtmlDocument ?? UserMessageRules.HtmlDocument(htmlBody, footer, language),
                InlineImages = rich.InlineImages,
                Language = language,
            };
        return new PlannedUserMessage
        {
            Index = index,
            DistinguishedName = account.DistinguishedName,
            Name = account.GetString(Attr.DisplayName) is { Length: > 0 } display ? display : account.Name,
            Login = account.GetString(Attr.SamAccountName),
            Language = language,
            Status = StatusOf(issues),
            Issues = [.. issues.Distinct()],
            Message = message,
        };
    }

    /// <summary>To, CC and whether the user's own address is the To address.</summary>
    private static (IReadOnlyList<MessageAddress> To, IReadOnlyList<MessageAddress> Cc, bool UsesUser) ResolveRecipients(
        MessageRecipientMode mode,
        DirectoryObject account,
        DirectoryObject? manager,
        List<MessageIssue> issues)
    {
        var user = AddressOf(account, issues);
        var hasManagerDn = !string.IsNullOrEmpty(account.GetString(Attr.Manager));
        MessageAddress? ManagerAddress()
        {
            if (manager is null)
            {
                issues.Add(new MessageIssue(MessageIssueCodes.ManagerMissing, mode == MessageRecipientMode.Manager ? MessageIssueSeverity.Error : MessageIssueSeverity.Warning));
                return null;
            }

            var address = AddressOf(manager, null);
            if (address is null)
            {
                issues.Add(new MessageIssue(MessageIssueCodes.ManagerNoAddress, mode == MessageRecipientMode.Manager ? MessageIssueSeverity.Error : MessageIssueSeverity.Warning, NameOf(manager)));
                return null;
            }

            if (((UserAccountControl)(manager.GetInt32(Attr.UserAccountControl) ?? 0)).HasFlag(UserAccountControl.AccountDisable))
            {
                issues.Add(new MessageIssue(MessageIssueCodes.ManagerDisabled, MessageIssueSeverity.Warning, NameOf(manager)));
            }

            return address;
        }

        switch (mode)
        {
            case MessageRecipientMode.Manager:
                return ManagerAddress() is { } onlyManager ? ([onlyManager], [], false) : ([], [], false);
            case MessageRecipientMode.UserAndManager:
            {
                var managerAddress = hasManagerDn || manager is not null ? ManagerAddress() : null;
                if (user is not null)
                {
                    return ([user], managerAddress is null ? [] : [managerAddress], true);
                }

                if (managerAddress is not null)
                {
                    issues.Add(new MessageIssue(MessageIssueCodes.ManagerInstead, MessageIssueSeverity.Warning));
                    return ([managerAddress], [], false);
                }

                issues.Add(new MessageIssue(MessageIssueCodes.NoAddress, MessageIssueSeverity.Error));
                return ([], [], false);
            }

            case MessageRecipientMode.UserOrManager:
            {
                if (user is not null)
                {
                    return ([user], [], true);
                }

                if (ManagerAddress() is { } instead)
                {
                    issues.Add(new MessageIssue(MessageIssueCodes.ManagerInstead, MessageIssueSeverity.Info));
                    return ([instead], [], false);
                }

                issues.Add(new MessageIssue(MessageIssueCodes.NoAddress, MessageIssueSeverity.Error));
                return ([], [], false);
            }

            default:
                if (user is null)
                {
                    issues.Add(new MessageIssue(MessageIssueCodes.NoAddress, MessageIssueSeverity.Error));
                    return ([], [], false);
                }

                return ([user], [], true);
        }
    }

    /// <summary><c>mail</c>, else the primary SMTP proxy address (info issue), with the display name; null without a valid one.</summary>
    private static MessageAddress? AddressOf(DirectoryObject obj, List<MessageIssue>? issues)
    {
        var name = UserMessageRules.HeaderSafe(NameOf(obj));
        if (UserMessageRules.TryParseAddress(obj.GetString(Attr.Mail), out var mail))
        {
            return new MessageAddress(mail, name);
        }

        if (UserMessageRules.PrimarySmtpAddress(obj.GetStrings(Attr.ProxyAddresses)) is { } proxy && UserMessageRules.TryParseAddress(proxy, out var primary))
        {
            issues?.Add(new MessageIssue(MessageIssueCodes.ProxyAddressUsed, MessageIssueSeverity.Info, primary));
            return new MessageAddress(primary, name);
        }

        return null;
    }

    private static string NameOf(DirectoryObject obj) => obj.GetString(Attr.DisplayName) is { Length: > 0 } display ? display : obj.Name;

    private static bool UsesUserAddress(PlannedUserMessage message) =>
        !message.Issues.Any(i => i.Code == MessageIssueCodes.ManagerInstead) && message.Message is not null;

    private static PlannedMessageStatus StatusOf(IReadOnlyCollection<MessageIssue> issues) =>
        issues.Any(i => i.Severity == MessageIssueSeverity.Error) ? PlannedMessageStatus.Error
        : issues.Any(i => i.Severity == MessageIssueSeverity.Skip) ? PlannedMessageStatus.Skipped
        : issues.Any(i => i.Severity == MessageIssueSeverity.Warning) ? PlannedMessageStatus.Warning
        : PlannedMessageStatus.Ready;

    private static PlannedUserMessage WithIssue(PlannedUserMessage message, MessageIssue issue)
    {
        var issues = message.Issues.Append(issue).ToList();
        return message with { Issues = issues, Status = StatusOf(issues) };
    }

    private static IReadOnlyList<string> AllowedDomains(UserMessagePlanRequest request) =>
        [.. request.Sender.AllowedLinkDomains
            .Append(request.Domain ?? string.Empty)
            .Append(UserMessageRules.DomainOf(request.Sender.From.Address) ?? string.Empty)
            .Select(UserMessageRules.NormalizeDomain)
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.Ordinal)];

    private static IEnumerable<string> Parts(MessageTemplate template) =>
        template.Texts().SelectMany(t => new[] { t.Subject, t.Body, t.Html ?? string.Empty }).Where(p => !string.IsNullOrEmpty(p));

    private IEnumerable<string> TokenArguments(MessageTemplate template, string token) =>
        Parts(template)
            .SelectMany(p => _fields.Engine.Parse(p).SpecialTokens)
            .Where(t => t.Name == token && !string.IsNullOrWhiteSpace(t.Argument))
            .Select(t => t.Argument!.Trim());
}
