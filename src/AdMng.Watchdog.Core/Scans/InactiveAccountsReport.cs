using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Configuration;
using AdMng.Watchdog.Core.Detection;

namespace AdMng.Watchdog.Core.Scans;

/// <summary>
/// The inactive accounts section of the weekly digest (section 10.17): live queries only – no events, nothing stored.
/// Enabled users and computers whose <c>lastLogonTimestamp</c> is older than <c>Days</c> (or missing) and that are
/// older than <c>MinAgeDays</c>; privileged principals (current PrivilegeState) with <c>PrivilegedDays</c>. Used by the
/// DigestBuilder on <c>InactiveAccounts.ReportDay</c>.
/// </summary>
public sealed class InactiveAccountsReport
{
    private const int PageSize = 1000;

    private static readonly string[] Attributes =
    [
        Attr.ObjectGuid, Attr.ObjectSid, Attr.ObjectClass, Attr.Name, Attr.DisplayName, Attr.SamAccountName, Attr.LastLogonTimestamp,
    ];

    private readonly IDirectorySessionProvider _sessions;
    private readonly IDetectionStore _store;
    private readonly IWatchdogConfigProvider _configuration;
    private readonly TimeProvider _timeProvider;

    public InactiveAccountsReport(IDirectorySessionProvider sessions, IDetectionStore store, IWatchdogConfigProvider configuration, TimeProvider timeProvider)
    {
        _sessions = sessions;
        _store = store;
        _configuration = configuration;
        _timeProvider = timeProvider;
    }

    /// <summary>True when the report is enabled and <paramref name="now"/> is on <c>ReportDay</c> in the configured time zone.</summary>
    public bool IsReportDay(DateTimeOffset now)
    {
        var configuration = _configuration.Current.Configuration;
        var zone = TimeZoneResolver.TryResolve(configuration.General.TimeZone, out var resolved) ? resolved : TimeZoneInfo.Utc;
        return configuration.Scans.InactiveAccounts.Enabled && TimeZoneInfo.ConvertTime(now, zone).DayOfWeek == configuration.Scans.InactiveAccounts.ReportDay;
    }

    /// <summary>Runs the live queries; null when the report is disabled.</summary>
    /// <param name="maxRows">Rows per section (<c>Digest.MaxRowsPerSection</c>).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<InactiveAccountsSummary?> BuildAsync(int maxRows, CancellationToken cancellationToken)
    {
        var settings = _configuration.Current.Configuration.Scans.InactiveAccounts;
        if (!settings.Enabled)
        {
            return null;
        }

        var session = await _sessions.GetSessionAsync(cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var domainDn = session.Info.DefaultNamingContext;
        var users = await QueryAsync(session, domainDn, Filter(DetectionFilters.Users, now, settings.Days, settings.MinAgeDays), maxRows, null, cancellationToken).ConfigureAwait(false);
        var computers = await QueryAsync(session, domainDn, Filter(LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.Computer), now, settings.Days, settings.MinAgeDays),
            maxRows, null, cancellationToken).ConfigureAwait(false);
        var state = await _store.GetPrivilegeStateAsync(cancellationToken).ConfigureAwait(false);
        var privilegedKeys = state?.Principals.Select(p => p.PrincipalKey).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var privileged = privilegedKeys.Count == 0
            ? []
            : await QueryAsync(session, domainDn, Filter(LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.User), now, settings.PrivilegedDays, settings.MinAgeDays),
                int.MaxValue, privilegedKeys, cancellationToken).ConfigureAwait(false);
        return new InactiveAccountsSummary(
            Section(users, settings.Days, maxRows),
            Section(computers, settings.Days, maxRows),
            Section(privileged, settings.PrivilegedDays, maxRows),
            now);
    }

    private static string Filter(string classFilter, DateTimeOffset now, int days, int minAgeDays) => LdapFilter.And(
        classFilter,
        DetectionFilters.Enabled,
        LdapFilter.Or(
            LdapFilter.LessOrEqual(Attr.LastLogonTimestamp, AdConvert.ToFileTime(now.AddDays(-days))),
            LdapFilter.NotPresent(Attr.LastLogonTimestamp)),
        LdapFilter.LessOrEqual(Attr.WhenCreated, AdConvert.ToGeneralizedTime(now.AddDays(-minAgeDays))));

    private static async Task<List<InactiveAccount>> QueryAsync(IDirectorySession session, string domainDn, string filter, int maxRows,
        HashSet<string>? keep, CancellationToken cancellationToken)
    {
        var rows = new List<InactiveAccount>();
        await foreach (var entry in session.SearchAsync(new DirectorySearch { BaseDn = domainDn, Filter = filter, Attributes = Attributes, PageSize = PageSize }, cancellationToken)
            .ConfigureAwait(false))
        {
            if (keep is not null && (entry.ObjectGuid is not { } guid || !keep.Contains(guid.ToString("N"))))
            {
                continue;
            }

            rows.Add(new InactiveAccount(ScanEventWriter.RefOf(entry), entry.GetFileTime(Attr.LastLogonTimestamp), entry.Dn.Parent?.ToString()));
        }

        return rows;
    }

    private static InactiveAccountsSection Section(List<InactiveAccount> rows, int thresholdDays, int maxRows) => new(
        rows.Count,
        thresholdDays,
        [.. rows.OrderBy(r => r.LastLogon ?? DateTimeOffset.MinValue).ThenBy(r => r.Object.Dn, StringComparer.OrdinalIgnoreCase).Take(Math.Max(0, maxRows))]);
}
