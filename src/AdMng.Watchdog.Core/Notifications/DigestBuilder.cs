using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Notifications.Digest;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>
/// <see cref="DigestModel"/> from the events of a period, the upcoming expiries, the health registry and – on the
/// weekly day – the live directory reports (section 13.4). Counters and "Wymaga uwagi" use every event of the period;
/// sections only the events matched by a rule with <c>IncludeInDigest</c>, health and configuration changes. The period
/// itself comes from <see cref="ResolvePeriodStart"/> (weekend / holiday catch-up).
/// </summary>
public sealed class DigestBuilder
{
    /// <summary>Most event ids in the model's <see cref="DigestModel.EventIds"/> (webhook payload).</summary>
    public const int MaxEventIds = 5000;

    private readonly IEventStore _events;
    private readonly IExpiryNoticeStore _expiry;
    private readonly IHealthRegistry _health;
    private readonly RolloutGate _rollout;
    private readonly IDigestDirectoryReports _reports;
    private readonly BusinessCalendarProvider _calendars;

    public DigestBuilder(IEventStore events, IExpiryNoticeStore expiry, IHealthRegistry health, RolloutGate rollout, IDigestDirectoryReports reports,
        BusinessCalendarProvider calendars)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(expiry);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(rollout);
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(calendars);
        _events = events;
        _expiry = expiry;
        _health = health;
        _rollout = rollout;
        _reports = reports;
        _calendars = calendars;
    }

    /// <summary>
    /// The start of the digest period ending at <paramref name="periodEnd"/>: the end of the previous digest (first
    /// digest: 24 h back). With <c>CoverNonBusinessDays</c>, the digest of the first business day after non-business
    /// days starts at the scheduled time of the last business day instead (Friday 07:30 → Monday 07:30), when that is
    /// earlier.
    /// </summary>
    public static (DateTimeOffset Start, bool CoversNonBusinessDays) ResolvePeriodStart(WatchdogConfiguration configuration, BusinessCalendar calendar,
        DateTimeOffset? lastPeriodEnd, DateTimeOffset periodEnd)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(calendar);
        var digest = configuration.Digest ?? new DigestSettings();
        var start = lastPeriodEnd is { } last && last < periodEnd ? last : periodEnd.AddHours(-24);
        var today = calendar.LocalDate(periodEnd);
        if (digest.CoverNonBusinessDays && calendar.IsBusinessDay(today) && !calendar.IsBusinessDay(today.AddDays(-1)))
        {
            var from = Schedule.OnDate(calendar.PreviousBusinessDay(today), digest.Time, calendar.TimeZone);
            if (from < start)
            {
                return (from, true);
            }
        }

        return (start, false);
    }

    /// <summary>Builds the digest of [<paramref name="periodStart"/>, <paramref name="periodEnd"/>).</summary>
    public async Task<DigestModel> BuildAsync(WatchdogConfiguration configuration, DateTimeOffset periodStart, DateTimeOffset periodEnd,
        bool coversNonBusinessDays, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var digest = configuration.Digest ?? new DigestSettings();
        var maxRows = Math.Max(1, digest.MaxRowsPerSection);
        var calendar = _calendars.For(configuration);
        var events = new List<WatchEvent>();
        await foreach (var e in _events.GetRangeAsync(periodStart, periodEnd, cancellationToken).ConfigureAwait(false))
        {
            if (e.Kind != WatchEventKind.Test)
            {
                events.Add(e);
            }
        }

        var digestRules = new HashSet<string>(
            (configuration.Rules ?? []).Where(r => r is not null && r.Delivery?.IncludeInDigest != false).Select(r => r.Id),
            StringComparer.OrdinalIgnoreCase);
        var reported = events
            .Where(e => DigestSections.IsHealth(e.Kind) || e.Kind == WatchEventKind.WatchdogConfigurationChanged || e.MatchedRules.Any(digestRules.Contains))
            .ToList();

        var localDay = calendar.LocalDate(periodEnd).DayOfWeek;
        var inactiveSettings = configuration.Scans?.InactiveAccounts ?? new InactiveAccountsScan();
        var withInactive = inactiveSettings.Enabled && localDay == inactiveSettings.ReportDay;
        var withHygiene = localDay == digest.WeeklySectionsDay;
        var sections = new List<DigestSection>();
        foreach (var kind in Enum.GetValues<DigestSectionKind>())
        {
            var section = await SectionAsync(kind, reported, configuration, calendar, periodEnd, maxRows, withInactive, withHygiene, cancellationToken)
                .ConfigureAwait(false);
            if (section is not null && (section.Total > 0 || section.Facts.Count > 0))
            {
                sections.Add(section);
            }
        }

        var attention = events
            .Where(e => e.Severity >= Severity.High)
            .OrderByDescending(e => e.Severity)
            .ThenBy(DigestSectionBuilder.Time)
            .ThenBy(e => e.Id)
            .ToList();
        var shownAttention = attention.Take(maxRows).ToList();
        return new DigestModel
        {
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            CoversNonBusinessDays = coversNonBusinessDays,
            IsWeekly = withInactive || withHygiene,
            Domain = DomainOf(events, _health.Directory.DcHostName),
            Health = await HealthAsync(configuration, events, periodEnd, cancellationToken).ConfigureAwait(false),
            Counts = DigestCountsBuilder.Count(events),
            Attention = shownAttention,
            Sections = sections,
            EventIds = [.. reported.Concat(attention).DistinctBy(e => e.Id).OrderBy(e => e.Id).Take(MaxEventIds).Select(e => e.EventId)],
            ShownEventIds = [.. sections.SelectMany(s => s.Rows).SelectMany(r => r.EventIds).Concat(shownAttention.Select(e => e.Id)).Distinct().Order()],
        };
    }

    private async Task<DigestSection?> SectionAsync(DigestSectionKind kind, IReadOnlyList<WatchEvent> reported, WatchdogConfiguration configuration,
        BusinessCalendar calendar, DateTimeOffset periodEnd, int maxRows, bool withInactive, bool withHygiene, CancellationToken cancellationToken)
    {
        var digest = configuration.Digest ?? new DigestSettings();
        var ofKind = reported.Where(e => DigestSections.For(e.Kind) == kind);
        switch (kind)
        {
            case DigestSectionKind.NewComputers:
                return DigestSectionBuilder.NewComputers(ofKind, maxRows);
            case DigestSectionKind.AccountParameters:
                return DigestSectionBuilder.AccountParameters(ofKind, maxRows);
            case DigestSectionKind.Lockouts:
                return DigestSectionBuilder.Lockouts(ofKind, calendar, maxRows);
            case DigestSectionKind.Passwords:
                var passwords = ofKind.ToList();
                return passwords.Count == 0 ? null : DigestSectionBuilder.Passwords(passwords, maxRows);
            case DigestSectionKind.UpcomingExpiry:
                var upcoming = await _expiry.GetUpcomingAsync(cancellationToken).ConfigureAwait(false);
                return DigestSectionBuilder.Upcoming(upcoming, periodEnd, digest.UpcomingExpiryDays, maxRows);
            case DigestSectionKind.InactiveAccounts:
                return withInactive
                    ? DigestSectionBuilder.Inactive(
                        await _reports.GetInactiveAccountsAsync(configuration.Scans?.InactiveAccounts ?? new InactiveAccountsScan(), periodEnd, maxRows, cancellationToken)
                            .ConfigureAwait(false),
                        maxRows)
                    : null;
            case DigestSectionKind.Hygiene:
                return withHygiene ? DigestSectionBuilder.Hygiene(await _reports.GetHygieneAsync(periodEnd, cancellationToken).ConfigureAwait(false)) : null;
            case DigestSectionKind.ActiveExceptions:
                return DigestSectionBuilder.Exceptions(configuration.Rules ?? [], periodEnd, maxRows);
            default:
                return DigestSectionBuilder.Events(kind, ofKind, maxRows);
        }
    }

    private async Task<DigestHealth> HealthAsync(WatchdogConfiguration configuration, IReadOnlyList<WatchEvent> events, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var directory = _health.Directory;
        var coverage = _health.Attribution.Enabled
            ? _health.Attribution.DomainControllers.Where(d => d.Coverage24h is not null).Select(d => d.Coverage24h!.Value).DefaultIfEmpty(double.NaN).Min()
            : double.NaN;
        var channels = configuration.Channels ?? [];
        var broken = _health.Channels
            .Where(c => c.State == HealthState.Error)
            .Select(c => channels.FirstOrDefault(s => s is not null && string.Equals(s.Id, c.ChannelId, StringComparison.OrdinalIgnoreCase))?.Name ?? c.ChannelId)
            .ToList();
        var domain = DomainOf(events, directory.DcHostName);
        return new DigestHealth
        {
            DirectoryState = directory.State,
            DcHostName = directory.DcHostName,
            LastPollAt = directory.LastPollAt,
            AttributionCoverage = double.IsNaN(coverage) ? null : coverage,
            EventLogGaps = events.Count(e => e.Kind == WatchEventKind.HealthEventLogGap),
            BrokenChannels = broken,
            RolloutUntil = await _rollout.ActiveUntilAsync(configuration, domain, now, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>The watched domain: the most frequent event domain, else the DNS suffix of the DC host name.</summary>
    private static string? DomainOf(IReadOnlyList<WatchEvent> events, string? dcHostName)
    {
        var fromEvents = events
            .Where(e => !string.IsNullOrWhiteSpace(e.Domain))
            .GroupBy(e => e.Domain!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();
        if (fromEvents is not null)
        {
            return fromEvents;
        }

        var dot = dcHostName?.IndexOf('.', StringComparison.Ordinal) ?? -1;
        return dot > 0 && dot < dcHostName!.Length - 1 ? dcHostName[(dot + 1)..] : null;
    }
}
