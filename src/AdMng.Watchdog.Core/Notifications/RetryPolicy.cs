using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>
/// Retry schedules per channel type (section 15.2) with ±20 % jitter. A <c>Retry-After</c> longer than the step is
/// honoured up to one hour. After the last step the message is given up (Dead): e-mail after ~22 h, Teams after ~16 h,
/// webhooks after the ninth failure.
/// </summary>
/// <remarks>Production passes <see cref="Random.Shared"/>; tests pass a seeded or zero-jitter source (a
/// <see cref="Random"/> whose <see cref="Random.NextDouble"/> returns 0.5).</remarks>
public sealed class RetryPolicy
{
    /// <summary>Upper bound of an honoured <c>Retry-After</c>.</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(1);

    /// <summary>Relative jitter (±).</summary>
    public const double Jitter = 0.2;

    private static readonly TimeSpan[] Email =
    [
        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1),
        TimeSpan.FromHours(2), TimeSpan.FromHours(4), TimeSpan.FromHours(6), TimeSpan.FromHours(8),
    ];

    private static readonly TimeSpan[] Teams =
    [
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(4), TimeSpan.FromHours(8),
    ];

    private static readonly TimeSpan[] Webhook =
    [
        TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(5),
        TimeSpan.FromHours(10), TimeSpan.FromHours(14), TimeSpan.FromHours(20), TimeSpan.FromHours(24),
    ];

    private readonly Random _random;
    private readonly Lock _gate = new();

    public RetryPolicy(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        _random = random;
    }

    /// <summary>The un-jittered delays of a channel type (step n follows attempt n).</summary>
    public static IReadOnlyList<TimeSpan> Steps(DeliveryChannelType channelType) => channelType switch
    {
        DeliveryChannelType.Email => Email,
        DeliveryChannelType.Teams => Teams,
        _ => Webhook,
    };

    /// <summary>The delay before the next attempt after <paramref name="attempt"/> failed attempts (1 = the first send
    /// failed), or null when the message is to be given up.</summary>
    /// <param name="channelType">Channel type.</param>
    /// <param name="attempt">Number of attempts made so far (≥ 1).</param>
    /// <param name="retryAfter">The server's <c>Retry-After</c>, if any.</param>
    public TimeSpan? Next(DeliveryChannelType channelType, int attempt, TimeSpan? retryAfter = null)
    {
        var steps = Steps(channelType);
        if (attempt < 1 || attempt > steps.Count)
        {
            return attempt < 1 ? Jittered(steps[0]) : null;
        }

        var delay = Jittered(steps[attempt - 1]);
        if (retryAfter is { } serverDelay)
        {
            var honoured = serverDelay > MaxRetryAfter ? MaxRetryAfter : serverDelay;
            if (honoured > delay)
            {
                delay = honoured;
            }
        }

        return delay;
    }

    private TimeSpan Jittered(TimeSpan step)
    {
        double sample;
        lock (_gate)
        {
            sample = _random.NextDouble();
        }

        var factor = 1 + (((sample * 2) - 1) * Jitter);
        return TimeSpan.FromTicks((long)(step.Ticks * factor));
    }
}
