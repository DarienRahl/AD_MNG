using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Service.Notifications;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Watchdog.Service.Tests.Notifications;

/// <summary>Token buckets replenished manually from the fake clock (spec 15.5).</summary>
public sealed class ChannelRateLimitersTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Email_AllowsTheLimitPerMinute_ThenDefersWithARetryHint()
    {
        using var limiters = new ChannelRateLimiters(_clock);

        for (var i = 0; i < 3; i++)
        {
            Assert.True(limiters.TryAcquireEmail(3).Acquired);
        }

        var denied = limiters.TryAcquireEmail(3);
        Assert.False(denied.Acquired);
        Assert.InRange(denied.RetryAfter, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void Email_RefillsOneTokenPerPeriodDividedByLimit()
    {
        using var limiters = new ChannelRateLimiters(_clock);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(limiters.TryAcquireEmail(3).Acquired);
        }

        _clock.Advance(TimeSpan.FromSeconds(19));
        Assert.False(limiters.TryAcquireEmail(3).Acquired);

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(limiters.TryAcquireEmail(3).Acquired);
        Assert.False(limiters.TryAcquireEmail(3).Acquired);

        _clock.Advance(TimeSpan.FromMinutes(5));
        for (var i = 0; i < 3; i++)
        {
            Assert.True(limiters.TryAcquireEmail(3).Acquired);
        }

        Assert.False(limiters.TryAcquireEmail(3).Acquired);   // never more than the bucket size
    }

    [Fact]
    public void Channel_ChainsPerSecondPerMinuteAndPerHour()
    {
        using var limiters = new ChannelRateLimiters(_clock);
        var settings = new RateLimitSettings { PerSecond = 1, PerMinute = 2, PerHour = 3 };

        Assert.True(limiters.TryAcquireChannel("teams-pomoc", settings).Acquired);
        Assert.False(limiters.TryAcquireChannel("teams-pomoc", settings).Acquired);   // per second

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(limiters.TryAcquireChannel("teams-pomoc", settings).Acquired);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(limiters.TryAcquireChannel("teams-pomoc", settings).Acquired);  // per minute

        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(limiters.TryAcquireChannel("teams-pomoc", settings).Acquired);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var denied = limiters.TryAcquireChannel("teams-pomoc", settings);
        Assert.False(denied.Acquired);                                                // per hour
        Assert.True(denied.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void Channels_HaveIndependentBuckets()
    {
        using var limiters = new ChannelRateLimiters(_clock);
        var settings = new RateLimitSettings { PerSecond = 1, PerMinute = 20, PerHour = 100 };

        Assert.True(limiters.TryAcquireChannel("teams-pomoc", settings).Acquired);
        Assert.True(limiters.TryAcquireChannel("webhook-siem", settings).Acquired);
        Assert.True(limiters.TryAcquireEmail(1).Acquired);
        Assert.False(limiters.TryAcquireChannel("teams-pomoc", settings).Acquired);
    }

    [Fact]
    public void ChangedSettings_RebuildTheBuckets()
    {
        using var limiters = new ChannelRateLimiters(_clock);

        Assert.True(limiters.TryAcquireEmail(1).Acquired);
        Assert.False(limiters.TryAcquireEmail(1).Acquired);

        Assert.True(limiters.TryAcquireEmail(2).Acquired);
    }
}
