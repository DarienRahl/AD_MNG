using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Rules;

/// <summary>Pending aggregation buckets and sent messages of one notification target in the rule preview
/// (<see cref="PreviewMessageEstimator"/>).</summary>
/// <param name="perEvent">Webhook target: one message per event instead of one aggregated bucket.</param>
/// <param name="hourlyCap">Messages per rule and target per hour (Critical is exempt).</param>
internal sealed class PreviewTargetQueue(bool perEvent, int hourlyCap)
{
    private static readonly TimeSpan ThrottleDelay = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private readonly List<PreviewBucket> _pending = [];

    /// <summary>Send times of the flushed messages.</summary>
    public List<DateTimeOffset> Sent { get; } = [];

    /// <summary>Adds an item due at <paramref name="dueAt"/>: joins the open bucket (its due time becomes the earliest)
    /// or opens one.</summary>
    public void Add(DateTimeOffset dueAt, Severity severity)
    {
        if (!perEvent && _pending.Count > 0)
        {
            var open = _pending[0];
            open.DueAt = dueAt < open.DueAt ? dueAt : open.DueAt;
            open.Severity = severity > open.Severity ? severity : open.Severity;
            return;
        }

        _pending.Add(new PreviewBucket { DueAt = dueAt, Severity = severity });
    }

    /// <summary>Flushes every bucket due before <paramref name="limit"/>; a bucket below Critical whose hourly cap is
    /// reached is rescheduled by 15 minutes (13.3) and keeps accumulating.</summary>
    public void FlushBefore(DateTimeOffset limit)
    {
        while (_pending.Count > 0)
        {
            var next = _pending.MinBy(b => b.DueAt)!;
            if (next.DueAt >= limit)
            {
                return;
            }

            var dueAt = next.DueAt;
            var sentLastHour = Sent.Count(s => s > dueAt - Hour && s <= dueAt);
            if (next.Severity < Severity.Critical && sentLastHour >= hourlyCap)
            {
                next.DueAt += ThrottleDelay;
                continue;
            }

            Sent.Add(next.DueAt);
            _pending.Remove(next);
        }
    }
}
