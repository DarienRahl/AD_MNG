using System.Collections.Concurrent;
using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Service.Notifications;

/// <summary>
/// Send rate limits (spec 15.5): e-mail <c>MaxMessagesPerMinute</c>; per Teams/webhook channel three chained token
/// buckets (per second / minute / hour). Each bucket refills evenly on the injected <see cref="TimeProvider"/>
/// (<see cref="TimeTokenBucket"/>; deterministic under FakeTimeProvider – the framework's TokenBucketRateLimiter measures
/// Stopwatch time even with manual replenishment). A denial returns the time until the exhausted bucket refills, so the
/// outbox defers the message without counting an attempt. Buckets are rebuilt when a channel's settings change.
/// </summary>
public sealed class ChannelRateLimiters : IDisposable
{
    /// <summary>Key of the e-mail limiter.</summary>
    public const string EmailKey = "email";

    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, LimiterSet> _sets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public ChannelRateLimiters(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <summary>One e-mail message (SMTP profile limit).</summary>
    public RateLimitDecision TryAcquireEmail(int maxMessagesPerMinute) =>
        Acquire(EmailKey, [(Math.Max(1, maxMessagesPerMinute), TimeSpan.FromMinutes(1))]);

    /// <summary>One POST to a Teams/webhook channel.</summary>
    public RateLimitDecision TryAcquireChannel(string channelId, RateLimitSettings settings)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelId);
        ArgumentNullException.ThrowIfNull(settings);
        return Acquire(
            "channel:" + channelId,
            [
                (Math.Max(1, settings.PerSecond), TimeSpan.FromSeconds(1)),
                (Math.Max(1, settings.PerMinute), TimeSpan.FromMinutes(1)),
                (Math.Max(1, settings.PerHour), TimeSpan.FromHours(1)),
            ]);
    }

    /// <summary>Forgets every bucket.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _sets.Clear();
        }
    }

    private RateLimitDecision Acquire(string key, (int Limit, TimeSpan Period)[] limits)
    {
        LimiterSet set;
        lock (_gate)
        {
            if (!_sets.TryGetValue(key, out set!) || !set.Matches(limits))
            {
                set = new LimiterSet(limits, _timeProvider);
                _sets[key] = set;
            }
        }

        return set.TryAcquire();
    }

    /// <summary>The chained buckets of one channel. A token is taken only when every bucket has one (a denial by the
    /// hourly bucket does not burn the per-second token).</summary>
    private sealed class LimiterSet
    {
        private readonly (int Limit, TimeSpan Period)[] _limits;
        private readonly TimeProvider _timeProvider;
        private readonly TimeTokenBucket[] _buckets;
        private readonly Lock _gate = new();

        public LimiterSet((int Limit, TimeSpan Period)[] limits, TimeProvider timeProvider)
        {
            _limits = limits;
            _timeProvider = timeProvider;
            var now = timeProvider.GetUtcNow();
            _buckets = [.. limits.Select(l => new TimeTokenBucket(l.Limit, l.Period / l.Limit, now))];
        }

        public bool Matches((int Limit, TimeSpan Period)[] limits) => _limits.SequenceEqual(limits);

        public RateLimitDecision TryAcquire()
        {
            lock (_gate)
            {
                var now = _timeProvider.GetUtcNow();
                var wait = TimeSpan.Zero;
                foreach (var bucket in _buckets)
                {
                    var bucketWait = bucket.WaitFor(now);
                    wait = bucketWait > wait ? bucketWait : wait;
                }

                if (wait > TimeSpan.Zero)
                {
                    return new RateLimitDecision(false, wait);
                }

                foreach (var bucket in _buckets)
                {
                    bucket.Take();
                }

                return RateLimitDecision.Granted;
            }
        }
    }
}
