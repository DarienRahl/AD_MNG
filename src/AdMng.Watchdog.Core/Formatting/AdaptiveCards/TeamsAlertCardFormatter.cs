using System.Text;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Formatting.AdaptiveCards;

/// <summary>
/// Teams alert cards (section 14.4): the single alert (header container in the severity style, the sentence, Kto/Skąd/
/// Kiedy/Gdzie rows, at most 6 change rows inline and the rest behind "Pokaż wszystkie zmiany", "Otwórz w AD Watchdog"
/// with <c>PublicBaseUrl</c>), the burst card for an aggregated bucket ("N zdarzeń: rule", digest layout), the
/// follow-up card ("Uzupełnienie: wykonawca ustalony") and the test card. Every AD-sourced string is a TextRun; the
/// message is cut to the 24 KiB budget.
/// </summary>
public static class TeamsAlertCardFormatter
{
    /// <summary>Change rows shown before the toggle.</summary>
    public const int InlineChanges = 6;

    private const string MoreChangesId = "watchdog-more-changes";

    /// <summary>A single alert card.</summary>
    public static RenderedHttpMessage Alert(WatchEvent watchEvent, RuleSettings? rule, NotificationContext context, string? cardVersion = null,
        bool followUpExpected = false)
    {
        var content = AlertContentBuilder.Build(watchEvent, rule, context, followUpExpected);
        var layout = AlertLayout(content, context, context.Text.Get("Card.ShowAllChanges"));
        return Render(layout, cardVersion, content.Title);
    }

    /// <summary>The burst card of several events of one bucket (digest layout).</summary>
    public static RenderedHttpMessage Burst(IReadOnlyList<WatchEvent> events, int moreCount, RuleSettings? rule, NotificationContext context,
        string? cardVersion = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(context);
        if (events.Count == 1 && moreCount == 0)
        {
            return Alert(events[0], rule, context, cardVersion);
        }

        var safe = events.Select(e => DetailLevelFilter.ForNotification(e, context.DetailLevel, context.MaxValueLength)).ToList();
        var text = context.Text;
        var total = events.Count + moreCount;
        var severity = events.Max(e => e.Severity);
        var name = EmailSubjectBuilder.AggregatedTitle(safe, rule, context);
        var title = text.Plural("Card.BurstTitle", total, name);
        var layout = new CardLayout
        {
            FallbackText = "AD Watchdog: " + title,
            TotalItems = total,
            ShownLine = (shown, all) => TeamsCardParts.Subtle(text.Format("Card.Shown", shown, all)),
        };
        layout.Header.Add(Header(severity, title, null, Subtitle(safe[0].Domain, rule, context, context.Time.Local(safe.Min(e => e.DetectedAt)))));
        var section = new CardSection { MoreLine = n => TeamsCardParts.Subtle(text.Format("Value.More", n)), Omitted = moreCount };
        section.Heading.Add(TeamsCardParts.SectionTitle(text.Get("Label.Events")));
        foreach (var e in safe)
        {
            section.Rows.Add(new CardRow(EventRow(e, context)));
        }

        layout.Sections.Add(section);
        AddServiceLink(layout, context);
        return Render(layout, cardVersion, title);
    }

    /// <summary>The follow-up card once the actors of <paramref name="events"/> are known.</summary>
    public static RenderedHttpMessage FollowUp(IReadOnlyList<WatchEvent> events, RuleSettings? rule, NotificationContext context, string? cardVersion = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(context);
        var text = context.Text;
        var safe = events.Select(e => DetailLevelFilter.ForNotification(e, context.DetailLevel, context.MaxValueLength)).ToList();
        var title = text.Get("Card.FollowUpTitle");
        var layout = new CardLayout { FallbackText = "AD Watchdog: " + title, TotalItems = safe.Count };
        layout.Header.Add(Header(safe.Count == 0 ? Severity.Info : safe.Max(e => e.Severity), title, null,
            Subtitle(safe.FirstOrDefault()?.Domain, rule, context, null)));
        var section = new CardSection();
        foreach (var e in safe)
        {
            var rows = new List<AdaptiveElement>
            {
                new RichTextBlock([new TextRun(text.KindTitle(e.Kind) + ": ") { IsSubtle = true }, new TextRun(AlertContentBuilder.ObjectName(e, context)) { Weight = "bolder" }])
                {
                    Spacing = "medium",
                    Separator = true,
                },
                TeamsCardParts.LabelValue(text.Get("Label.Who"), context.Objects.Actor(e.Attribution)),
            };
            if (context.Objects.Source(e.Attribution) is { } source)
            {
                rows.Add(TeamsCardParts.LabelValue(text.Get("Label.From"), source));
            }

            rows.Add(TeamsCardParts.LabelValue(text.Get("Label.When"), context.Time.Local(e.OriginatingTime ?? e.DetectedAt)));
            section.Rows.Add(new CardRow(new Container(rows)));
        }

        layout.Sections.Add(section);
        if (safe.Count == 1 && context.EventLink(safe[0].EventId) is { } link)
        {
            layout.Actions.Add(new OpenUrlAction(text.Get("Card.OpenInWatchdog"), link));
        }
        else
        {
            AddServiceLink(layout, context);
        }

        return Render(layout, cardVersion, title);
    }

    /// <summary>The test card ("Wiadomość testowa AD Watchdog").</summary>
    public static RenderedHttpMessage Test(NotificationContext context, DateTimeOffset sentAt, string? cardVersion = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var text = context.Text;
        var title = text.Get("Card.TestTitle");
        var layout = new CardLayout { FallbackText = "AD Watchdog: " + title };
        layout.Header.Add(Header(Severity.Info, title, context.InstanceName, context.Time.Local(sentAt)));
        layout.Header.Add(new TextBlock(text.Get("Card.TestBody")) { Spacing = "medium" });
        AddServiceLink(layout, context);
        return Render(layout, cardVersion, title);
    }

    /// <summary>The layout of one alert (also used by the event page tests).</summary>
    public static CardLayout AlertLayout(AlertContent content, NotificationContext context, string toggleTitle)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(context);
        var text = context.Text;
        var e = content.Event;
        var layout = new CardLayout
        {
            FallbackText = Fallback(content),
            TotalItems = content.Changes.Count,
            ToggleTitle = toggleTitle,
        };
        layout.Header.Add(Header(content.Severity, content.Title, content.ObjectName,
            Subtitle(e.Domain, null, context, content.When, content.Category)));
        layout.Header.Add(new RichTextBlock([new TextRun(content.Via is null ? content.Sentence : content.Sentence + " " + content.Via)]) { Spacing = "medium" });
        var first = true;
        var inHeader = new[] { text.Get("Label.What"), text.Get("Label.Object") };
        foreach (var fact in content.Facts.Where(f => !inHeader.Contains(f.Label, StringComparer.Ordinal)))
        {
            layout.Header.Add(TeamsCardParts.LabelValue(fact.Label, fact.Value, first ? "medium" : "small"));
            first = false;
        }

        if (content.Remedy is not null)
        {
            layout.Header.Add(new RichTextBlock([new TextRun(content.Remedy) { IsSubtle = true, Italic = true }]) { Spacing = "small" });
        }

        if (content.Changes.Count > 0)
        {
            var section = new CardSection
            {
                HiddenId = MoreChangesId,
                MoreLine = n => TeamsCardParts.Subtle(text.Format("Value.More", n)),
            };
            section.Heading.Add(TeamsCardParts.SectionTitle(text.Format("Card.Changes", content.Changes.Count)));
            for (var i = 0; i < content.Changes.Count; i++)
            {
                var row = new CardRow(ChangeRow(content.Changes[i]));
                (i < InlineChanges ? section.Rows : section.HiddenRows).Add(row);
            }

            layout.Sections.Add(section);
        }

        if (content.EventLink is { } link)
        {
            layout.Actions.Add(new OpenUrlAction(text.Get("Card.OpenInWatchdog"), link));
        }

        return layout;
    }

    /// <summary>"AD Watchdog: &lt;title&gt; &lt;object&gt; – &lt;actor&gt;".</summary>
    public static string Fallback(AlertContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var builder = new StringBuilder("AD Watchdog: ").Append(content.Title).Append(' ').Append(content.ObjectName);
        if (content.ActorShort is { } actor)
        {
            builder.Append(" – ").Append(actor);
        }

        return UntrustedText.ForHeader(builder.ToString());
    }

    /// <summary>The header container: title (TextBlock), object (TextRun, bold), subtitle (TextRun, subtle).</summary>
    internal static Container Header(Severity severity, string title, string? objectName, string subtitle)
    {
        var items = new List<AdaptiveElement> { new TextBlock(title) { Size = "large", Weight = "bolder" } };
        if (!string.IsNullOrEmpty(objectName))
        {
            items.Add(RichTextBlock.Of(objectName, weight: "bolder"));
        }

        items.Add(new RichTextBlock([new TextRun(subtitle) { IsSubtle = true }]) { Spacing = "none" });
        return new Container(items) { Style = TeamsCardParts.Style(severity), Bleed = true };
    }

    /// <summary>"contoso.com · reguła: Zmiany parametrów kont · 14.10.2026 09:12:03 (UTC+02:00)".</summary>
    internal static string Subtitle(string? domain, RuleSettings? rule, NotificationContext context, string? when, string? category = null)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(domain))
        {
            parts.Add(domain);
        }

        var ruleName = category ?? context.Objects.RuleName(rule);
        if (!string.IsNullOrEmpty(ruleName))
        {
            parts.Add(context.Text.Format("Card.Rule", ruleName));
        }

        if (!string.IsNullOrEmpty(when))
        {
            parts.Add(when);
        }

        return parts.Count == 0 ? context.InstanceName : string.Join(" · ", parts);
    }

    /// <summary>A Czas · Obiekt · Zmiana · Kto row of one sanitized event.</summary>
    internal static ColumnSet EventRow(WatchEvent e, NotificationContext context)
    {
        var text = context.Text;
        var change = text.KindTitle(e.Kind);
        if (e.Member is not null)
        {
            change += ": " + context.Objects.Name(e.Member);
        }

        var who = context.Objects.ActorShort(e.Attribution)
            ?? (e.Attribution.Status == AttributionStatus.NotApplicable ? string.Empty : text.Get(e.Attribution.Status == AttributionStatus.Pending ? "Value.ActorPending" : "Value.Unknown"));
        return TeamsCardParts.TextRow(
            [(context.Time.LocalShort(e.OriginatingTime ?? e.DetectedAt), 2), (AlertContentBuilder.ObjectName(e, context), 3), (change, 3), (who, 2)],
            subtleFirst: true);
    }

    private static ColumnSet ChangeRow(AlertChangeRow change)
    {
        var columns = new List<Column> { new([RichTextBlock.Of(change.Attribute)], 2) };
        if (change.Before is null)
        {
            columns.Add(new Column([RichTextBlock.Of(change.After, isSubtle: true)], 6));
        }
        else
        {
            columns.Add(new Column([RichTextBlock.Of(change.Before, isSubtle: true, strikethrough: change.Before != "—")], 3));
            columns.Add(new Column([RichTextBlock.Of(change.After)], 3));
        }

        return new ColumnSet(columns) { Spacing = "small" };
    }

    private static void AddServiceLink(CardLayout layout, NotificationContext context)
    {
        if (context.ServiceLink is { } link)
        {
            layout.Actions.Add(new OpenUrlAction(context.Text.Get("Card.OpenInWatchdog"), link));
        }
    }

    private static RenderedHttpMessage Render(CardLayout layout, string? cardVersion, string title) => new()
    {
        Body = CardBudget.Fit(layout, cardVersion),
        ContentType = "application/json; charset=utf-8",
        Title = UntrustedText.ForHeader(title),
    };
}
