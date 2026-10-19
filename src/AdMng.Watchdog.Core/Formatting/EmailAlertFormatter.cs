using System.Globalization;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Formatting;

/// <summary>
/// Alert e-mails (sections 14.2–14.3): the single alert (banner, sentence, facts, changes, links, footer), the
/// aggregated alert (summary line and a Czas · Obiekt · Zmiana · Kto · Skąd table, "+N kolejnych"), the follow-up when
/// the actor was attributed later (<c>Re:</c> subject, <c>In-Reply-To</c>) and the test message – HTML (encoded by
/// <see cref="HtmlWriter"/>) plus an aligned text alternative, and the <c>X-AdMng-*</c> / auto-reply headers.
/// </summary>
public static class EmailAlertFormatter
{
    /// <summary>At most this many event GUIDs go into <c>X-AdMng-Event-Id</c>.</summary>
    public const int MaxEventIdsInHeader = 20;

    /// <summary>A single alert.</summary>
    public static RenderedEmail Alert(WatchEvent watchEvent, RuleSettings? rule, NotificationContext context, bool followUpExpected = false)
    {
        var content = AlertContentBuilder.Build(watchEvent, rule, context, followUpExpected);
        var subject = EmailSubjectBuilder.Single(content, context);
        return Render(subject, content.Severity, rule, [watchEvent], context, html => WriteAlert(html, content, context, null),
            text => WriteAlert(text, content, context, null));
    }

    /// <summary>An aggregated alert of <paramref name="events"/> (already capped at <c>MaxItemsPerMessage</c>);
    /// <paramref name="moreCount"/> further events are announced as "+N kolejnych".</summary>
    public static RenderedEmail Aggregated(IReadOnlyList<WatchEvent> events, int moreCount, RuleSettings? rule, NotificationContext context)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(context);
        if (events.Count == 1 && moreCount == 0)
        {
            return Alert(events[0], rule, context);
        }

        var safe = events.Select(e => DetailLevelFilter.ForNotification(e, context.DetailLevel, context.MaxValueLength)).ToList();
        var severity = events.Max(e => e.Severity);
        var subject = EmailSubjectBuilder.Aggregated(safe, events.Count + moreCount, severity, rule, context);
        var summary = Summary(safe, events.Count + moreCount, rule, context);
        var rows = safe.Select(e => ItemRow(e, context)).ToList();
        return Render(subject, severity, rule, events, context,
            html => WriteAggregated(html, severity, summary, ItemHeaders(context), rows, moreCount, rule, safe, context, null),
            text => WriteAggregated(text, summary, ItemHeaders(context), rows, moreCount, rule, safe, context, null));
    }

    /// <summary>The follow-up to message <paramref name="originalMessageId"/> once the actors of <paramref name="events"/>
    /// are known or their severity was raised (an escalation, 13.2 #7): subject "Re: <paramref name="originalSubject"/>",
    /// same thread. Every event is shown with its final severity – a single event in the banner and, when it changed, as
    /// "Priorytet: Średni → Wysoki"; several in a Priorytet column – and the banner says what is new
    /// (<see cref="FollowUpChange"/>).</summary>
    public static RenderedEmail FollowUp(IReadOnlyList<WatchEvent> events, string originalSubject, string originalMessageId, RuleSettings? rule,
        NotificationContext context, IReadOnlyList<FollowUpChange>? changes = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalMessageId);
        ArgumentNullException.ThrowIfNull(context);
        if (events.Count == 0)
        {
            throw new ArgumentException("A follow-up needs at least one event.", nameof(events));
        }

        var subject = EmailSubjectBuilder.FollowUp(originalSubject);
        var banner = FollowUpChange.Title(events, changes, context, "Email.FollowUpBanner");
        var severity = events.Max(e => e.Severity);
        RenderedEmail email;
        if (events.Count == 1)
        {
            var content = AlertContentBuilder.Build(events[0], rule, context);
            if (FollowUpChange.SeverityChange(events[0], changes, context) is { } change)
            {
                // Right after "Co": the news of an escalation.
                content = content with { Facts = [.. content.Facts.Take(1), new AlertFact(context.Text.Get("Label.Severity"), change), .. content.Facts.Skip(1)] };
            }

            email = Render(subject, severity, rule, events, context, html => WriteAlert(html, content, context, banner),
                text => WriteAlert(text, content, context, banner));
        }
        else
        {
            var safe = events.Select(e => DetailLevelFilter.ForNotification(e, context.DetailLevel, context.MaxValueLength)).ToList();
            var summary = Summary(safe, safe.Count, rule, context);
            var headers = (IReadOnlyList<string>)[.. ItemHeaders(context), context.Text.Get("Label.Severity")];
            var rows = events.Select((e, i) => (IReadOnlyList<string?>)[.. ItemRow(safe[i], context), FollowUpChange.SeverityText(e, changes, context)]).ToList();
            email = Render(subject, severity, rule, events, context,
                html => WriteAggregated(html, severity, summary, headers, rows, 0, rule, safe, context, banner),
                text => WriteAggregated(text, summary, headers, rows, 0, rule, safe, context, banner));
        }

        return email with { InReplyTo = originalMessageId };
    }

    /// <summary>The test message (<c>POST /api/v1/test/email</c>).</summary>
    public static RenderedEmail Test(NotificationContext context, DateTimeOffset sentAt)
    {
        ArgumentNullException.ThrowIfNull(context);
        var subject = EmailSubjectBuilder.Test(context);
        var body = context.Text.Format("Email.TestBody", context.InstanceName, context.Time.Local(sentAt));
        var html = new HtmlWriter();
        EmailLayout.Begin(html, context.Text.Culture.TwoLetterISOLanguageName, subject);
        EmailLayout.Banner(html, Severity.Info, context.Text.Get("Subject.Test"));
        EmailLayout.Paragraph(html, body);
        EmailLayout.Footer(html, context.Text.Format("Email.FooterTest", context.ServiceName));
        EmailLayout.End(html);
        var text = new PlainTextWriter().Line(context.Text.Get("Subject.Test")).Line().Paragraph(body)
            .Line("── " + context.Text.Format("Email.FooterTest", context.ServiceName));
        return new RenderedEmail
        {
            Subject = subject,
            HtmlBody = html.ToString(),
            TextBody = text.ToString(),
            Headers = Headers(Severity.Info, null, []),
        };
    }

    /// <summary>The headers every notification e-mail carries (Message-ID, In-Reply-To and References are added by the
    /// service's MIME factory).</summary>
    public static Dictionary<string, string> Headers(Severity severity, string? ruleId, IEnumerable<Guid> eventIds)
    {
        ArgumentNullException.ThrowIfNull(eventIds);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Auto-Submitted"] = "auto-generated",
            ["X-Auto-Response-Suppress"] = "All",
            ["X-AdMng-Severity"] = severity.ToString(),
        };
        if (!string.IsNullOrWhiteSpace(ruleId))
        {
            headers["X-AdMng-Rule"] = UntrustedText.ForHeader(ruleId);
        }

        var ids = eventIds.Take(MaxEventIdsInHeader).Select(g => g.ToString("D", CultureInfo.InvariantCulture)).ToList();
        if (ids.Count > 0)
        {
            headers["X-AdMng-Event-Id"] = string.Join(", ", ids);
        }

        return headers;
    }

    private static RenderedEmail Render(string subject, Severity severity, RuleSettings? rule, IReadOnlyList<WatchEvent> events,
        NotificationContext context, Action<HtmlWriter> writeHtml, Action<PlainTextWriter> writeText)
    {
        var html = new HtmlWriter();
        EmailLayout.Begin(html, context.Text.Culture.TwoLetterISOLanguageName, subject);
        writeHtml(html);
        EmailLayout.End(html);
        var text = new PlainTextWriter();
        writeText(text);
        return new RenderedEmail
        {
            Subject = subject,
            HtmlBody = html.ToString(),
            TextBody = text.ToString(),
            Headers = Headers(severity, rule?.Id, events.Select(e => e.EventId)),
        };
    }

    /// <summary>The HTML rows of one alert (also the body of the event page).</summary>
    internal static void WriteAlert(HtmlWriter html, AlertContent content, NotificationContext context, string? banner)
    {
        var text = context.Text;
        EmailLayout.Banner(html, content.Severity, (banner is null ? string.Empty : banner + " · ") + content.SeverityWord + " · " + content.Category);
        EmailLayout.Paragraph(html, content.Via is null ? content.Sentence : content.Sentence + "\n" + content.Via);
        EmailLayout.Facts(html, content.Facts);
        if (content.Remedy is not null)
        {
            EmailLayout.Paragraph(html, content.Remedy, "padding:0 16px 8px 16px;font-size:13px;color:#a4262c;");
        }

        if (content.Changes.Count > 0)
        {
            EmailLayout.Table(html, [text.Get("Label.Changes"), text.Get("Label.Before"), text.Get("Label.After")],
                content.Changes.Select(c => (IReadOnlyList<string?>)[c.Attribute, c.Before, c.After]));
        }

        EmailLayout.Links(html, text.Get("Email.OpenInBrowser"), content.EventLink, content.AdMngHint);
        EmailLayout.Footer(html, content.Footer);
    }

    private static void WriteAlert(PlainTextWriter writer, AlertContent content, NotificationContext context, string? banner)
    {
        var text = context.Text;
        writer.Line((banner is null ? string.Empty : banner + " · ") + content.SeverityWord + " · " + content.Category).Line()
            .Paragraph(content.Via is null ? content.Sentence : content.Sentence + "\n" + content.Via)
            .Facts(content.Facts.Select(f => (f.Label, f.Value)));
        if (content.Remedy is not null)
        {
            writer.Line().Line(content.Remedy);
        }

        if (content.Changes.Count > 0)
        {
            writer.Line().Line(text.Get("Label.Changes") + ":").Facts(content.Changes.Select(c =>
                (c.Attribute, c.Before is null ? c.After : c.Before + " → " + c.After)));
        }

        writer.Line();
        if (content.EventLink is not null)
        {
            writer.Line(text.Get("Email.OpenInBrowser") + ": " + content.EventLink);
        }

        writer.Line(content.AdMngHint).Line().Line("── " + content.Footer);
    }

    private static void WriteAggregated(HtmlWriter html, Severity severity, string summary, IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string?>> rows, int moreCount, RuleSettings? rule, IReadOnlyList<WatchEvent> events, NotificationContext context,
        string? banner)
    {
        var text = context.Text;
        var category = EmailSubjectBuilder.AggregatedTitle(events, rule, context);
        EmailLayout.Banner(html, severity, (banner is null ? string.Empty : banner + " · ") + text.SeverityWord(severity) + " · " + category);
        EmailLayout.Paragraph(html, summary);
        EmailLayout.Table(html, headers, rows);
        if (moreCount > 0)
        {
            EmailLayout.Paragraph(html, text.Format("Value.More", moreCount), "padding:0 16px 8px 16px;font-size:13px;color:#605e5c;");
        }

        EmailLayout.Links(html, text.Get("Email.OpenInBrowser"), context.ServiceLink, text.Get("Email.InAdMngList"));
        EmailLayout.Footer(html, Footer(rule, events, context));
    }

    private static void WriteAggregated(PlainTextWriter writer, string summary, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string?>> rows,
        int moreCount, RuleSettings? rule, IReadOnlyList<WatchEvent> events, NotificationContext context, string? banner)
    {
        var text = context.Text;
        var severity = events.Max(e => e.Severity);
        writer.Line((banner is null ? string.Empty : banner + " · ") + text.SeverityWord(severity) + " · " + EmailSubjectBuilder.AggregatedTitle(events, rule, context))
            .Line().Paragraph(summary);
        foreach (var row in rows)
        {
            writer.Facts(headers.Select((h, i) => (h, row[i] ?? string.Empty)).Where(f => f.Item2.Length > 0)).Line();
        }

        if (moreCount > 0)
        {
            writer.Line(text.Format("Value.More", moreCount)).Line();
        }

        if (context.ServiceLink is { } link)
        {
            writer.Line(text.Get("Email.OpenInBrowser") + ": " + link);
        }

        writer.Line(text.Get("Email.InAdMngList")).Line().Line("── " + Footer(rule, events, context));
    }

    private static string Summary(IReadOnlyList<WatchEvent> events, int total, RuleSettings? rule, NotificationContext context)
    {
        var text = context.Text;
        var summary = EmailSubjectBuilder.AggregatedTitle(events, rule, context) + ": " + text.Number(total);
        if (ObjectDisplay.CommonParentRdn(events) is { } parent)
        {
            summary += " (" + parent + ")";
        }

        var actors = events.Select(e => context.Objects.ActorShort(e.Attribution)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (actors.Count == 1 && actors[0] is { } actor)
        {
            summary += " · " + text.Format("Email.ActorOfAll", actor);
        }

        return summary;
    }

    /// <summary>Czas · Obiekt · Zmiana · Kto · Skąd.</summary>
    internal static IReadOnlyList<string> ItemHeaders(NotificationContext context) =>
    [
        context.Text.Get("Label.Time"), context.Text.Get("Label.Object"), context.Text.Get("Label.Change"),
        context.Text.Get("Label.Who"), context.Text.Get("Label.From"),
    ];

    /// <summary>Czas · Obiekt · Zmiana · Kto · Skąd of one sanitized event.</summary>
    internal static IReadOnlyList<string?> ItemRow(WatchEvent e, NotificationContext context)
    {
        var objects = context.Objects;
        var change = context.Text.KindTitle(e.Kind);
        if (e.Changes.Count > 0 && e.Kind is WatchEventKind.AttributesChanged or WatchEventKind.AccountFlagsChanged)
        {
            change += ": " + string.Join(", ", e.Changes.Select(c => context.Attributes.Name(c.Attribute)));
        }
        else if (e.Member is not null)
        {
            change += ": " + objects.Name(e.Member);
        }

        var who = e.Kind == WatchEventKind.AccountLockedOut || e.Attribution.Status == AttributionStatus.NotApplicable
            ? string.Empty
            : objects.ActorShort(e.Attribution) ?? context.Text.Get(e.Attribution.Status == AttributionStatus.Pending ? "Value.ActorPending" : "Value.Unknown");
        var from = e.Kind == WatchEventKind.AccountLockedOut ? objects.LockoutSources(e.Lockout) : objects.Source(e.Attribution);
        return [context.Time.LocalShort(e.OriginatingTime ?? e.DetectedAt), objects.Name(e.Object), change, who, from ?? string.Empty];
    }

    private static string Footer(RuleSettings? rule, IReadOnlyList<WatchEvent> events, NotificationContext context)
    {
        var ids = events.Count == 0
            ? string.Empty
            : events.Min(e => e.Id).ToString(CultureInfo.InvariantCulture) + "–" + events.Max(e => e.Id).ToString(CultureInfo.InvariantCulture);
        return rule is null
            ? context.Text.Format("Email.FooterNoRule", ids, context.ServiceName)
            : context.Text.Format("Email.Footer", rule.Id, ids, context.ServiceName);
    }
}
