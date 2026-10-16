using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Scans;

/// <summary>
/// Account expiry (section 10.14): users whose <c>accountExpires</c> lies between yesterday and the largest threshold
/// (+ 4 days, at least the digest's upcoming horizon); AccountExpired once, AccountExpiring per threshold (calendar
/// days to the displayed last valid day, business-day adjusted) and the upcoming-expiry list of the digest.
/// </summary>
internal sealed class AccountExpiryScanner
{
    private readonly ExpiryEvaluator _evaluator;
    private readonly IExpiryNoticeStore _notices;
    private readonly IBusinessDayCalendar _calendar;

    public AccountExpiryScanner(ExpiryEvaluator evaluator, IExpiryNoticeStore notices, IBusinessDayCalendar calendar)
    {
        _evaluator = evaluator;
        _notices = notices;
        _calendar = calendar;
    }

    public async Task RunAsync(ScanContext context, CancellationToken cancellationToken)
    {
        var scan = context.Configuration.Scans.AccountExpiry;
        if (!scan.Enabled)
        {
            await _notices.ReplaceUpcomingAsync(ExpiryKind.Account, [], cancellationToken).ConfigureAwait(false);
            return;
        }

        var upcomingDays = context.Configuration.Digest.UpcomingExpiryDays;
        var horizon = Math.Max(scan.ThresholdsDays.DefaultIfEmpty(0).Max() + 4, upcomingDays + 1);
        var filters = new List<string>
        {
            DetectionFilters.Users,
            LdapFilter.GreaterOrEqual(Attr.AccountExpires, AdConvert.ToFileTime(context.Now.AddDays(-1))),
            LdapFilter.LessOrEqual(Attr.AccountExpires, AdConvert.ToFileTime(context.Now.AddDays(horizon))),
        };
        if (!scan.IncludeDisabled)
        {
            filters.Add(DetectionFilters.Enabled);
        }

        var calendar = scan.FireOnBusinessDays ? _calendar : null;
        var upcoming = new List<UpcomingExpiry>();
        await foreach (var entry in ScanQueries.SearchAsync(context, LdapFilter.And(filters), [Attr.AccountExpires, Attr.ServicePrincipalName], cancellationToken).ConfigureAwait(false))
        {
            var raw = entry.GetInt64(Attr.AccountExpires);
            if (raw is not > 0 || raw == long.MaxValue || AdConvert.FromFileTime(raw.Value) is not { } expiresAt)
            {
                continue;
            }

            var target = ScanEventWriter.RefOf(entry);
            var lastDay = ExpiryThresholds.LastValidDay(expiresAt, ExpiryKind.Account, context.TimeZone);
            var info = new ExpiryInfo(ExpiryKind.Account, expiresAt, lastDay, ExpiryThresholds.DaysLeft(lastDay, context.Today), 0);
            await _evaluator.EvaluateAsync(context, target, info, raw.Value, scan.ThresholdsDays, calendar, cancellationToken).ConfigureAwait(false);
            if (expiresAt > context.Now && info.DaysLeft <= upcomingDays)
            {
                upcoming.Add(new UpcomingExpiry { Object = target, Kind = ExpiryKind.Account, ExpiresAt = expiresAt, Tags = ScanQueries.Tags(context, entry, target) });
            }
        }

        await _notices.ReplaceUpcomingAsync(ExpiryKind.Account, upcoming, cancellationToken).ConfigureAwait(false);
    }
}
