using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Scans;

/// <summary>
/// The per-object threshold rule of section 10.14: an expired account is reported once (threshold 0); otherwise every
/// due threshold is recorded and only the smallest newly recorded one produces an event, so a downtime does not flood
/// stale thresholds.
/// </summary>
internal sealed class ExpiryEvaluator
{
    private readonly IExpiryNoticeStore _notices;
    private readonly ScanEventWriter _writer;

    public ExpiryEvaluator(IExpiryNoticeStore notices, ScanEventWriter writer)
    {
        _notices = notices;
        _writer = writer;
    }

    /// <summary>Records the due notices of one object and emits at most one event.</summary>
    /// <param name="context">The scan iteration.</param>
    /// <param name="target">The account (ObjectGuid required).</param>
    /// <param name="info">Kind, expiry moment, last valid day and days left (ThresholdDays is ignored).</param>
    /// <param name="rawValue">The raw FILETIME (dedup key).</param>
    /// <param name="thresholds">Configured thresholds in days.</param>
    /// <param name="calendar">Business-day calendar, or null when thresholds fire on calendar days.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task EvaluateAsync(ScanContext context, ObjectRef target, ExpiryInfo info, long rawValue, IReadOnlyList<int> thresholds,
        IBusinessDayCalendar? calendar, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(info);
        if (target.ObjectGuid is not { } guid)
        {
            return;
        }

        if (info.ExpiresAt <= context.Now)
        {
            if (info.Kind == ExpiryKind.Account && await RecordAsync(guid, info.Kind, rawValue, 0, context.Now, cancellationToken).ConfigureAwait(false))
            {
                await EmitAsync(context, target, WatchEventKind.AccountExpired, info with { ThresholdDays = 0 }, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        int? smallest = null;
        foreach (var threshold in ExpiryThresholds.Due(thresholds, info.LastValidDay, context.Today, calendar))
        {
            if (await RecordAsync(guid, info.Kind, rawValue, threshold, context.Now, cancellationToken).ConfigureAwait(false))
            {
                smallest = threshold;
            }
        }

        if (smallest is { } fired)
        {
            var kind = info.Kind == ExpiryKind.Account ? WatchEventKind.AccountExpiring : WatchEventKind.PasswordExpiring;
            await EmitAsync(context, target, kind, info with { ThresholdDays = fired }, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task<bool> RecordAsync(Guid guid, ExpiryKind kind, long raw, int threshold, DateTimeOffset now, CancellationToken cancellationToken) =>
        _notices.TryRecordAsync(new ExpiryNotice(guid, kind, raw, threshold, now), cancellationToken);

    private Task<WatchEvent?> EmitAsync(ScanContext context, ObjectRef target, WatchEventKind kind, ExpiryInfo info, CancellationToken cancellationToken) =>
        _writer.AppendAsync(context, new EventDraft { Kind = kind, Expiry = info }, target, DetectionSource.ExpiryScan, cancellationToken);
}
