using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Notifications;
using AdMng.Watchdog.Core.Tests.Notifications.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using static AdMng.Watchdog.Core.Tests.Notifications.NotificationHarness;

namespace AdMng.Watchdog.Core.Tests.Notifications;

public sealed class DigestJobTests
{
    /// <summary>Thursday 15 October 2026, 07:30 in Warsaw.</summary>
    private static readonly DateTimeOffset NextMorning = new(2026, 10, 15, 5, 30, 0, TimeSpan.Zero);

    private static readonly RuleSettings NewAccounts = Rule("new-accounts", "Nowe konta", immediate: false);

    private readonly NotificationHarness _harness;
    private readonly DigestJob _job;

    public DigestJobTests()
    {
        _harness = new NotificationHarness(Configuration());
        _job = Job(_harness);
    }

    [Fact]
    public void Job_IsDigestWakingOnConfigurationAndDigestRequests()
    {
        Assert.Equal(("digest", TimeSpan.FromMinutes(1)), (_job.Name, _job.DefaultInterval));
        Assert.Equal([WatchdogSignal.ConfigChanged, WatchdogSignal.DigestRequested], _job.WakeSignals);
    }

    [Fact]
    public async Task FirstRun_SchedulesTheNextDigest_WithoutSendingAnything()
    {
        var result = await _job.RunOnceAsync(Ct);

        Assert.Equal(NextMorning, _job.NextScheduledAt);
        Assert.Equal(DigestJob.MaxWait, result.NextRunIn);
        Assert.Empty(await OutboxAsync());
    }

    [Fact]
    public async Task AtTheScheduledTime_TheDigestIsSentToEveryRecipientAndRecorded()
    {
        using var outboxWake = _harness.Signals.Subscribe([WatchdogSignal.OutboxEnqueued]);
        await _job.RunOnceAsync(Ct);
        await _harness.StoreAsync(Event(WatchEventKind.UserCreated, Severity.Low, detectedAt: Now.AddHours(2)) with { MatchedRules = [NewAccounts.Id] });
        _harness.Clock.SetUtcNow(NextMorning);

        var result = await _job.RunOnceAsync(Ct);

        var outbox = await OutboxAsync();
        Assert.False(result.Failed);
        Assert.Equal([DeliveryChannelType.Email, DeliveryChannelType.Teams, DeliveryChannelType.Webhook], outbox.Select(o => o.ChannelType).Order());
        Assert.All(outbox, o => Assert.Equal(DeliveryKind.Digest, o.Kind));
        Assert.Equal(NextMorning, await _harness.Store.GetLastPeriodEndAsync(Ct));
        Assert.Equal(NextMorning.AddDays(1), _job.NextScheduledAt);
        Assert.True(await outboxWake.WaitAsync(TimeSpan.Zero, Ct));
        Assert.Contains("1 zdarzenie", outbox.Single(o => o.ChannelType == DeliveryChannelType.Email).Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissedDigest_IsSentAtStartUp_WhenLessThan12HoursLate()
    {
        await _harness.Store.RecordAsync(Now.AddDays(-3), Now.AddDays(-1).AddHours(-2.5), 0, [], Ct);   // yesterday's 07:30 digest

        await _job.RunOnceAsync(Ct);

        Assert.Equal(3, (await OutboxAsync()).Count);
        Assert.Equal(Now, await _harness.Store.GetLastPeriodEndAsync(Ct));
        Assert.Equal(NextMorning, _job.NextScheduledAt);
    }

    [Fact]
    public async Task MissedDigest_MoreThan12HoursLate_IsSkipped()
    {
        _harness.Config.Set(_harness.Current with { Digest = _harness.Current.Digest with { Time = new TimeOnly(20, 0) } });
        await _harness.Store.RecordAsync(Now.AddDays(-3), Now.AddDays(-2), 0, [], Ct);

        await _job.RunOnceAsync(Ct);

        Assert.Empty(await OutboxAsync());
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 18, 0, 0, TimeSpan.Zero), _job.NextScheduledAt);
    }

    [Fact]
    public async Task FreshInstall_DoesNotSendACatchUpDigest()
    {
        await _job.RunOnceAsync(Ct);

        Assert.Empty(await OutboxAsync());
        Assert.Null(await _harness.Store.GetLastPeriodEndAsync(Ct));
    }

    [Fact]
    public async Task EmptyPeriod_WithoutSendWhenEmpty_IsRecordedButNotSent()
    {
        _harness.Config.Set(_harness.Current with { Digest = _harness.Current.Digest with { SendWhenEmpty = false } });
        await _job.RunOnceAsync(Ct);
        _harness.Clock.SetUtcNow(NextMorning);

        await _job.RunOnceAsync(Ct);

        Assert.Empty(await OutboxAsync());
        Assert.Equal(NextMorning, await _harness.Store.GetLastPeriodEndAsync(Ct));
    }

    [Fact]
    public async Task EmptyPeriod_IsSentAsHeartbeatByDefault()
    {
        await _job.RunOnceAsync(Ct);
        _harness.Clock.SetUtcNow(NextMorning);

        await _job.RunOnceAsync(Ct);

        Assert.Contains(await OutboxAsync(), o => o.Subject?.Contains("0 zdarzeń", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task DisabledDigest_HasNoSchedule()
    {
        _harness.Config.Set(_harness.Current with { Digest = _harness.Current.Digest with { Enabled = false } });

        var result = await _job.RunOnceAsync(Ct);

        Assert.Null(_job.NextScheduledAt);
        Assert.Equal(_job.DefaultInterval, result.NextRunIn);
    }

    [Fact]
    public async Task ChangedTime_MovesTheSchedule()
    {
        await _job.RunOnceAsync(Ct);
        _harness.Config.Set(_harness.Current with { Digest = _harness.Current.Digest with { Time = new TimeOnly(6, 0) } });

        await _job.RunOnceAsync(Ct);

        Assert.Equal(new DateTimeOffset(2026, 10, 15, 4, 0, 0, TimeSpan.Zero), _job.NextScheduledAt);
    }

    [Fact]
    public async Task SendNow_EnqueuesTheDigest_WithoutMovingTheSchedule()
    {
        await _job.RunOnceAsync(Ct);

        await _job.SendNowAsync(Ct);

        Assert.Equal(3, (await OutboxAsync()).Count);
        Assert.Equal(NextMorning, _job.NextScheduledAt);
        Assert.Equal(Now, await _harness.Store.GetLastPeriodEndAsync(Ct));
    }

    [Fact]
    public async Task Preview_RendersTheDigestAsItWouldBeSent_WithoutEnqueueing()
    {
        await _harness.StoreAsync(Event(WatchEventKind.UserCreated, Severity.Low, name: "Zofia Żak", detectedAt: Now.AddHours(-1)) with { MatchedRules = [NewAccounts.Id] });
        await _job.RunOnceAsync(Ct);

        var preview = await _job.PreviewAsync(Ct);

        Assert.Equal((Now.AddHours(-24), Now), (preview.PeriodStart, preview.PeriodEnd));
        Assert.Equal("[AD Watchdog] Raport dzienny contoso.com – 14.10.2026: 1 zdarzenie", preview.Subject);
        Assert.Contains("Zofia Żak", preview.Text, StringComparison.Ordinal);
        Assert.Contains("<html", preview.Html, StringComparison.Ordinal);
        Assert.Equal((1, NextMorning, (DateTimeOffset?)null), (preview.EventCount, preview.NextScheduledAt, preview.LastSentAt));
        Assert.Empty(await OutboxAsync());
    }

    private static WatchdogConfiguration Configuration() => ConfigWith(NewAccounts) with
    {
        Digest = new DigestSettings { RecipientGroupIds = [AdminsGroup], ChannelIds = [TeamsChannel, SiemChannel] },
    };

    private static DigestJob Job(NotificationHarness harness) => new(
        new DigestBuilder(harness.Store, harness.Store, harness.Health, harness.Rollout, new FakeDigestReports(), harness.Calendars),
        harness.Store, harness.Store, harness.Config, harness.Calendars, harness.Signals, harness.Clock, NullLogger<DigestJob>.Instance);

    private async Task<IReadOnlyList<OutboxItemDto>> OutboxAsync() => (await _harness.Store.QueryAsync(new OutboxQuery(), Ct)).Items;
}
