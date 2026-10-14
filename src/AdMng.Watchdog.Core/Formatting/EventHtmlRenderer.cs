using System.Globalization;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Formatting;

/// <summary>
/// <see cref="IEventHtmlRenderer"/>: the read-only HTML event page behind the links in notifications (section 14.6) – a
/// complete document (<c>&lt;meta charset="utf-8"&gt;</c>) with the single-alert e-mail body, the related events of the
/// same change set, the Security-log evidence and the deliveries; language from the configuration. No scripts, no
/// external resources (the service adds <c>Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'</c>).
/// </summary>
/// <remarks><see cref="Render(EventDetails)"/> shows what it is given (Full); for non-Admin callers the service passes the
/// details through <see cref="DetailLevelFilter.Apply(EventDetails, DetailLevel, int)"/> first or calls
/// <see cref="Render(EventDetails, DetailLevel)"/> with <c>Authorization.ViewerDetailLevel</c>.</remarks>
public sealed class EventHtmlRenderer : IEventHtmlRenderer
{
    private readonly IWatchdogConfigProvider _configProvider;

    public EventHtmlRenderer(IWatchdogConfigProvider configProvider)
    {
        ArgumentNullException.ThrowIfNull(configProvider);
        _configProvider = configProvider;
    }

    /// <inheritdoc/>
    public string Render(EventDetails details) => Render(details, DetailLevel.Full);

    /// <summary>The page at <paramref name="level"/>.</summary>
    public string Render(EventDetails details, DetailLevel level)
    {
        ArgumentNullException.ThrowIfNull(details);
        var context = NotificationContext.Create(_configProvider.Current.Configuration, level);
        var text = context.Text;
        var configuration = context.Configuration;
        var rule = configuration.Rules?.FirstOrDefault(r => r is not null && details.Event.MatchedRules.Contains(r.Id, StringComparer.OrdinalIgnoreCase));
        var content = AlertContentBuilder.Build(details.Event, rule, context);
        var title = EmailSubjectBuilder.Single(content, context);

        var html = new HtmlWriter();
        EmailLayout.Begin(html, text.Culture.TwoLetterISOLanguageName, title);
        EmailAlertFormatter.WriteAlert(html, content with { EventLink = null }, context, null);

        var related = details.RelatedEvents.Where(e => e.EventId != details.Event.EventId).ToList();
        if (related.Count > 0)
        {
            EmailLayout.Heading(html, text.Get("Page.RelatedEvents"));
            EmailLayout.Table(html, EmailAlertFormatter.ItemHeaders(context),
                related.Select(e => EmailAlertFormatter.ItemRow(DetailLevelFilter.ForNotification(e, level, context.MaxValueLength), context)));
        }

        var evidence = content.Event.Attribution.Evidence;
        if (evidence.Count > 0)
        {
            EmailLayout.Heading(html, text.Get("Page.Evidence"));
            EmailLayout.Table(html,
                [text.Get("Page.Dc"), text.Get("Page.Log"), text.Get("Page.EventCode"), text.Get("Page.RecordId"), text.Get("Label.Time")],
                evidence.Select(e => (IReadOnlyList<string?>)
                [
                    e.Dc, e.Log, e.EventCode.ToString(CultureInfo.InvariantCulture), e.RecordId.ToString(CultureInfo.InvariantCulture),
                    context.Time.Local(e.TimeCreated),
                ]));
        }

        if (details.Deliveries.Count > 0)
        {
            EmailLayout.Heading(html, text.Get("Page.Deliveries"));
            EmailLayout.Table(html,
                [text.Get("Page.Channel"), text.Get("Page.DeliveryKind"), text.Get("Page.Status"), text.Get("Page.Created"), text.Get("Page.Sent"), text.Get("Page.Error")],
                details.Deliveries.Select(d => (IReadOnlyList<string?>)
                [
                    UntrustedText.Sanitize(d.ChannelName ?? d.ChannelId), text.Get("DeliveryKind." + d.Kind), text.Get("OutboxStatus." + d.Status),
                    context.Time.Local(d.CreatedAt), d.SentAt is { } sent ? context.Time.Local(sent) : "—", UntrustedText.Sanitize(d.LastErrorCode),
                ]));
        }

        if (content.Event.AcknowledgedAt is { } acknowledgedAt)
        {
            EmailLayout.Paragraph(html, text.Format("Page.Acknowledged", context.Time.Local(acknowledgedAt), content.Event.AcknowledgedBy ?? "?")
                + (string.IsNullOrEmpty(content.Event.AcknowledgeComment) ? string.Empty : "\n" + content.Event.AcknowledgeComment));
        }

        EmailLayout.End(html);
        return html.ToString();
    }
}
