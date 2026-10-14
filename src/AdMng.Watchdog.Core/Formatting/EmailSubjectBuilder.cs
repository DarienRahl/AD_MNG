using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Formatting;

/// <summary>
/// E-mail subjects (section 14.2): <c>{prefix}[SEVERITY] Title: object [→ related] – actor</c>, aggregated
/// <c>{prefix}[SEVERITY] Titles: N (OU=…) – actor</c>, follow-ups <c>Re: …</c>, the daily report and the test message.
/// Key facts come first; the object part is cut so the subject stays within <see cref="MaxLength"/> characters; line
/// breaks and control characters never reach a subject.
/// </summary>
public static class EmailSubjectBuilder
{
    /// <summary>Maximum subject length.</summary>
    public const int MaxLength = 200;

    private const string Ellipsis = "…";

    /// <summary>The subject of a single alert.</summary>
    public static string Single(AlertContent content, NotificationContext context)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(context);
        var e = content.Event;
        var objectPart = e.Kind switch
        {
            WatchEventKind.PrivilegeGranted or WatchEventKind.PrivilegeRevoked when e.Member is not null =>
                content.ObjectName + " → " + context.Objects.Name(e.Member),
            WatchEventKind.GroupMemberAdded or WatchEventKind.GroupMemberRemoved when e.Member is not null =>
                context.Objects.Name(e.Member) + " → " + content.ObjectName,
            _ => content.ObjectName,
        };
        var head = Head(context, content.Severity) + content.Title + ":";
        return Compose(head, objectPart, ActorSuffix(e.Attribution, context));
    }

    /// <summary>The subject of an aggregated alert of <paramref name="totalCount"/> events (the first ones given).</summary>
    public static string Aggregated(IReadOnlyList<WatchEvent> events, int totalCount, Severity severity, RuleSettings? rule, NotificationContext context)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(context);
        var title = AggregatedTitle(events, rule, context);
        var head = Head(context, severity) + title + ": " + context.Text.Number(totalCount);
        var parent = ObjectDisplay.CommonParentRdn(events);
        var actors = events.Select(e => context.Objects.ActorShort(e.Attribution)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var actor = actors.Count == 1 && actors[0] is { } single ? "– " + single : string.Empty;
        return Compose(head, parent is null ? string.Empty : "(" + parent + ")", actor);
    }

    /// <summary>"Nowe konta" when all events share a kind, else the rule name (or "Zdarzenia").</summary>
    public static string AggregatedTitle(IReadOnlyList<WatchEvent> events, RuleSettings? rule, NotificationContext context)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(context);
        var kinds = events.Select(e => e.Kind).Distinct().ToList();
        if (kinds.Count == 1)
        {
            return context.Text.KindTitle(kinds[0], plural: true);
        }

        var ruleName = context.Objects.RuleName(rule);
        return ruleName.Length > 0 ? ruleName : context.Text.Get("Subject.Events");
    }

    /// <summary>"Re: original subject".</summary>
    public static string FollowUp(string originalSubject)
    {
        ArgumentNullException.ThrowIfNull(originalSubject);
        var subject = UntrustedText.ForHeader(originalSubject);
        subject = subject.StartsWith("Re: ", StringComparison.OrdinalIgnoreCase) ? subject : "Re: " + subject;
        return subject.Length <= MaxLength ? subject : subject[..(MaxLength - 1)] + Ellipsis;
    }

    /// <summary>"[AD Watchdog] Raport dzienny contoso.com – 14.10.2026: 71 zdarzeń (2 wymagają uwagi)".</summary>
    public static string Digest(string? domain, DateOnly date, int eventCount, int attentionCount, NotificationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var text = context.Text;
        var head = context.SubjectPrefix + " " + text.Format("Subject.Digest", UntrustedText.ForHeader(domain ?? context.InstanceName), context.Time.Date(date))
            + ": " + text.Plural("Subject.DigestEvents", eventCount);
        var attention = attentionCount > 0 ? " (" + text.Plural("Subject.DigestAttention", attentionCount) + ")" : string.Empty;
        return Cut(UntrustedText.ForHeader(head + attention));
    }

    /// <summary>"[AD Watchdog] Wiadomość testowa".</summary>
    public static string Test(NotificationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Cut(context.SubjectPrefix + " " + context.Text.Get("Subject.Test"));
    }

    /// <summary>"– CONTOSO\adm.nowak", "– wykonawca: ustalanie…", "– wykonawca: nieustalony" or empty.</summary>
    public static string ActorSuffix(AdMng.Watchdog.Contracts.Events.Attribution? attribution, NotificationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return attribution?.Status switch
        {
            null or AttributionStatus.Pending => "– " + context.Text.Get("Subject.ActorPending"),
            AttributionStatus.Unknown or AttributionStatus.Ambiguous => "– " + context.Text.Get("Subject.ActorUnknown"),
            AttributionStatus.Resolved => context.Objects.ActorShort(attribution) is { } actor ? "– " + actor : string.Empty,
            _ => string.Empty,
        };
    }

    private static string Head(NotificationContext context, Severity severity) =>
        context.SubjectPrefix + "[" + context.Text.SeverityWord(severity) + "] ";

    /// <summary>head, object and actor joined by spaces; the object part is shortened first, the whole subject only
    /// when nothing else fits.</summary>
    private static string Compose(string head, string objectPart, string actorPart)
    {
        head = UntrustedText.ForHeader(head);
        objectPart = UntrustedText.ForHeader(objectPart);
        actorPart = UntrustedText.ForHeader(actorPart);
        var full = Join(head, objectPart, actorPart);
        if (full.Length <= MaxLength)
        {
            return full;
        }

        var room = MaxLength - Join(head, "x", actorPart).Length + 1;
        return room > 8 && objectPart.Length > 0
            ? Join(head, objectPart[..Math.Min(objectPart.Length, room - 1)] + Ellipsis, actorPart)
            : Cut(full);
    }

    private static string Join(params string[] parts) => string.Join(' ', parts.Where(p => p.Length > 0));

    private static string Cut(string subject) => subject.Length <= MaxLength ? subject : subject[..(MaxLength - 1)] + Ellipsis;
}
