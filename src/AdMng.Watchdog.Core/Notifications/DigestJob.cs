using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Diagnostics;
using AdMng.Watchdog.Core.Formatting;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Notifications.Digest;
using Microsoft.Extensions.Logging;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>
/// <see cref="IWatchdogJob"/> "digest" and <see cref="IDigestService"/> (section 13.4). The schedule is
/// <c>Schedule.NextOccurrence(Digest.Time, Digest.Days, zone, now)</c> (DST-safe); a digest missed during a downtime is
/// sent at start-up when it is less than 12 h late, otherwise skipped. Each digest covers the time since the previous
/// one (<see cref="DigestBuilder.ResolvePeriodStart"/>, incl. the weekend / holiday catch-up) up to now – or just before
/// an event the rule evaluation has not processed yet (<see cref="DigestBuilder.ResolvePeriodEndAsync"/>) –, is enqueued
/// for every recipient (<see cref="DigestMessages"/>) – an empty period only with <c>SendWhenEmpty</c> – and recorded in
/// <see cref="IDigestRunStore"/>. Once the first message is enqueued, enqueueing and recording are not cancelled (a
/// service stop in between would send the digest again at start-up) and a failure to record does not repeat the digest:
/// the schedule moves on. Send-now and preview never move the schedule. The preview – served to Viewers – shows the
/// active exceptions only as a number (<see cref="DigestSectionBuilder.ExceptionCount"/>).
/// </summary>
public sealed partial class DigestJob : IWatchdogJob, IDigestService
{
    /// <summary>A missed digest older than this is skipped at start-up.</summary>
    public static readonly TimeSpan MaxCatchUpDelay = TimeSpan.FromHours(12);

    /// <summary>Longest wait between two schedule checks.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(5);

    private static readonly WatchdogSignal[] Signals = [WatchdogSignal.ConfigChanged, WatchdogSignal.DigestRequested];

    private readonly DigestBuilder _builder;
    private readonly IDigestRunStore _runs;
    private readonly IOutboxStore _outbox;
    private readonly IWatchdogConfigProvider _configProvider;
    private readonly BusinessCalendarProvider _calendars;
    private readonly IWatchdogSignals _signals;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DigestJob> _logger;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Lock _scheduleGate = new();
    private bool _started;
    private string? _scheduleKey;
    private DateTimeOffset? _due;
    private DateTimeOffset? _lastBusinessDayEnd;

    public DigestJob(DigestBuilder builder, IDigestRunStore runs, IOutboxStore outbox, IWatchdogConfigProvider configProvider, BusinessCalendarProvider calendars,
        IWatchdogSignals signals, TimeProvider timeProvider, ILogger<DigestJob> logger)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(configProvider);
        ArgumentNullException.ThrowIfNull(calendars);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _builder = builder;
        _runs = runs;
        _outbox = outbox;
        _configProvider = configProvider;
        _calendars = calendars;
        _signals = signals;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    public string Name => "digest";

    /// <inheritdoc/>
    public TimeSpan DefaultInterval { get; } = TimeSpan.FromMinutes(1);

    /// <inheritdoc/>
    public IReadOnlyList<WatchdogSignal> WakeSignals => Signals;

    /// <inheritdoc/>
    public DateTimeOffset? NextScheduledAt
    {
        get
        {
            lock (_scheduleGate)
            {
                return _due;
            }
        }
    }

    /// <inheritdoc/>
    public async Task<JobResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var now = _timeProvider.GetUtcNow();
            var configuration = _configProvider.Current.Configuration;
            var digest = configuration.Digest ?? new DigestSettings();
            var timeZone = _calendars.For(configuration).TimeZone;
            var due = await DueAsync(digest, timeZone, now, cancellationToken).ConfigureAwait(false);
            if (due is null)
            {
                return new JobResult(DefaultInterval);
            }

            if (now >= due.Value)
            {
                if (now - due.Value >= MaxCatchUpDelay)
                {
                    LogSkipped(due.Value);
                }
                else
                {
                    await SendAsync(configuration, now, cancellationToken).ConfigureAwait(false);
                }

                due = Schedule.NextOccurrence(digest.Time, digest.Days ?? [], timeZone, now);
                SetDue(due);
            }

            var wait = due is { } next ? next - now : DefaultInterval;
            return new JobResult(wait < TimeSpan.Zero ? TimeSpan.Zero : wait > MaxWait ? MaxWait : wait);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SafeExceptionLog.LogError(_logger, ex, "digest");
            return new JobResult(DefaultInterval, Failed: true, FailureCode: SafeExceptionLog.ErrorCode(ex));
        }
    }

    /// <inheritdoc/>
    public Task<DigestPreviewDto> PreviewAsync(CancellationToken cancellationToken) => PreviewAsync(DetailLevel.Full, cancellationToken);

    /// <inheritdoc/>
    public async Task<DigestPreviewDto> PreviewAsync(DetailLevel callerLevel, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var configuration = _configProvider.Current.Configuration;
        var last = await _runs.GetLastPeriodEndAsync(cancellationToken).ConfigureAwait(false);
        var end = await _builder.ResolvePeriodEndAsync(now, last, cancellationToken).ConfigureAwait(false);
        var (start, covers) = DigestBuilder.ResolvePeriodStart(configuration, _calendars.For(configuration), last, end, LastBusinessDayEnd);
        var model = await _builder.BuildAsync(configuration, start, end, covers, cancellationToken).ConfigureAwait(false);

        // The caller may be a Viewer: exclusions are Admin configuration (the redacted GET /config keeps only their number),
        // and the API passes the detail level only – so no preview lists them.
        model = model with
        {
            Sections = [.. model.Sections.Select(s => s.Kind == DigestSectionKind.ActiveExceptions ? DigestSectionBuilder.ExceptionCount(s) : s)],
        };
        var recipientLevel = (configuration.Digest?.RecipientGroupIds ?? [])
            .Select(id => NotificationTargetResolver.EmailTarget(configuration, id))
            .FirstOrDefault(t => t is not null)?.DetailLevel ?? DetailLevel.Full;
        var level = callerLevel < recipientLevel ? callerLevel : recipientLevel;
        var email = EmailDigestFormatter.Format(model, NotificationContext.Create(configuration, level));
        return new DigestPreviewDto
        {
            PeriodStart = model.PeriodStart,
            PeriodEnd = model.PeriodEnd,
            Subject = email.Subject,
            Text = email.TextBody,
            Html = email.HtmlBody,
            EventCount = model.Counts.Total,
            NextScheduledAt = NextScheduledAt,
            LastSentAt = last,
        };
    }

    /// <inheritdoc/>
    public Task SendNowAsync(CancellationToken cancellationToken) =>
        SendAsync(_configProvider.Current.Configuration, _timeProvider.GetUtcNow(), cancellationToken);

    /// <summary>The due time of the next digest; computes the schedule at start-up (with the catch-up of a missed
    /// digest) and whenever time, days or zone change.</summary>
    private async Task<DateTimeOffset?> DueAsync(DigestSettings digest, TimeZoneInfo timeZone, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!digest.Enabled)
        {
            SetDue(null, null);
            return null;
        }

        var days = digest.Days ?? [];
        var key = string.Join(',', digest.Time.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), timeZone.Id,
            string.Join('|', days.Distinct().Order()));
        bool started;
        lock (_scheduleGate)
        {
            if (_scheduleKey == key)
            {
                return _due;
            }

            started = _started;
            _started = true;
        }

        DateTimeOffset? due = null;
        if (!started && Schedule.PreviousOccurrence(digest.Time, days, timeZone, now) is { } previous && now - previous < MaxCatchUpDelay
            && await _runs.GetLastPeriodEndAsync(cancellationToken).ConfigureAwait(false) is { } lastEnd && lastEnd < previous)
        {
            due = previous;     // missed during a downtime: send now
        }

        due ??= Schedule.NextOccurrence(digest.Time, days, timeZone, now);
        SetDue(due, key);
        return due;
    }

    private void SetDue(DateTimeOffset? due, string? key = null)
    {
        lock (_scheduleGate)
        {
            _due = due;
            if (key is not null || due is null)
            {
                _scheduleKey = key;
            }
        }
    }

    private async Task SendAsync(WatchdogConfiguration configuration, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var ids = new List<long>();
        try
        {
            var digest = configuration.Digest ?? new DigestSettings();
            var calendar = _calendars.For(configuration);
            var last = await _runs.GetLastPeriodEndAsync(cancellationToken).ConfigureAwait(false);
            var end = await _builder.ResolvePeriodEndAsync(now, last, cancellationToken).ConfigureAwait(false);
            var (start, covers) = DigestBuilder.ResolvePeriodStart(configuration, calendar, last, end, LastBusinessDayEnd);
            var model = await _builder.BuildAsync(configuration, start, end, covers, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<OutboxMessage> messages = !model.IsEmpty || digest.SendWhenEmpty ? DigestMessages.Create(model, configuration, now) : [];

            // From the first enqueue on, the digest is committed: a stop (cancellation) between two messages or before the
            // run is recorded would leave a partial digest that the start-up catch-up sends again.
            foreach (var message in messages)
            {
                ids.Add(await _outbox.EnqueueAsync(message, CancellationToken.None).ConfigureAwait(false));
            }

            await _runs.RecordAsync(start, end, model.Counts.Total, ids, CancellationToken.None).ConfigureAwait(false);
            if (calendar.IsBusinessDay(calendar.LocalDate(end)))
            {
                LastBusinessDayEnd = end;
            }

            LogSent(ids.Count, model.Counts.Total);
        }
        catch (Exception ex) when (ids.Count > 0 && ex is not OutOfMemoryException)
        {
            // The digest went out but its run is not (fully) recorded: the next digest starts at the previous end (it
            // repeats this period's content), which beats enqueueing this digest again every minute.
            SafeExceptionLog.LogError(_logger, ex, "digest run record");
        }
        finally
        {
            if (ids.Count > 0)
            {
                _signals.Raise(WatchdogSignal.OutboxEnqueued);
            }

            _sendGate.Release();
        }
    }

    /// <summary>The end of the last digest whose period ended on a business day (in memory; null after a restart).</summary>
    private DateTimeOffset? LastBusinessDayEnd
    {
        get
        {
            lock (_scheduleGate)
            {
                return _lastBusinessDayEnd;
            }
        }

        set
        {
            lock (_scheduleGate)
            {
                _lastBusinessDayEnd = value;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Digest enqueued: {Messages} messages, {Events} events in the period.")]
    private partial void LogSent(int messages, int events);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The digest scheduled at {ScheduledAt:O} was missed by more than 12 hours and is skipped.")]
    private partial void LogSkipped(DateTimeOffset scheduledAt);
}
