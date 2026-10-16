using AdMng.Watchdog.Core.Notifications;
using AdMng.Watchdog.Core.Notifications.Digest;

namespace AdMng.Watchdog.Core.Formatting;

/// <summary>The shared texts of the digest formatters: title, period, health line, counters and section headings
/// (section 13.4). Directory-sourced parts (domain, DC host name) are sanitized.</summary>
public static class DigestText
{
    /// <summary>"Raport dzienny – contoso.com".</summary>
    public static string Title(DigestModel model, NotificationContext context)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);
        var title = context.Text.Get("Digest.Title");
        return string.IsNullOrWhiteSpace(model.Domain) ? title : title + " – " + UntrustedText.Sanitize(model.Domain);
    }

    /// <summary>"Okres: 13.10.2026 07:30:00 (UTC+02:00) – 14.10.2026 07:30:00 (UTC+02:00) · obejmuje dni wolne …".</summary>
    public static string Period(DigestModel model, NotificationContext context)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);
        var text = context.Text;
        var period = text.Format("Digest.Period", context.Time.Local(model.PeriodStart), context.Time.Local(model.PeriodEnd));
        if (model.CoversNonBusinessDays)
        {
            period += " · " + text.Get("Digest.CoversNonBusinessDays");
        }

        if (model.IsWeekly)
        {
            period += " · " + text.Get("Digest.Weekly");
        }

        return period;
    }

    /// <summary>"Kontroler domeny dc01.contoso.com (OK) · ostatni odczyt … · pokrycie atrybucji 97% · …".</summary>
    public static string HealthLine(DigestModel model, NotificationContext context)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);
        var text = context.Text;
        var health = model.Health;
        var parts = new List<string>
        {
            string.IsNullOrWhiteSpace(health.DcHostName)
                ? text.Get("Digest.Health.NoDirectory")
                : text.Format("Digest.Health.Directory", UntrustedText.Sanitize(health.DcHostName), text.Get("Digest.Health.State." + health.DirectoryState)),
        };
        if (health.LastPollAt is { } lastPoll)
        {
            parts.Add(text.Format("Digest.Health.LastPoll", context.Time.Local(lastPoll)));
        }

        if (health.AttributionCoverage is { } coverage)
        {
            parts.Add(text.Format("Digest.Health.Coverage", coverage.ToString("P0", text.Culture)));
        }

        if (health.EventLogGaps > 0)
        {
            parts.Add(text.Format("Digest.Health.LogGaps", health.EventLogGaps));
        }

        if (health.BrokenChannels.Count > 0)
        {
            parts.Add(text.Format("Digest.Health.BrokenChannels", string.Join(", ", health.BrokenChannels.Select(c => UntrustedText.Sanitize(c)))));
        }

        if (health.RolloutUntil is { } until)
        {
            parts.Add(text.Format("Digest.Health.Rollout", context.Time.Local(until)));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>The counters worth showing: the total always, the others when not zero.</summary>
    public static IReadOnlyList<AlertFact> Counts(DigestModel model, NotificationContext context)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);
        var facts = new List<AlertFact>();
        foreach (var (name, value) in model.Counts.ToDictionary())
        {
            if (value != 0 || name == "total")
            {
                facts.Add(new AlertFact(context.Text.Get("Digest.Count." + char.ToUpperInvariant(name[0]) + name[1..]), context.Text.Number(value)));
            }
        }

        return facts;
    }

    /// <summary>"Nowe konta (3)".</summary>
    public static string SectionTitle(DigestSection section, NotificationContext context)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(context);
        var title = context.Text.Get("Digest.Section." + section.Kind);
        return section.Rows.Count == 0 && section.Total == 0 ? title : context.Text.Format("Digest.SectionTitle", title, context.Text.Number(section.Total));
    }

    /// <summary>The counters of a section ("brak danych" when unknown).</summary>
    public static IReadOnlyList<AlertFact> SectionFacts(DigestSection section, NotificationContext context)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(context);
        return
        [
            .. section.Facts.Select(f => new AlertFact(
                context.Text.Get("Digest.Fact." + f.Key),
                f.Value is { } value ? value.ToString("N0", context.Text.Culture) : context.Text.Get("Digest.Unknown"))),
        ];
    }

    /// <summary>"… i 12 kolejnych – pełna lista w zakładce Watchdog".</summary>
    public static string More(int count, NotificationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Text.Format("Digest.More", context.Text.Number(count));
    }
}
