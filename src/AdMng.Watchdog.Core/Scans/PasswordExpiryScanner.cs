using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Scans;

/// <summary>
/// Password expiry (section 10.14): enabled users with a set password that can expire, read with the constructed
/// <c>msDS-UserPasswordExpiryTimeComputed</c> (PSO-aware); 0 (must change) and "never" are skipped; scope
/// PrivilegedAndService keeps privileged principals and service accounts only. Same thresholds, dedup and business-day
/// rule as account expiry (<c>AccountExpiry.FireOnBusinessDays</c>); the expiry moment's local date is the last day.
/// </summary>
internal sealed class PasswordExpiryScanner
{
    private readonly ExpiryEvaluator _evaluator;
    private readonly IExpiryNoticeStore _notices;
    private readonly IBusinessDayCalendar _calendar;

    public PasswordExpiryScanner(ExpiryEvaluator evaluator, IExpiryNoticeStore notices, IBusinessDayCalendar calendar)
    {
        _evaluator = evaluator;
        _notices = notices;
        _calendar = calendar;
    }

    public async Task RunAsync(ScanContext context, CancellationToken cancellationToken)
    {
        var scan = context.Configuration.Scans.PasswordExpiry;
        if (scan.Scope == PasswordExpiryScope.None)
        {
            await _notices.ReplaceUpcomingAsync(ExpiryKind.Password, [], cancellationToken).ConfigureAwait(false);
            return;
        }

        var filter = LdapFilter.And(
            DetectionFilters.Users,
            LdapFilter.GreaterOrEqual(Attr.PwdLastSet, 1),
            DetectionFilters.Enabled,
            LdapFilter.Not(LdapFilter.BitAnd(Attr.UserAccountControl, (long)UserAccountControl.DontExpirePassword)));
        var calendar = context.Configuration.Scans.AccountExpiry.FireOnBusinessDays ? _calendar : null;
        var upcomingDays = context.Configuration.Digest.UpcomingExpiryDays;
        var upcoming = new List<UpcomingExpiry>();
        await foreach (var entry in ScanQueries.SearchAsync(context, filter, [WatchdogAttributes.UserPasswordExpiryTimeComputed, Attr.ServicePrincipalName], cancellationToken).ConfigureAwait(false))
        {
            var raw = entry.GetInt64(WatchdogAttributes.UserPasswordExpiryTimeComputed);
            if (raw is not > 0 || raw == long.MaxValue || AdConvert.FromFileTime(raw.Value) is not { } expiresAt || expiresAt <= context.Now)
            {
                continue;
            }

            var target = ScanEventWriter.RefOf(entry);
            var tags = ScanQueries.Tags(context, entry, target);
            if (scan.Scope == PasswordExpiryScope.PrivilegedAndService && tags.Count == 0)
            {
                continue;
            }

            var lastDay = ExpiryThresholds.LastValidDay(expiresAt, ExpiryKind.Password, context.TimeZone);
            var info = new ExpiryInfo(ExpiryKind.Password, expiresAt, lastDay, ExpiryThresholds.DaysLeft(lastDay, context.Today), 0);
            await _evaluator.EvaluateAsync(context, target, info, raw.Value, scan.ThresholdsDays, calendar, cancellationToken).ConfigureAwait(false);
            if (info.DaysLeft <= upcomingDays)
            {
                upcoming.Add(new UpcomingExpiry { Object = target, Kind = ExpiryKind.Password, ExpiresAt = expiresAt, Tags = tags });
            }
        }

        await _notices.ReplaceUpcomingAsync(ExpiryKind.Password, upcoming, cancellationToken).ConfigureAwait(false);
    }
}
