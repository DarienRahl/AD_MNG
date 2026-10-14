using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Notifications;

namespace AdMng.Watchdog.Core.Formatting;

/// <summary>
/// The digest e-mail (sections 13.4, 14.2–14.3): subject "[AD Watchdog] Raport dzienny contoso.com – 14.10.2026: 71
/// zdarzeń (2 wymagają uwagi)", a banner, the period and health line, the counters, "Wymaga uwagi" with the
/// acknowledgement state and one table per non-empty section ("… i N kolejnych – pełna lista w zakładce Watchdog"). An
/// empty period renders "Brak zmian w okresie." (heartbeat). HTML via <see cref="HtmlWriter"/> plus the aligned text
/// alternative; the auto-reply and <c>X-AdMng-*</c> headers of <see cref="EmailAlertFormatter.Headers"/>.
/// </summary>
public static class EmailDigestFormatter
{
    /// <summary>The digest e-mail of <paramref name="model"/> for a recipient group with <paramref name="context"/>'s detail level.</summary>
    public static RenderedEmail Format(DigestModel model, NotificationContext context)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);
        var text = context.Text;
        var subject = EmailSubjectBuilder.Digest(model.Domain, DateOnly.FromDateTime(context.Time.ToLocal(model.PeriodEnd).DateTime), model.Counts.Total,
            model.Counts.Attention, context);
        var severity = model.Attention.Count == 0 ? Severity.Info : model.Attention.Max(e => e.Severity);
        var banner = DigestText.Title(model, context);
        var tables = Tables(model, context);

        var html = new HtmlWriter();
        EmailLayout.Begin(html, text.Culture.TwoLetterISOLanguageName, subject);
        EmailLayout.Banner(html, severity, banner);
        EmailLayout.Paragraph(html, DigestText.Period(model, context));
        EmailLayout.Paragraph(html, DigestText.HealthLine(model, context), "padding:0 16px 8px 16px;font-size:13px;color:#605e5c;");
        if (model.IsEmpty)
        {
            EmailLayout.Paragraph(html, text.Get("Digest.Empty"));
        }
        else
        {
            EmailLayout.Facts(html, DigestText.Counts(model, context));
        }

        foreach (var table in tables)
        {
            EmailLayout.Heading(html, table.Title);
            if (table.Facts.Count > 0)
            {
                EmailLayout.Facts(html, table.Facts);
            }

            if (table.Rows.Count > 0)
            {
                EmailLayout.Table(html, table.Headers, table.Rows);
            }

            if (table.More > 0)
            {
                EmailLayout.Paragraph(html, DigestText.More(table.More, context), "padding:0 16px 8px 16px;font-size:13px;color:#605e5c;");
            }
        }

        EmailLayout.Links(html, text.Get("Email.OpenInBrowser"), context.ServiceLink, text.Get("Email.InAdMngList"));
        EmailLayout.Footer(html, text.Format("Digest.Footer", context.ServiceName));
        EmailLayout.End(html);

        var plain = new PlainTextWriter().Line(banner).Line().Paragraph(DigestText.Period(model, context)).Paragraph(DigestText.HealthLine(model, context));
        if (model.IsEmpty)
        {
            plain.Paragraph(text.Get("Digest.Empty"));
        }
        else
        {
            plain.Facts(DigestText.Counts(model, context).Select(f => (f.Label, f.Value))).Line();
        }

        foreach (var table in tables)
        {
            plain.Line("■ " + table.Title).Line();
            if (table.Facts.Count > 0)
            {
                plain.Facts(table.Facts.Select(f => (f.Label, f.Value))).Line();
            }

            foreach (var row in table.Rows)
            {
                plain.Facts(table.Headers.Select((h, i) => (h, i < row.Count ? row[i] ?? string.Empty : string.Empty)).Where(f => f.Item2.Length > 0)).Line();
            }

            if (table.More > 0)
            {
                plain.Line(DigestText.More(table.More, context)).Line();
            }
        }

        if (context.ServiceLink is { } link)
        {
            plain.Line(text.Get("Email.OpenInBrowser") + ": " + link);
        }

        plain.Line(text.Get("Email.InAdMngList")).Line().Line("── " + text.Format("Digest.Footer", context.ServiceName));
        return new RenderedEmail
        {
            Subject = subject,
            HtmlBody = html.ToString(),
            TextBody = plain.ToString(),
            Headers = EmailAlertFormatter.Headers(severity, null, model.EventIds),
        };
    }

    private static List<DigestTable> Tables(DigestModel model, NotificationContext context)
    {
        var tables = new List<DigestTable>();
        if (model.Attention.Count > 0)
        {
            tables.Add(new DigestTable(
                context.Text.Format("Digest.AttentionTitle", context.Text.Number(model.Counts.Attention)),
                [],
                DigestRowFormatter.AttentionHeaders(context),
                [.. model.Attention.Select(e => DigestRowFormatter.AttentionCells(e, context))],
                model.Counts.Attention - model.Attention.Count));
        }

        foreach (var section in model.Sections)
        {
            tables.Add(new DigestTable(
                DigestText.SectionTitle(section, context),
                DigestText.SectionFacts(section, context),
                DigestRowFormatter.Headers(section.Kind, context),
                [.. section.Rows.Select(r => DigestRowFormatter.Cells(r, context, model.PeriodEnd))],
                section.MoreCount));
        }

        return tables;
    }

    private sealed record DigestTable(string Title, IReadOnlyList<AlertFact> Facts, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string?>> Rows, int More);
}
