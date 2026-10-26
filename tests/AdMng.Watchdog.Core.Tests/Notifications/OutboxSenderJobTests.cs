using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Notifications;
using AdMng.Watchdog.Core.Tests.Notifications.Fakes;
using static AdMng.Watchdog.Core.Tests.Notifications.NotificationHarness;

namespace AdMng.Watchdog.Core.Tests.Notifications;

public sealed class OutboxSenderJobTests
{
    private readonly NotificationHarness _harness = new();
    private readonly FakeTransport _email = new(DeliveryChannelType.Email);
    private readonly FakeTransport _teams = new(DeliveryChannelType.Teams);
    private readonly FakeTransport _webhook = new(DeliveryChannelType.Webhook);
    private readonly CapturingLogger<OutboxSenderJob> _logger = new();
    private readonly OutboxSenderJob _job;

    public OutboxSenderJobTests()
    {
        _job = Job(_email, _teams, _webhook);
    }

    private DateTimeOffset Time => _harness.Clock.GetUtcNow();

    [Fact]
    public void Job_IsOutboxEveryFiveSecondsWakingOnEnqueuedMessages()
    {
        Assert.Equal(("outbox", TimeSpan.FromSeconds(5)), (_job.Name, _job.DefaultInterval));
        Assert.Equal([WatchdogSignal.OutboxEnqueued], _job.WakeSignals);
    }

    [Fact]
    public async Task Delivered_MarksSentAndTheChannelHealthy()
    {
        var id = await EnqueueAsync(DeliveryChannelType.Email);

        var result = await _job.RunOnceAsync(Ct);

        var item = await _harness.OutboxItemAsync(id);
        Assert.Null(result.NextRunIn);
        Assert.Equal((OutboxStatus.Sent, 1, Time), (item.Status, item.Attempts, item.SentAt));
        Assert.Single(_email.Sent);
        var channel = _harness.Health.GetChannel("email")!;
        Assert.Equal((HealthState.Ok, Time, 0, 0), (channel.State, channel.LastSuccessAt, channel.ConsecutiveFailures, channel.Pending));
    }

    [Fact]
    public async Task Deferred_RetriesAfterRetryAfterWithoutCountingAnAttempt()
    {
        var id = await EnqueueAsync(DeliveryChannelType.Teams, TeamsChannel);
        _teams.Answer(new DeliveryResult { Outcome = DeliveryOutcome.Deferred, ErrorCode = "ratelimit", RetryAfter = TimeSpan.FromSeconds(30) });

        await _job.RunOnceAsync(Ct);

        var item = await _harness.OutboxItemAsync(id);
        Assert.Equal((OutboxStatus.Retrying, 0, Time.AddSeconds(30), "ratelimit"), (item.Status, item.Attempts, item.NextAttemptAt, item.LastErrorCode));
        Assert.Null(_harness.Health.GetChannel(TeamsChannel)?.LastFailureAt);
    }

    [Fact]
    public async Task Transient_FollowsTheRetryPolicyAndHonoursALongerRetryAfter()
    {
        var first = await EnqueueAsync(DeliveryChannelType.Email);
        var second = await EnqueueAsync(DeliveryChannelType.Email);
        _email.Answer(new DeliveryResult { Outcome = DeliveryOutcome.Transient, ErrorCode = "smtp.451" });
        _email.Answer(new DeliveryResult { Outcome = DeliveryOutcome.Transient, ErrorCode = "smtp.421", RetryAfter = TimeSpan.FromMinutes(10) });

        await _job.RunOnceAsync(Ct);

        var a = await _harness.OutboxItemAsync(first);
        var b = await _harness.OutboxItemAsync(second);
        Assert.Equal((OutboxStatus.Retrying, 1, Time.AddSeconds(30), "smtp.451"), (a.Status, a.Attempts, a.NextAttemptAt, a.LastErrorCode));
        Assert.Equal((OutboxStatus.Retrying, 1, Time.AddMinutes(10)), (b.Status, b.Attempts, b.NextAttemptAt));
        var channel = _harness.Health.GetChannel("email")!;
        Assert.Equal((HealthState.Ok, 2, "smtp.421"), (channel.State, channel.ConsecutiveFailures, channel.LastErrorCode));
    }

    [Fact]
    public async Task ThreeTransientFailuresInARow_TurnTheChannelToWarning()
    {
        for (var i = 0; i < 3; i++)
        {
            await EnqueueAsync(DeliveryChannelType.Webhook, SiemChannel);
        }

        _webhook.Default = (_, _) => Task.FromResult(new DeliveryResult { Outcome = DeliveryOutcome.Transient, ErrorCode = "http.503" });

        await _job.RunOnceAsync(Ct);

        Assert.Equal(HealthState.Warning, _harness.Health.GetChannel(SiemChannel)!.State);
    }

    [Fact]
    public async Task ExhaustedRetries_AreDead_AndReportTheFailingChannelOnce()
    {
        using var eventsStored = _harness.Signals.Subscribe([WatchdogSignal.EventsStored]);
        var id = await EnqueueAsync(DeliveryChannelType.Email);
        _email.Default = (_, _) => Task.FromResult(new DeliveryResult { Outcome = DeliveryOutcome.Transient, ErrorCode = "socket" });

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            await _job.RunOnceAsync(Ct);
            _harness.Clock.Advance(TimeSpan.FromHours(9));
        }

        var item = await _harness.OutboxItemAsync(id);
        Assert.Equal((OutboxStatus.Dead, 10), (item.Status, item.Attempts));
        Assert.Equal(10, _email.Sent.Count);
        Assert.Equal(HealthState.Error, _harness.Health.GetChannel("email")!.State);
        var health = Assert.Single(await HealthEventsAsync());
        Assert.Equal((WatchEventKind.HealthDeliveryFailing, Severity.High, DetectionSource.Health, WatchObjectKind.Watchdog),
            (health.Kind, health.Severity, health.Source, health.Object.Kind));
        Assert.Equal(["delivery.failing", "email", "Email", "socket"], health.Changes.Select(c => c.NewValues.Single()));
        Assert.True(await eventsStored.WaitAsync(TimeSpan.Zero, Ct));
    }

    [Fact]
    public async Task ExhaustedRetries_AfterAPartialDelivery_AreDeadWithAWarningButNoHealthEvent()
    {
        // One mailbox of the group answers 452 4.2.2 for days while the others got the alert: the e-mail channel works.
        var id = await EnqueueAsync(DeliveryChannelType.Email);
        _email.Default = (_, _) => Task.FromResult(new DeliveryResult { Outcome = DeliveryOutcome.Transient, ErrorCode = "partial.smtp.452.4.2.2" });

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            await _job.RunOnceAsync(Ct);
            _harness.Clock.Advance(TimeSpan.FromHours(9));
        }

        var item = await _harness.OutboxItemAsync(id);
        Assert.Equal((OutboxStatus.Dead, 10, "partial.smtp.452.4.2.2"), (item.Status, item.Attempts, item.LastErrorCode));
        Assert.Equal(HealthState.Warning, _harness.Health.GetChannel("email")!.State);
        Assert.Empty(await HealthEventsAsync());
    }

    [Fact]
    public async Task Permanent_IsDeadWithAWarningButNoHealthEvent()
    {
        var id = await EnqueueAsync(DeliveryChannelType.Email);
        _email.Answer(new DeliveryResult { Outcome = DeliveryOutcome.Permanent, ErrorCode = "smtp.5.1.1" });

        await _job.RunOnceAsync(Ct);

        var item = await _harness.OutboxItemAsync(id);
        Assert.Equal((OutboxStatus.Dead, 1, "smtp.5.1.1"), (item.Status, item.Attempts, item.LastErrorCode));
        Assert.Equal(HealthState.Warning, _harness.Health.GetChannel("email")!.State);
        Assert.Empty(await HealthEventsAsync());
        Assert.Contains(_logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.Contains("smtp.5.1.1", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(DeliveryOutcome.ChannelBroken, "http.404")]
    [InlineData(DeliveryOutcome.AuthenticationFailed, "auth")]
    public async Task BrokenChannel_RetriesHourlyForADay_ReportingOnce(DeliveryOutcome outcome, string errorCode)
    {
        var old = await EnqueueAsync(DeliveryChannelType.Teams, TeamsChannel, createdAt: Time.AddHours(-25));
        var fresh = await EnqueueAsync(DeliveryChannelType.Teams, TeamsChannel);
        _teams.Default = (_, _) => Task.FromResult(new DeliveryResult { Outcome = outcome, ErrorCode = errorCode });

        await _job.RunOnceAsync(Ct);

        Assert.Equal((OutboxStatus.Dead, 1), Status(await _harness.OutboxItemAsync(old)));
        var retrying = await _harness.OutboxItemAsync(fresh);
        // Broken-channel retries do not use up the transient retry budget (only the 24 h age limit ends them).
        Assert.Equal((OutboxStatus.Retrying, 0, Time.AddHours(1), errorCode), (retrying.Status, retrying.Attempts, retrying.NextAttemptAt, retrying.LastErrorCode));
        Assert.Equal(HealthState.Error, _harness.Health.GetChannel(TeamsChannel)!.State);
        var health = Assert.Single(await HealthEventsAsync());
        Assert.Equal(TeamsChannel, health.Changes.Single(c => c.Attribute == "channel").NewValues.Single());

        _harness.Clock.Advance(TimeSpan.FromHours(1));
        await _job.RunOnceAsync(Ct);
        Assert.Single(await HealthEventsAsync());
    }

    [Fact]
    public async Task ChannelRecovery_AllowsTheNextFailureToBeReportedAgain()
    {
        await EnqueueAsync(DeliveryChannelType.Teams, TeamsChannel);
        _teams.Answer(new DeliveryResult { Outcome = DeliveryOutcome.ChannelBroken, ErrorCode = "http.404" });
        await _job.RunOnceAsync(Ct);
        _harness.Clock.Advance(TimeSpan.FromHours(1));
        await _job.RunOnceAsync(Ct);                              // delivered: Ok again
        await EnqueueAsync(DeliveryChannelType.Teams, TeamsChannel);
        _teams.Answer(new DeliveryResult { Outcome = DeliveryOutcome.AuthenticationFailed, ErrorCode = "auth" });

        await _job.RunOnceAsync(Ct);

        Assert.Equal(2, (await HealthEventsAsync()).Count);
    }

    [Fact]
    public async Task MissingTransport_CountsAsABrokenChannel()
    {
        var job = Job(_email);
        var id = await EnqueueAsync(DeliveryChannelType.Webhook, SiemChannel);

        await job.RunOnceAsync(Ct);

        var item = await _harness.OutboxItemAsync(id);
        Assert.Equal((OutboxStatus.Retrying, "transport.missing"), (item.Status, item.LastErrorCode));
        Assert.Equal(HealthState.Error, _harness.Health.GetChannel(SiemChannel)!.State);
    }

    [Fact]
    public async Task TransportException_IsTransient_AndLoggedWithoutItsMessage()
    {
        var id = await EnqueueAsync(DeliveryChannelType.Email);
        _email.Answer((_, _) => throw new InvalidOperationException("Odbiorca łucja.nowak@contoso.test odrzucony"));

        var result = await _job.RunOnceAsync(Ct);

        var item = await _harness.OutboxItemAsync(id);
        Assert.False(result.Failed);
        Assert.Equal((OutboxStatus.Retrying, 1, "InvalidOperationException"), (item.Status, item.Attempts, item.LastErrorCode));
        Assert.NotEmpty(_logger.Entries);
        Assert.All(_logger.Entries, e =>
        {
            Assert.DoesNotContain("łucja", e.Message, StringComparison.Ordinal);
            Assert.False(e.HadException);
        });
    }

    [Fact]
    public async Task Channels_AreSentInParallel_ButEachChannelSequentially()
    {
        var emailStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var teamsActive = 0;
        var teamsMaxActive = 0;
        _teams.Default = async (_, ct) =>
        {
            var active = Interlocked.Increment(ref teamsActive);
            InterlockedMax(ref teamsMaxActive, active);
            await emailStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            Interlocked.Decrement(ref teamsActive);
            return DeliveryResult.Delivered;
        };
        _email.Default = (_, _) =>
        {
            emailStarted.TrySetResult();
            return Task.FromResult(DeliveryResult.Delivered);
        };
        var ids = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add(await EnqueueAsync(DeliveryChannelType.Teams, TeamsChannel));
        }

        ids.Add(await EnqueueAsync(DeliveryChannelType.Email));

        await _job.RunOnceAsync(Ct);

        Assert.Equal(1, teamsMaxActive);
        foreach (var id in ids)
        {
            Assert.Equal(OutboxStatus.Sent, (await _harness.OutboxItemAsync(id)).Status);
        }

        Assert.Equal(ids.Take(3), _teams.Sent.Select(m => m.Id));
    }

    [Fact]
    public async Task FullBatch_RunsAgainImmediately_AndPendingCountsAreReported()
    {
        for (var i = 0; i < OutboxSenderJob.BatchSize + 2; i++)
        {
            await EnqueueAsync(DeliveryChannelType.Email);
        }

        _email.Default = (_, _) => Task.FromResult(new DeliveryResult { Outcome = DeliveryOutcome.Deferred, RetryAfter = TimeSpan.FromMinutes(1) });

        var result = await _job.RunOnceAsync(Ct);

        Assert.Equal(TimeSpan.Zero, result.NextRunIn);
        Assert.Equal(OutboxSenderJob.BatchSize + 2, _harness.Health.GetChannel("email")!.Pending);
    }

    [Fact]
    public async Task BrokenChannelRetries_DoNotUseUpTheTransientRetryBudget()
    {
        // A Teams flow switched off for ten hours: hourly ChannelBroken answers, then one 429 when the flow is back.
        var id = await EnqueueAsync(DeliveryChannelType.Teams, TeamsChannel);
        for (var hour = 0; hour < 10; hour++)
        {
            _teams.Answer(new DeliveryResult { Outcome = DeliveryOutcome.ChannelBroken, ErrorCode = "teams.workflowDisabled" });
            await _job.RunOnceAsync(Ct);
            _harness.Clock.Advance(OutboxSenderJob.BrokenChannelRetry);
        }

        _teams.Answer(new DeliveryResult { Outcome = DeliveryOutcome.Transient, ErrorCode = "http.429" });
        await _job.RunOnceAsync(Ct);

        var item = await _harness.OutboxItemAsync(id);
        Assert.Equal((OutboxStatus.Retrying, 1, Time.AddSeconds(5), "http.429"), (item.Status, item.Attempts, item.NextAttemptAt, item.LastErrorCode));
        Assert.Equal(11, _teams.Sent.Count);
    }

    [Fact]
    public async Task DisabledChannel_GivesUpItsQueuedMessages_WithoutAFalseChannelAlert()
    {
        var id = await EnqueueAsync(DeliveryChannelType.Teams, TeamsChannel);
        _harness.Config.Set(_harness.Current with
        {
            Channels = [.. _harness.Current.Channels.Select(c => c.Id == TeamsChannel ? c with { Enabled = false } : c)],
        });

        await _job.RunOnceAsync(Ct);

        var item = await _harness.OutboxItemAsync(id);
        Assert.Equal((OutboxStatus.Dead, "channel.disabled"), (item.Status, item.LastErrorCode));
        Assert.Empty(_teams.Sent);
        Assert.NotEqual(HealthState.Error, _harness.Health.GetChannel(TeamsChannel)?.State);
        Assert.Empty(await HealthEventsAsync());
    }

    [Fact]
    public async Task RemovedChannel_AndDisabledSmtp_GiveUpTheirQueuedMessages_WithoutAFalseChannelAlert()
    {
        var webhook = await EnqueueAsync(DeliveryChannelType.Webhook, SiemChannel);
        var email = await EnqueueAsync(DeliveryChannelType.Email);
        _harness.Config.Set(_harness.Current with
        {
            Smtp = _harness.Current.Smtp with { Enabled = false },
            Channels = [.. _harness.Current.Channels.Where(c => c.Id != SiemChannel)],
        });

        await _job.RunOnceAsync(Ct);

        Assert.Equal((OutboxStatus.Dead, "channel.unknown"), StatusAndCode(await _harness.OutboxItemAsync(webhook)));
        Assert.Equal((OutboxStatus.Dead, "smtp.disabled"), StatusAndCode(await _harness.OutboxItemAsync(email)));
        Assert.Empty(_webhook.Sent);
        Assert.Empty(_email.Sent);
        Assert.Null(_harness.Health.GetChannel(SiemChannel)?.LastFailureAt);
        Assert.Null(_harness.Health.GetChannel("email")?.LastFailureAt);
        Assert.Empty(await HealthEventsAsync());
    }

    [Fact]
    public async Task ChannelDisabledWhileSending_IsGivenUpWithoutAFalseChannelAlert()
    {
        var id = await EnqueueAsync(DeliveryChannelType.Teams, TeamsChannel);
        _teams.Answer(new DeliveryResult { Outcome = DeliveryOutcome.ChannelBroken, ErrorCode = "channel.disabled" });

        await _job.RunOnceAsync(Ct);

        Assert.Equal((OutboxStatus.Dead, "channel.disabled"), StatusAndCode(await _harness.OutboxItemAsync(id)));
        Assert.Null(_harness.Health.GetChannel(TeamsChannel)?.LastFailureAt);
        Assert.Empty(await HealthEventsAsync());
    }

    [Fact]
    public async Task Heartbeat_IsNotRetriedPastItsInterval()
    {
        // SIEM heartbeat every 15 minutes; the webhook schedule would retry it after 5 s, 5 min, 30 min, … for days.
        var id = await EnqueueAsync(DeliveryChannelType.Webhook, SiemChannel, kind: DeliveryKind.Health);
        _webhook.Default = (_, _) => Task.FromResult(new DeliveryResult { Outcome = DeliveryOutcome.Transient, ErrorCode = "http.503" });

        await _job.RunOnceAsync(Ct);
        Assert.Equal((OutboxStatus.Retrying, Time.AddSeconds(5)), ((await _harness.OutboxItemAsync(id)).Status, (await _harness.OutboxItemAsync(id)).NextAttemptAt));
        _harness.Clock.Advance(TimeSpan.FromSeconds(5));
        await _job.RunOnceAsync(Ct);
        Assert.Equal((OutboxStatus.Retrying, Time.AddMinutes(5)), ((await _harness.OutboxItemAsync(id)).Status, (await _harness.OutboxItemAsync(id)).NextAttemptAt));
        _harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await _job.RunOnceAsync(Ct);                              // the next step (30 min) is after the next heartbeat

        var item = await _harness.OutboxItemAsync(id);
        Assert.Equal((OutboxStatus.Dead, "heartbeat.superseded"), StatusAndCode(item));
        Assert.Equal(3, _webhook.Sent.Count);
        Assert.Equal((HealthState.Warning, 3, "http.503"), Channel(SiemChannel));
        Assert.Empty(await HealthEventsAsync());
    }

    [Fact]
    public async Task StaleHeartbeat_IsNotSentAfterADowntime()
    {
        var stale = await EnqueueAsync(DeliveryChannelType.Webhook, SiemChannel, createdAt: Time.AddMinutes(-20), kind: DeliveryKind.Health);
        var fresh = await EnqueueAsync(DeliveryChannelType.Webhook, SiemChannel, createdAt: Time.AddMinutes(-1), kind: DeliveryKind.Health);

        await _job.RunOnceAsync(Ct);

        Assert.Equal((OutboxStatus.Dead, "heartbeat.superseded"), StatusAndCode(await _harness.OutboxItemAsync(stale)));
        Assert.Equal(OutboxStatus.Sent, (await _harness.OutboxItemAsync(fresh)).Status);
        Assert.Equal([fresh], _webhook.Sent.Select(m => m.Id));
        Assert.Equal(HealthState.Ok, _harness.Health.GetChannel(SiemChannel)!.State);
    }

    [Fact]
    public async Task RateLimitedHeartbeat_IsDroppedOnceTheNextOneIsDue()
    {
        var id = await EnqueueAsync(DeliveryChannelType.Webhook, SiemChannel, createdAt: Time.AddMinutes(-14), kind: DeliveryKind.Health);
        _webhook.Answer(new DeliveryResult { Outcome = DeliveryOutcome.Deferred, ErrorCode = "rateLimit", RetryAfter = TimeSpan.FromMinutes(2) });

        await _job.RunOnceAsync(Ct);

        Assert.Equal((OutboxStatus.Dead, "heartbeat.superseded"), StatusAndCode(await _harness.OutboxItemAsync(id)));
        Assert.Null(_harness.Health.GetChannel(SiemChannel)?.LastFailureAt);
    }

    [Fact]
    public async Task ChannelError_DecaysWithoutTraffic_SoTheNextOutageIsReportedAgain()
    {
        await EnqueueAsync(DeliveryChannelType.Teams, TeamsChannel, createdAt: Time.AddHours(-25));
        _teams.Answer(new DeliveryResult { Outcome = DeliveryOutcome.ChannelBroken, ErrorCode = "http.404" });
        await _job.RunOnceAsync(Ct);                              // dead letter: Error, reported once
        Assert.Equal(HealthState.Error, _harness.Health.GetChannel(TeamsChannel)!.State);

        _harness.Clock.Advance(TimeSpan.FromDays(3));
        await _job.RunOnceAsync(Ct);                              // nothing to send – the old Error expires
        Assert.Equal(HealthState.Warning, _harness.Health.GetChannel(TeamsChannel)!.State);

        await EnqueueAsync(DeliveryChannelType.Teams, TeamsChannel);
        _teams.Answer(new DeliveryResult { Outcome = DeliveryOutcome.ChannelBroken, ErrorCode = "http.410" });
        await _job.RunOnceAsync(Ct);

        Assert.Equal(HealthState.Error, _harness.Health.GetChannel(TeamsChannel)!.State);
        Assert.Equal(["http.404", "http.410"], (await HealthEventsAsync()).Select(e => e.Changes.Single(c => c.Attribute == "errorCode").NewValues.Single()));
    }

    private OutboxSenderJob Job(params FakeTransport[] transports) =>
        new(_harness.Store, transports, new RetryPolicy(new FixedRandom()), new ChannelHealthTracker(_harness.Health),
            new DeliveryFailureReporter(_harness.Store, _harness.Signals), _harness.Config, _harness.Clock, _logger);

    private (HealthState, int, string?) Channel(string channelId)
    {
        var channel = _harness.Health.GetChannel(channelId)!;
        return (channel.State, channel.ConsecutiveFailures, channel.LastErrorCode);
    }

    [Fact]
    public async Task UserMessage_RejectedPermanently_IsDeadWithoutTouchingTheChannelHealth()
    {
        EnableUserMessages();
        var id = await EnqueueAsync(DeliveryChannelType.Email, kind: DeliveryKind.UserMessage);
        _email.Answer(new DeliveryResult { Outcome = DeliveryOutcome.Permanent, ErrorCode = "smtp.550.5.1.1" });

        await _job.RunOnceAsync(Ct);

        Assert.Equal((OutboxStatus.Dead, "smtp.550.5.1.1"), StatusAndCode(await _harness.OutboxItemAsync(id)));
        Assert.Null(_harness.Health.GetChannel("email")?.LastFailureAt);
        Assert.Empty(await HealthEventsAsync());
    }

    [Theory]
    [InlineData(DeliveryOutcome.ChannelBroken, "tls")]
    [InlineData(DeliveryOutcome.AuthenticationFailed, "auth")]
    public async Task UserMessage_OnABrokenChannel_IsDeadAtOnce_AndTheChannelReportsTheFailure(DeliveryOutcome outcome, string errorCode)
    {
        // AD Manager was told the run failed – the message is not retried hourly for a day behind the operator's back.
        EnableUserMessages();
        var id = await EnqueueAsync(DeliveryChannelType.Email, kind: DeliveryKind.UserMessage);
        _email.Answer(new DeliveryResult { Outcome = outcome, ErrorCode = errorCode });

        await _job.RunOnceAsync(Ct);

        Assert.Equal((OutboxStatus.Dead, errorCode), StatusAndCode(await _harness.OutboxItemAsync(id)));
        Assert.Equal(HealthState.Error, _harness.Health.GetChannel("email")!.State);
        Assert.Single(await HealthEventsAsync());
    }

    [Fact]
    public async Task UserMessages_AreGivenUp_WhenTheFeatureIsSwitchedOff_WithoutAFalseChannelAlert()
    {
        var id = await EnqueueAsync(DeliveryChannelType.Email, kind: DeliveryKind.UserMessage);
        var alert = await EnqueueAsync(DeliveryChannelType.Email);

        await _job.RunOnceAsync(Ct);

        Assert.Equal((OutboxStatus.Dead, OutboxSenderJob.UserMessagesDisabledCode), StatusAndCode(await _harness.OutboxItemAsync(id)));
        Assert.Equal(OutboxStatus.Sent, (await _harness.OutboxItemAsync(alert)).Status);
        Assert.Equal(DeliveryKind.Alert, Assert.Single(_email.Sent).Kind);
        Assert.Empty(await HealthEventsAsync());
    }

    [Fact]
    public async Task Alerts_AreSentBeforeTheUserMessagesOfTheirChannel()
    {
        EnableUserMessages();
        var first = await EnqueueAsync(DeliveryChannelType.Email, kind: DeliveryKind.UserMessage);
        var second = await EnqueueAsync(DeliveryChannelType.Email, kind: DeliveryKind.UserMessage);
        var alert = await EnqueueAsync(DeliveryChannelType.Email);

        await _job.RunOnceAsync(Ct);

        Assert.Equal([alert, first, second], _email.Sent.Select(m => m.Id));
    }

    private void EnableUserMessages() =>
        _harness.Config.Set(_harness.Current with { UserMessages = _harness.Current.UserMessages with { Enabled = true } });

    private async Task<long> EnqueueAsync(DeliveryChannelType type, string channelId = "email", DateTimeOffset? createdAt = null, DeliveryKind kind = DeliveryKind.Alert)
    {
        var at = createdAt ?? Time;
        return await _harness.Store.EnqueueAsync(new OutboxMessage
        {
            MessageId = Guid.CreateVersion7(at).ToString("N"),
            ChannelType = type,
            ChannelId = channelId,
            Kind = kind,
            Recipients = type == DeliveryChannelType.Email ? ["łucja.nowak@contoso.test"] : [],
            Email = type == DeliveryChannelType.Email
                ? new RenderedEmail { Subject = "[AD Watchdog] Nowe konto: Zażółć Gęślą", HtmlBody = "<p>Zażółć gęślą jaźń</p>", TextBody = "Zażółć gęślą jaźń" }
                : null,
            Http = type == DeliveryChannelType.Email ? null : new RenderedHttpMessage { Body = "{\"type\":\"ad.user.created\"}"u8.ToArray() },
            Severity = Severity.High,
            CreatedAt = at,
            NextAttemptAt = at,
        }, Ct);
    }

    private async Task<IReadOnlyList<WatchEvent>> HealthEventsAsync()
    {
        var events = new List<WatchEvent>();
        await foreach (var e in _harness.Store.GetRangeAsync(Now.AddDays(-30), Time.AddDays(30), Ct))
        {
            events.Add(e);
        }

        return events;
    }

    private static (OutboxStatus, int) Status(OutboxItemDto item) => (item.Status, item.Attempts);

    private static (OutboxStatus, string?) StatusAndCode(OutboxItemDto item) => (item.Status, item.LastErrorCode);

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
