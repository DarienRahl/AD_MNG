using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Configuration;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Rules;

/// <summary>
/// <c>POST /rules/preview</c> (<see cref="IRulePreviewService"/>): what an unsaved rule would have matched and sent over
/// the last <see cref="RulePreviewRequest.Days"/> days (1–90) of stored events, at most <see cref="ScanLimit"/> events.
/// Matching uses the stored (final) attribution – a still pending actor counts as unresolved; messages are estimated
/// by <see cref="PreviewMessageEstimator"/> (aggregation, throttle, delivery hours, repeat suppression; rollout mode
/// ignored = the "would have sent N" figure of 12.5). Days are local days of <c>General.TimeZone</c>.
/// </summary>
public sealed class RulePreviewService : IRulePreviewService
{
    /// <summary>Maximum number of events scanned (17.2).</summary>
    public const int ScanLimit = 100_000;

    /// <summary>Maximum number of sample events returned (newest first).</summary>
    public const int MaxSamples = 20;

    private readonly IEventStore _events;
    private readonly IWatchdogConfigProvider _configProvider;
    private readonly RuleEngine _engine;
    private readonly TimeProvider _timeProvider;
    private readonly IBusinessHoursCalendar? _calendar;

    public RulePreviewService(IEventStore events, IWatchdogConfigProvider configProvider, RuleEngine engine, TimeProvider timeProvider,
        IBusinessHoursCalendar? calendar = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(configProvider);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _events = events;
        _configProvider = configProvider;
        _engine = engine;
        _timeProvider = timeProvider;
        _calendar = calendar;
    }

    /// <inheritdoc/>
    public async Task<RulePreviewResult> PreviewAsync(RulePreviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Rule);
        var rule = ConfigurationNormalizer.Rule(request.Rule);
        var days = Math.Clamp(request.Days, 1, 90);
        var configuration = _configProvider.Current.Configuration;
        var context = _engine.CreateContext(configuration);
        var now = _timeProvider.GetUtcNow();
        var timeZone = TimeZoneResolver.TryResolve(configuration.General?.TimeZone, out var zone) ? zone : TimeZoneInfo.Utc;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone).DateTime);
        var firstDay = today.AddDays(1 - days);
        var from = LocalMidnight(firstDay, timeZone);

        var matched = new List<(WatchEvent Event, Severity Severity)>();
        var scanned = 0;
        var limitReached = false;
        await foreach (var watchEvent in _events.GetRangeAsync(from, now, cancellationToken).ConfigureAwait(false))
        {
            if (++scanned > ScanLimit)
            {
                limitReached = true;
                break;
            }

            if (RuleMatcher.Match(rule, watchEvent, context, now, attributionGraceExpired: true) != RuleMatchOutcome.NoMatch)
            {
                matched.Add((watchEvent, rule.Severity ?? EventSeverities.Own(watchEvent)));
            }
        }

        matched.Sort((a, b) => a.Event.DetectedAt.CompareTo(b.Event.DetectedAt));
        var sent = PreviewMessageEstimator.Estimate(rule, context.Configuration, matched, IsBusinessTime(configuration));
        return new RulePreviewResult
        {
            MatchedEvents = matched.Count,
            EstimatedImmediateMessages = sent.Count,
            PerDay = PerDay(firstDay, days, timeZone, matched.Select(m => m.Event.DetectedAt), sent),
            Samples = [.. matched.Select(m => m.Event).OrderByDescending(e => e.DetectedAt).ThenByDescending(e => e.Id).Take(MaxSamples)],
            ScanLimitReached = limitReached,
        };
    }

    private Func<DateTimeOffset, bool> IsBusinessTime(WatchdogConfiguration configuration) =>
        _calendar is { } calendar ? instant => calendar.IsBusinessTime(instant, configuration) : _ => true;

    private static List<DailyCount> PerDay(DateOnly firstDay, int days, TimeZoneInfo timeZone,
        IEnumerable<DateTimeOffset> eventTimes, IEnumerable<DateTimeOffset> messageTimes)
    {
        var events = eventTimes.GroupBy(t => LocalDay(t, timeZone)).ToDictionary(g => g.Key, g => g.Count());
        var messages = messageTimes.GroupBy(t => LocalDay(t, timeZone)).ToDictionary(g => g.Key, g => g.Count());
        return [.. Enumerable.Range(0, days)
            .Select(firstDay.AddDays)
            .Select(day => new DailyCount(day, events.GetValueOrDefault(day), messages.GetValueOrDefault(day)))];
    }

    private static DateOnly LocalDay(DateTimeOffset instant, TimeZoneInfo timeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, timeZone).DateTime);

    private static DateTimeOffset LocalMidnight(DateOnly day, TimeZoneInfo timeZone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);   // a DST gap at midnight starts the day at the next valid minute
        }

        return new DateTimeOffset(local, timeZone.GetUtcOffset(local));
    }
}
