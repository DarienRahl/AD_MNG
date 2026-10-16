using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Formatting;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Notifications.Digest;

/// <summary>The sections of a digest from the reported events of the period (section 13.4): row selection, grouping,
/// roll-ups and the <c>MaxRowsPerSection</c> cap.</summary>
public static class DigestSectionBuilder
{
    /// <summary>Most sources shown per lockout row.</summary>
    public const int MaxLockoutSources = 3;

    private static readonly string[] UnusualComputerTags = [EventTags.DefaultContainer, EventTags.UnexpectedContainer, EventTags.CreatedByNonAdmin];

    /// <summary>One row per event, oldest first.</summary>
    public static DigestSection Events(DigestSectionKind kind, IEnumerable<WatchEvent> events, int maxRows)
    {
        var ordered = events.OrderBy(Time).ThenBy(e => e.Id).ToList();
        return new DigestSection { Kind = kind, Total = ordered.Count, Rows = [.. ordered.Take(maxRows).Select(e => new DigestEventRow(e))] };
    }

    /// <summary>New computers grouped by parent OU (path, then time); unusual ones (default container, created by a
    /// non-admin) are flagged.</summary>
    public static DigestSection NewComputers(IEnumerable<WatchEvent> events, int maxRows)
    {
        var ordered = events
            .OrderBy(e => ObjectDisplay.ParentPath(e.Object.Dn), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(Time)
            .ThenBy(e => e.Id)
            .ToList();
        return new DigestSection
        {
            Kind = DigestSectionKind.NewComputers,
            Total = ordered.Count,
            Rows = [.. ordered.Take(maxRows).Select(e => new DigestEventRow(e, e.Tags.Any(t => UnusualComputerTags.Contains(t, StringComparer.OrdinalIgnoreCase))))],
        };
    }

    /// <summary>Account parameter changes grouped per object (oldest change first).</summary>
    public static DigestSection AccountParameters(IEnumerable<WatchEvent> events, int maxRows)
    {
        var groups = events
            .GroupBy(ObjectKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(Time).ThenBy(e => e.Id).ToList())
            .OrderBy(g => Time(g[0]))
            .ToList();
        return new DigestSection
        {
            Kind = DigestSectionKind.AccountParameters,
            Total = groups.Count,
            Rows = [.. groups.Take(maxRows).Select(g => new DigestObjectRow(g[^1].Object, g))],
        };
    }

    /// <summary>One row per account and local day ("zablokowane 5× dziś, ostatnio 10:42", top sources, unlocks); mass
    /// lockouts first.</summary>
    public static DigestSection Lockouts(IEnumerable<WatchEvent> events, BusinessCalendar calendar, int maxRows)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var list = events.ToList();
        var unlocks = list.Where(e => e.Kind == WatchEventKind.AccountUnlocked)
            .GroupBy(e => (ObjectKey(e), calendar.LocalDate(Time(e))))
            .ToDictionary(g => g.Key, g => g.Count());
        var mass = list.Where(e => e.Kind == WatchEventKind.MassLockout).OrderBy(Time).Select(e => (DigestRow)new DigestEventRow(e));
        var rolled = list.Where(e => e.Kind == WatchEventKind.AccountLockedOut)
            .GroupBy(e => (Key: ObjectKey(e), Day: calendar.LocalDate(e.Lockout?.LockedAt ?? e.DetectedAt)))
            .Select(g =>
            {
                var lockouts = g.OrderBy(e => e.Lockout?.LockedAt ?? e.DetectedAt).ThenBy(e => e.Id).ToList();
                return new DigestLockoutRow(lockouts[^1].Object, g.Key.Day, lockouts, unlocks.GetValueOrDefault(g.Key), TopSources(lockouts));
            })
            .OrderByDescending(r => r.Count)
            .ThenByDescending(r => r.LastAt)
            .Select(r => (DigestRow)r);
        var rows = mass.Concat(rolled).ToList();
        return new DigestSection { Kind = DigestSectionKind.Lockouts, Total = rows.Count, Rows = [.. rows.Take(maxRows)] };
    }

    /// <summary>Password resets, own changes, changes by an unknown actor and age resets counted separately; changes of
    /// privileged accounts listed.</summary>
    public static DigestSection Passwords(IEnumerable<WatchEvent> events, int maxRows)
    {
        var list = events.ToList();
        var changes = list.Where(e => e.Kind == WatchEventKind.PasswordChanged).ToList();
        var resets = changes.Count(e => HasTag(e, EventTags.PasswordReset));
        var own = changes.Count(e => !HasTag(e, EventTags.PasswordReset) && HasTag(e, EventTags.SelfChange));
        var privileged = list.Where(e => HasTag(e, EventTags.Privileged)).OrderBy(Time).ThenBy(e => e.Id).ToList();
        return new DigestSection
        {
            Kind = DigestSectionKind.Passwords,
            Facts =
            [
                new DigestFact("PasswordResets", resets),
                new DigestFact("OwnPasswordChanges", own),
                new DigestFact("OtherPasswordChanges", changes.Count - resets - own),
                new DigestFact("PasswordAgeResets", list.Count(e => e.Kind == WatchEventKind.PasswordAgeReset)),
            ],
            Total = privileged.Count,
            Rows = [.. privileged.Take(maxRows).Select(e => new DigestEventRow(e))],
        };
    }

    /// <summary>Expiries after the period end within <paramref name="days"/> days, soonest first.</summary>
    public static DigestSection Upcoming(IEnumerable<UpcomingExpiry> upcoming, DateTimeOffset periodEnd, int days, int maxRows)
    {
        var until = periodEnd.AddDays(Math.Max(0, days));
        var list = upcoming.Where(u => u.ExpiresAt > periodEnd && u.ExpiresAt <= until).OrderBy(u => u.ExpiresAt).ToList();
        return new DigestSection { Kind = DigestSectionKind.UpcomingExpiry, Total = list.Count, Rows = [.. list.Take(maxRows).Select(u => new DigestExpiryRow(u))] };
    }

    /// <summary>The weekly inactive-accounts section; unknown counts when the directory could not be queried.</summary>
    public static DigestSection Inactive(InactiveAccountsResult? result, int maxRows) => new()
    {
        Kind = DigestSectionKind.InactiveAccounts,
        Facts =
        [
            new DigestFact("InactiveUsers", result?.Users),
            new DigestFact("InactiveComputers", result?.Computers),
            new DigestFact("InactivePrivileged", result?.Privileged),
        ],
        Total = result?.Total ?? 0,
        Rows = [.. (result?.Rows ?? []).Take(maxRows).Select(a => new DigestInactiveRow(a))],
    };

    /// <summary>The weekly hygiene counters.</summary>
    public static DigestSection Hygiene(HygieneCounters? counters) => new()
    {
        Kind = DigestSectionKind.Hygiene,
        Facts =
        [
            new DigestFact("PasswordNotRequired", counters?.PasswordNotRequired),
            new DigestFact("PasswordNeverExpires", counters?.PasswordNeverExpires),
            new DigestFact("UnconstrainedDelegation", counters?.UnconstrainedDelegation),
            new DigestFact("AdminCountOrphans", counters?.AdminCountOrphans),
            new DigestFact("KrbtgtPasswordAgeDays", counters?.KrbtgtPasswordAgeDays),
            new DigestFact("AdministratorPasswordAgeDays", counters?.AdministratorPasswordAgeDays),
        ],
    };

    /// <summary>Rule exclusions still in force at the period end, in rule order.</summary>
    public static DigestSection Exceptions(IEnumerable<RuleSettings?> rules, DateTimeOffset periodEnd, int maxRows)
    {
        var rows = rules
            .Where(r => r is not null)
            .SelectMany(r => (r!.Exclusions ?? []).Where(x => x is not null && (x.ExpiresAt is null || x.ExpiresAt > periodEnd)).Select(x => new DigestExceptionRow(r, x)))
            .ToList();
        return new DigestSection { Kind = DigestSectionKind.ActiveExceptions, Total = rows.Count, Rows = [.. rows.Take(maxRows)] };
    }

    /// <summary>The time an event is shown and sorted by (originating time when known).</summary>
    public static DateTimeOffset Time(WatchEvent e) => e.OriginatingTime ?? e.DetectedAt;

    private static string ObjectKey(WatchEvent e) => e.Object.ObjectGuid?.ToString("N") ?? e.Object.Dn;

    private static bool HasTag(WatchEvent e, string tag) => e.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase);

    private static List<LockoutSource> TopSources(IEnumerable<WatchEvent> lockouts) =>
    [
        .. lockouts
            .SelectMany(e => e.Lockout?.Sources ?? [])
            .GroupBy(s => (Ip: s.IpAddress?.ToLowerInvariant(), Workstation: s.Workstation?.ToUpperInvariant()))
            .Select(g => g.First() with { Count = g.Sum(s => s.Count) })
            .OrderByDescending(s => s.Count)
            .ThenBy(s => s.IpAddress ?? s.Workstation, StringComparer.OrdinalIgnoreCase)
            .Take(MaxLockoutSources),
    ];
}
