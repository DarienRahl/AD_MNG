using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;
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

    [Fact]
    public async Task Preview_ShowsOnlyTheNumberOfActiveExceptions_WhileTheSentDigestListsThem()
    {
        // GET /digest/preview is served to Viewers, who must not see exclusions (the redacted configuration hides them).
        var exclusion = new RuleExclusion
        {
            Id = "entra-sync",
            Actor = "S-1-5-21-1004336348-1177238915-682003330-7777",
            SubtreeDn = "OU=Ukryte konta,OU=Firma,DC=contoso,DC=com",
            Reason = "Konto synchronizacji Entra Connect – zgłoszenie ZGŁ-4711",
            CreatedBy = @"CONTOSO\adm.wójcik",
            CreatedAt = Now.AddDays(-2),
        };
        _harness.Config.Set(_harness.Current with { Rules = [NewAccounts with { Exclusions = [exclusion] }] });
        await _job.RunOnceAsync(Ct);

        foreach (var level in new[] { DetailLevel.Minimal, DetailLevel.Standard, DetailLevel.Full })
        {
            var preview = await _job.PreviewAsync(level, Ct);
            foreach (var secret in new[] { "7777", "Ukryte konta", "ZGŁ-4711", "adm.wójcik" })
            {
                Assert.DoesNotContain(secret, preview.Text, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, preview.Html, StringComparison.Ordinal);
            }

            Assert.Contains("Aktywne wyjątki", preview.Text, StringComparison.Ordinal);
            Assert.Contains("Liczba aktywnych wyjątków (szczegóły tylko w wysłanym raporcie): 1", preview.Text, StringComparison.Ordinal);
        }

        await _job.SendNowAsync(Ct);
        var sent = (await _harness.Store.LeaseDueAsync(_harness.Clock.GetUtcNow(), 10, TimeSpan.FromMinutes(5), Ct)).Single(m => m.ChannelType == DeliveryChannelType.Email);
        Assert.Contains("ZGŁ-4711", sent.Email!.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DigestStoppedWhileEnqueueing_IsCompleted_AndNotSentAgainAfterARestart()
    {
        // The service stops right after the first message of the digest was enqueued.
        await _harness.Store.RecordAsync(Now.AddDays(-1).AddHours(-2.5), Now.AddHours(-2.5), 0, [], Ct);     // today's 07:30 digest
        using var stopping = new CancellationTokenSource();
        var job = Job(_harness, outbox: new StoppingOutbox(_harness.Store, stopping));
        await job.RunOnceAsync(Ct);
        Assert.Equal(NextMorning, job.NextScheduledAt);
        _harness.Clock.SetUtcNow(NextMorning);

        try
        {
            await job.RunOnceAsync(stopping.Token);
        }
        catch (OperationCanceledException)
        {
            // the host is stopping
        }

        Assert.True(stopping.IsCancellationRequested);
        Assert.Equal(3, (await OutboxAsync()).Count);
        Assert.Equal(NextMorning, await _harness.Store.GetLastPeriodEndAsync(Ct));

        _harness.Clock.Advance(TimeSpan.FromMinutes(2));          // restart
        await Job(_harness).RunOnceAsync(Ct);

        Assert.Equal(3, (await OutboxAsync()).Count);
    }

    [Fact]
    public async Task DigestWhoseRunCouldNotBeRecorded_IsNotEnqueuedAgainEveryMinute()
    {
        var runs = new RecordingRuns(_harness.Store) { FailNextRecord = true };
        var job = Job(_harness, runs: runs);
        await job.RunOnceAsync(Ct);
        _harness.Clock.SetUtcNow(NextMorning);

        await job.RunOnceAsync(Ct);
        _harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await job.RunOnceAsync(Ct);
        _harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await job.RunOnceAsync(Ct);

        Assert.Equal(3, (await OutboxAsync()).Count);
        Assert.Equal(NextMorning.AddDays(1), job.NextScheduledAt);
    }

    [Fact]
    public async Task EventNotYetRuleEvaluated_AtDigestTime_IsListedInTheNextDigest()
    {
        await _job.RunOnceAsync(Ct);
        var late = await _harness.StoreAsync(Event(WatchEventKind.UserCreated, Severity.Low, name: "Łucja Późna", detectedAt: NextMorning.AddSeconds(-2)));
        _harness.Clock.SetUtcNow(NextMorning);

        await _job.RunOnceAsync(Ct);                              // the rule evaluation job has not seen the event yet

        Assert.Equal(NextMorning.AddSeconds(-2), await _harness.Store.GetLastPeriodEndAsync(Ct));
        await _harness.Store.CommitEvaluationAsync([new EventEvaluation { EventId = late.Id, Severity = Severity.Low, MatchedRules = [NewAccounts.Id] }], [], Ct);
        _harness.Clock.SetUtcNow(NextMorning.AddDays(1));
        await _job.RunOnceAsync(Ct);

        var digest = Assert.Single(await _harness.Store.GetMessagesForEventAsync(late.Id, Ct), m => m.ChannelType == DeliveryChannelType.Email);
        Assert.Contains("Łucja Późna", digest.Email!.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OldUnevaluatedEvents_DoNotHoldThePeriodBack()
    {
        await _job.RunOnceAsync(Ct);
        await _harness.StoreAsync(Event(WatchEventKind.UserCreated, Severity.Low, detectedAt: NextMorning.AddHours(-3)));
        _harness.Clock.SetUtcNow(NextMorning);

        await _job.RunOnceAsync(Ct);

        Assert.Equal(NextMorning, await _harness.Store.GetLastPeriodEndAsync(Ct));
    }

    [Fact]
    public async Task MondayDigest_AfterWeekendDigests_StartsWhereTheLateFridayDigestEnded()
    {
        _harness.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 16, 8, 0, 0, TimeSpan.Zero));     // Friday 10:00: catch-up after a downtime
        var runs = new RecordingRuns(_harness.Store);
        var job = Job(_harness, runs: runs);
        await job.SendNowAsync(Ct);
        foreach (var day in new[] { 17, 18, 19 })                                                  // Saturday, Sunday, Monday 07:30
        {
            _harness.Clock.SetUtcNow(new DateTimeOffset(2026, 10, day, 5, 30, 0, TimeSpan.Zero));
            await job.SendNowAsync(Ct);
        }

        Assert.Equal(new DateTimeOffset(2026, 10, 16, 8, 0, 0, TimeSpan.Zero), runs.Starts[^1]);

        _harness.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));       // Monday 14:00: send now
        await job.SendNowAsync(Ct);
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 5, 30, 0, TimeSpan.Zero), runs.Starts[^1]);
    }

    private static WatchdogConfiguration Configuration() => ConfigWith(NewAccounts) with
    {
        Digest = new DigestSettings { RecipientGroupIds = [AdminsGroup], ChannelIds = [TeamsChannel, SiemChannel] },
    };

    private static DigestJob Job(NotificationHarness harness, IOutboxStore? outbox = null, IDigestRunStore? runs = null) => new(
        new DigestBuilder(harness.Store, harness.Store, harness.Health, harness.Rollout, new FakeDigestReports(), harness.Calendars),
        runs ?? harness.Store, outbox ?? harness.Store, harness.Config, harness.Calendars, harness.Signals, harness.Clock, NullLogger<DigestJob>.Instance);

    /// <summary>The digest run store, recording the period starts and optionally failing one RecordAsync.</summary>
    private sealed class RecordingRuns(IDigestRunStore inner) : IDigestRunStore
    {
        public List<DateTimeOffset> Starts { get; } = [];

        public bool FailNextRecord { get; set; }

        public Task<DateTimeOffset?> GetLastPeriodEndAsync(CancellationToken cancellationToken) => inner.GetLastPeriodEndAsync(cancellationToken);

        public Task RecordAsync(DateTimeOffset periodStart, DateTimeOffset periodEnd, int eventCount, IReadOnlyList<long> outboxIds, CancellationToken cancellationToken)
        {
            if (FailNextRecord)
            {
                FailNextRecord = false;
                throw new InvalidOperationException("database is locked");
            }

            Starts.Add(periodStart);
            return inner.RecordAsync(periodStart, periodEnd, eventCount, outboxIds, cancellationToken);
        }
    }

    /// <summary>The outbox; the host starts stopping right after the first message was enqueued (honours the token).</summary>
    private sealed class StoppingOutbox(IOutboxStore inner, CancellationTokenSource stopping) : IOutboxStore
    {
        private int _enqueued;

        public async Task<long> EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();       // like SqliteDatabase.WriteAsync
            var id = await inner.EnqueueAsync(message, cancellationToken);
            if (Interlocked.Increment(ref _enqueued) == 1)
            {
                await stopping.CancelAsync();
            }

            return id;
        }

        public Task<IReadOnlyList<OutboxMessage>> LeaseDueAsync(DateTimeOffset now, int limit, TimeSpan lease, CancellationToken cancellationToken) =>
            inner.LeaseDueAsync(now, limit, lease, cancellationToken);

        public Task MarkSentAsync(long id, DateTimeOffset at, CancellationToken cancellationToken) => inner.MarkSentAsync(id, at, cancellationToken);

        public Task MarkRetryAsync(long id, DateTimeOffset nextAttemptAt, string errorCode, string? error, bool countAttempt, CancellationToken cancellationToken) =>
            inner.MarkRetryAsync(id, nextAttemptAt, errorCode, error, countAttempt, cancellationToken);

        public Task MarkDeadAsync(long id, string errorCode, string? error, CancellationToken cancellationToken) => inner.MarkDeadAsync(id, errorCode, error, cancellationToken);

        public Task<bool> RequeueAsync(long id, DateTimeOffset at, CancellationToken cancellationToken) => inner.RequeueAsync(id, at, cancellationToken);

        public Task<OutboxPage> QueryAsync(OutboxQuery query, CancellationToken cancellationToken) => inner.QueryAsync(query, cancellationToken);

        public Task<IReadOnlyList<EventDeliveryDto>> GetDeliveriesForEventAsync(long eventId, CancellationToken cancellationToken) =>
            inner.GetDeliveriesForEventAsync(eventId, cancellationToken);

        public Task<IReadOnlyList<OutboxMessage>> GetMessagesForEventAsync(long eventId, CancellationToken cancellationToken) =>
            inner.GetMessagesForEventAsync(eventId, cancellationToken);

        public Task<bool> WasNotifiedAboutObjectAsync(string ruleId, Guid objectGuid, DateTimeOffset since, CancellationToken cancellationToken) =>
            inner.WasNotifiedAboutObjectAsync(ruleId, objectGuid, since, cancellationToken);

        public Task<int> CountSentAsync(string channelId, string? ruleId, DateTimeOffset since, CancellationToken cancellationToken) =>
            inner.CountSentAsync(channelId, ruleId, since, cancellationToken);

        public Task<IReadOnlyDictionary<string, int>> CountPendingByChannelAsync(CancellationToken cancellationToken) => inner.CountPendingByChannelAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<OutboxItemDto>> OutboxAsync() => (await _harness.Store.QueryAsync(new OutboxQuery(), Ct)).Items;
}
