using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Notifications;

namespace AdMng.Watchdog.Core.Formatting.AdaptiveCards;

/// <summary>
/// The digest summary card for Teams (sections 13.4, 14.4): header container (style <c>emphasis</c>) with the title,
/// the domain and the health line as TextRuns, a FactSet of counters, "Wymaga uwagi" and every section with up to
/// <see cref="MaxRowsPerSection"/> rows (time · object · change · actor, all TextRun), "… i N kolejnych" and
/// "Pokazano X z Y pozycji", an Open-URL action to the service when configured – within the 24 KiB budget
/// (<see cref="CardBudget"/>). Also the heartbeat that keeps Teams flows active: an empty period says "Brak zmian w okresie".
/// </summary>
public static class TeamsDigestCardFormatter
{
    /// <summary>Rows per section on the card (the e-mail shows <c>MaxRowsPerSection</c>).</summary>
    public const int MaxRowsPerSection = 10;

    /// <summary>The card message of <paramref name="model"/> for a channel with <paramref name="context"/>'s detail level.</summary>
    public static RenderedHttpMessage Format(DigestModel model, NotificationContext context, string? cardVersion = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);
        var text = context.Text;
        var title = DigestText.Title(model, context);
        var layout = new CardLayout
        {
            FallbackText = "AD Watchdog: " + EmailSubjectBuilder.Digest(model.Domain, DateOnly.FromDateTime(context.Time.ToLocal(model.PeriodEnd).DateTime),
                model.Counts.Total, model.Counts.Attention, context),
            ShownLine = (shown, total) => TeamsCardParts.Subtle(text.Format("Digest.Shown", shown, total)),
            AlwaysShowShownLine = !model.IsEmpty,
        };
        layout.Header.Add(new Container(
        [
            new TextBlock(text.Get("Digest.Title")) { Size = "large", Weight = "bolder" },
            RichTextBlock.Of(string.IsNullOrWhiteSpace(model.Domain) ? context.InstanceName : UntrustedText.Sanitize(model.Domain), weight: "bolder"),
            new TextBlock(DigestText.Period(model, context)) { IsSubtle = true, Spacing = "none" },
            new RichTextBlock([new TextRun(DigestText.HealthLine(model, context)) { IsSubtle = true }]) { Spacing = "small" },
        ]) { Style = "emphasis", Bleed = true });
        layout.Header.Add(model.IsEmpty
            ? new TextBlock(text.Get("Digest.Empty")) { Spacing = "medium" }
            : new FactSet([.. DigestText.Counts(model, context).Select(f => new Fact(f.Label, f.Value))]) { Spacing = "medium" });

        var total = 0;
        if (model.Attention.Count > 0)
        {
            var rows = model.Attention.Select(e => DigestRowFormatter.AttentionCells(e, context, model.PreviousSeverity(e.Id))).Select(c => (IReadOnlyList<string>)[c[0], c[1], c[2] + " · " + c[4], c[3]]);
            layout.Sections.Add(Section(text.Format("Digest.AttentionTitle", text.Number(model.Counts.Attention)), [], rows, model.Counts.Attention, context));
            total += model.Counts.Attention;
        }

        foreach (var section in model.Sections)
        {
            var rows = section.Rows.Select(r => DigestRowFormatter.Cells(r, context, model.PeriodEnd));
            layout.Sections.Add(Section(DigestText.SectionTitle(section, context), DigestText.SectionFacts(section, context), rows, section.Total, context));
            total += section.Total;
        }

        layout.TotalItems = total;
        if (context.ServiceLink is { } link)
        {
            layout.Actions.Add(new OpenUrlAction(text.Get("Card.OpenInWatchdog"), link));
        }

        return new RenderedHttpMessage
        {
            Body = CardBudget.Fit(layout, cardVersion),
            ContentType = "application/json; charset=utf-8",
            Title = UntrustedText.ForHeader(title),
        };
    }

    private static CardSection Section(string title, IReadOnlyList<AlertFact> facts, IEnumerable<IReadOnlyList<string>> rows, int total, NotificationContext context)
    {
        var section = new CardSection { MoreLine = n => TeamsCardParts.Subtle(DigestText.More(n, context)) };
        section.Heading.Add(TeamsCardParts.SectionTitle(title));
        if (facts.Count > 0)
        {
            section.Heading.Add(new FactSet([.. facts.Select(f => new Fact(f.Label, f.Value))]));
        }

        foreach (var cells in rows.Take(MaxRowsPerSection))
        {
            section.Rows.Add(new CardRow(TeamsCardParts.TextRow([.. cells.Select((c, i) => (c, Width(cells.Count, i)))], subtleFirst: true)));
        }

        section.Omitted = Math.Max(0, total - section.Rows.Count);
        return section;
    }

    private static int Width(int columns, int index) => columns switch
    {
        3 => index == 1 ? 4 : 3,
        _ => index switch { 0 => 2, 1 => 3, 2 => 3, _ => 2 },
    };
}
