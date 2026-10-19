using System.Globalization;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Notifications.Digest;

namespace AdMng.Watchdog.Core.Formatting;

/// <summary>
/// The table cells of digest rows (section 13.4) for one recipient: every event is masked for the context's detail
/// level and sanitized (<see cref="DetailLevelFilter.ForNotification(WatchEvent, DetailLevel, int)"/>)
/// before anything is read from it; object references outside events are sanitized too. Event rows: Czas · Obiekt ·
/// Zmiana · Kto; lockouts: Czas · Konto · Blokady · Źródła; expiries: Wygasa · Obiekt · Co; inactive accounts: Konto ·
/// Ostatnie logowanie · OU; exceptions: Reguła · Wyjątek · Wygasa · Dodano przez.
/// </summary>
public static class DigestRowFormatter
{
    /// <summary>Changes listed per event before "…".</summary>
    public const int MaxChangesPerRow = 3;

    /// <summary>The column headers of <paramref name="kind"/>.</summary>
    public static IReadOnlyList<string> Headers(DigestSectionKind kind, NotificationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var text = context.Text;
        return kind switch
        {
            DigestSectionKind.Lockouts =>
                [text.Get("Label.Time"), text.Get("Digest.Column.Account"), text.Get("Digest.Column.Lockouts"), text.Get("Digest.Column.Sources")],
            DigestSectionKind.UpcomingExpiry => [text.Get("Digest.Column.Expires"), text.Get("Label.Object"), text.Get("Label.What")],
            DigestSectionKind.InactiveAccounts => [text.Get("Digest.Column.Account"), text.Get("Digest.Column.LastLogon"), text.Get("Digest.Column.Ou")],
            DigestSectionKind.ActiveExceptions =>
                [text.Get("Digest.Column.Rule"), text.Get("Digest.Column.Exception"), text.Get("Digest.Column.Expires"), text.Get("Digest.Column.CreatedBy")],
            _ => [text.Get("Label.Time"), text.Get("Label.Object"), text.Get("Label.Change"), text.Get("Label.Who")],
        };
    }

    /// <summary>Czas · Obiekt · Zmiana · Kto · Stan of "Wymaga uwagi".</summary>
    public static IReadOnlyList<string> AttentionHeaders(NotificationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return [.. Headers(DigestSectionKind.Health, context), context.Text.Get("Digest.Column.State")];
    }

    /// <summary>The cells of one "Wymaga uwagi" event (with its acknowledgement state); <paramref name="previousSeverity"/>
    /// – the severity its alert showed before an escalation (13.2 #7) – adds "(priorytet: Średni → Wysoki)" to the change.</summary>
    public static IReadOnlyList<string> AttentionCells(WatchEvent watchEvent, NotificationContext context, Severity? previousSeverity = null)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        ArgumentNullException.ThrowIfNull(context);
        var e = Safe(watchEvent, context);
        var state = e.AcknowledgedAt is null
            ? context.Text.Get("Digest.Unacknowledged")
            : context.Text.Format("Digest.Acknowledged", e.AcknowledgedBy ?? string.Empty);
        var cells = EventCells(e, context, false).ToList();
        if (previousSeverity is { } previous && previous != e.Severity)
        {
            cells[2] += " (" + context.Text.Format("Digest.SeverityChanged", context.Text.SeverityName(previous), context.Text.SeverityName(e.Severity)) + ")";
        }

        return [.. cells, state];
    }

    /// <summary>The cells of <paramref name="row"/> (same count as <see cref="Headers"/> of its section);
    /// <paramref name="reportTime"/> (the period end) decides what "dziś" is.</summary>
    public static IReadOnlyList<string> Cells(DigestRow row, NotificationContext context, DateTimeOffset reportTime)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(context);
        return row switch
        {
            DigestEventRow r => EventCells(Safe(r.Event, context), context, r.Flagged),
            DigestObjectRow r => ObjectCells(r, context),
            DigestLockoutRow r => LockoutCells(r, context, reportTime),
            DigestExpiryRow r => ExpiryCells(r, context),
            DigestInactiveRow r => InactiveCells(r, context),
            DigestExceptionRow r => ExceptionCells(r, context),
            _ => [],
        };
    }

    /// <summary>"Zmiana" of a sanitized event: the kind and what changed ("Dodano do grupy: Domain Admins",
    /// "Zmiana atrybutów: Dział: Sprzedaż → Księgowość").</summary>
    public static string Change(WatchEvent e, NotificationContext context)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(context);
        var title = context.Text.KindTitle(e.Kind);
        if (DigestSections.IsHealth(e.Kind) || e.Kind == WatchEventKind.WatchdogConfigurationChanged)
        {
            return AlertContentBuilder.Sentence(e, context);
        }

        if (e.Burst is { } burst)
        {
            return title + ": " + context.Objects.Burst(burst);
        }

        if (e.Member is not null)
        {
            return title + ": " + context.Objects.Name(e.Member);
        }

        if (e.Kind == WatchEventKind.ObjectMoved)
        {
            return title + ": " + ObjectDisplay.ParentPath(e.PreviousDn) + " → " + ObjectDisplay.ParentPath(e.Object.Dn);
        }

        if (e.Changes.Count == 0 || e.Kind is WatchEventKind.PasswordChanged or WatchEventKind.PasswordAgeReset)
        {
            return title;
        }

        var rows = AlertContentBuilder.ChangeRows(e, context);
        var shown = string.Join("; ", rows.Take(MaxChangesPerRow).Select(r => r.Before is null ? r.Attribute + ": " + r.After : r.Attribute + ": " + r.Before + " → " + r.After));
        return title + ": " + shown + (rows.Count > MaxChangesPerRow ? " …" : string.Empty);
    }

    private static IReadOnlyList<string> EventCells(WatchEvent e, NotificationContext context, bool flagged)
    {
        var change = Change(e, context);
        if (flagged)
        {
            change += " (" + context.Text.Get("Digest.Unusual") + ")";
        }

        return [context.Time.LocalShort(e.OriginatingTime ?? e.DetectedAt), AlertContentBuilder.ObjectName(e, context), change, Who(e, context)];
    }

    private static IReadOnlyList<string> ObjectCells(DigestObjectRow row, NotificationContext context)
    {
        var events = row.Events.Select(e => Safe(e, context)).ToList();
        var name = events.Count > 0 ? AlertContentBuilder.ObjectName(events[^1], context) : context.Objects.Name(DetailLevelFilter.SafeReference(row.Object));
        var changes = string.Join(" · ", events.Select(e => context.Time.LocalTime(e.OriginatingTime ?? e.DetectedAt) + " " + Change(e, context)));
        var who = string.Join(", ", events.Select(e => Who(e, context)).Where(w => w.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
        var time = events.Count > 0 ? context.Time.LocalShort(events[^1].OriginatingTime ?? events[^1].DetectedAt) : string.Empty;
        return [time, name, changes, who];
    }

    private static IReadOnlyList<string> LockoutCells(DigestLockoutRow row, NotificationContext context, DateTimeOffset reportTime)
    {
        var text = context.Text;
        var lockouts = row.Lockouts.Select(e => Safe(e, context)).ToList();
        var last = row.LastAt;
        var day = context.Time.Date(row.Day) == context.Time.LocalDate(reportTime) ? text.Get("Digest.Today") : context.Time.Date(row.Day);
        var summary = text.Format("Digest.LockedOut", row.Count, day, context.Time.LocalTime(last));
        if (row.Unlocks > 0)
        {
            summary += "; " + text.Format("Digest.Unlocked", row.Unlocks);
        }

        // Sources are masked like the lockout events (Minimal: none) and sanitized.
        var sources = lockouts.Any(e => e.Lockout?.Sources.Count > 0)
            ? context.Objects.LockoutSources(new LockoutInfo
            {
                LockedAt = last,
                Sources = [.. row.TopSources.Select(s => s with { IpAddress = Sanitize(s.IpAddress), Workstation = Sanitize(s.Workstation) })],
            })
            : null;
        return [context.Time.LocalShort(last), AlertContentBuilder.ObjectName(lockouts[^1], context), summary, sources ?? string.Empty];
    }

    private static IReadOnlyList<string> ExpiryCells(DigestExpiryRow row, NotificationContext context)
    {
        var expiry = row.Expiry;
        var when = expiry.Kind == ExpiryKind.Account ? context.Time.LocalDate(expiry.ExpiresAt) : context.Time.LocalShort(expiry.ExpiresAt);
        return
        [
            context.Time.LocalDate(expiry.ExpiresAt),
            context.Objects.Name(DetailLevelFilter.SafeReference(expiry.Object)),
            context.Text.Format("Digest.Expiry." + expiry.Kind, when),
        ];
    }

    private static IReadOnlyList<string> InactiveCells(DigestInactiveRow row, NotificationContext context)
    {
        var account = row.Account;
        var reference = DetailLevelFilter.SafeReference(account.Account);
        var name = context.Objects.Name(reference);
        if (account.Privileged)
        {
            name += " – " + context.Text.Get("Digest.Privileged");
        }

        var lastLogon = account.LastLogon is { } at ? context.Time.LocalDate(at) : context.Text.Get("Digest.NeverLoggedOn");
        var ou = context.DetailLevel == DetailLevel.Minimal ? string.Empty : ObjectDisplay.ParentPath(reference.Dn);
        return [name, lastLogon, ou];
    }

    private static IReadOnlyList<string> ExceptionCells(DigestExceptionRow row, NotificationContext context)
    {
        var text = context.Text;
        var exclusion = row.Exclusion;
        var parts = new List<string> { UntrustedText.Sanitize(exclusion.Reason) };
        if (!string.IsNullOrWhiteSpace(exclusion.Actor))
        {
            parts.Add(text.Format("Digest.Exception.Actor", UntrustedText.Sanitize(exclusion.Actor)));
        }

        if (exclusion.ObjectGuid is { } guid)
        {
            parts.Add(text.Format("Digest.Exception.Object", guid.ToString("D", CultureInfo.InvariantCulture)));
        }

        if (!string.IsNullOrWhiteSpace(exclusion.SubtreeDn))
        {
            parts.Add(text.Format("Digest.Exception.Subtree", UntrustedText.Sanitize(exclusion.SubtreeDn)));
        }

        if (!string.IsNullOrWhiteSpace(exclusion.Attribute))
        {
            parts.Add(text.Format("Digest.Exception.Attribute", UntrustedText.Sanitize(exclusion.Attribute)));
        }

        var until = exclusion.ExpiresAt is { } expires ? text.Format("Digest.Exception.Until", context.Time.LocalShort(expires)) : text.Get("Digest.Exception.NoExpiry");
        return [context.Objects.RuleName(row.Rule), string.Join(" · ", parts), until, UntrustedText.Sanitize(exclusion.CreatedBy)];
    }

    private static string Who(WatchEvent e, NotificationContext context) =>
        e.Attribution.Status == AttributionStatus.NotApplicable || e.Kind == WatchEventKind.AccountLockedOut
            ? string.Empty
            : context.Objects.ActorShort(e.Attribution)
                ?? context.Text.Get(e.Attribution.Status == AttributionStatus.Pending ? "Value.ActorPending" : "Value.Unknown");

    private static WatchEvent Safe(WatchEvent e, NotificationContext context) => DetailLevelFilter.ForNotification(e, context.DetailLevel, context.MaxValueLength);

    private static string? Sanitize(string? value) => value is null ? null : UntrustedText.Sanitize(value);
}
