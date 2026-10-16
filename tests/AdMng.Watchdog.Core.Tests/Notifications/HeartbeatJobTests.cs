using System.Text.Json;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using static AdMng.Watchdog.Core.Tests.Notifications.NotificationHarness;

namespace AdMng.Watchdog.Core.Tests.Notifications;

public sealed class HeartbeatJobTests
{
    private readonly NotificationHarness _harness = new();
    private readonly HeartbeatJob _job;

    public HeartbeatJobTests()
    {
        _job = new HeartbeatJob(_harness.Store, _harness.Health, _harness.Config, _harness.Signals, _harness.Clock, NullLogger<HeartbeatJob>.Instance);
    }

    [Fact]
    public async Task FirstRun_EnqueuesAHeartbeatPerWebhookChannel_WithServiceStateOnly()
    {
        using var outboxWake = _harness.Signals.Subscribe([WatchdogSignal.OutboxEnqueued]);
        _harness.Health.ReportDirectory(new DirectoryStatusDto { State = HealthState.Ok, LastPollAt = Now.AddSeconds(-12), DcHostName = "dc01.contoso.com" });

        var result = await _job.RunOnceAsync(Ct);

        var item = Assert.Single((await _harness.Store.QueryAsync(new OutboxQuery(), Ct)).Items);
        Assert.Equal((DeliveryChannelType.Webhook, SiemChannel, DeliveryKind.Health), (item.ChannelType, item.ChannelId, item.Kind));
        Assert.Equal(TimeSpan.FromMinutes(1), result.NextRunIn);
        Assert.True(await outboxWake.WaitAsync(TimeSpan.Zero, Ct));

        var leased = Assert.Single(await _harness.Store.LeaseDueAsync(Now, 10, TimeSpan.FromMinutes(5), Ct));
        var root = JsonDocument.Parse(leased.Http!.Body).RootElement;
        var data = root.GetProperty("data");
        Assert.Equal("watchdog.heartbeat", root.GetProperty("type").GetString());
        Assert.Equal("AD Watchdog – Kraków", data.GetProperty("instance").GetString());
        Assert.Equal("Ok", data.GetProperty("directoryState").GetString());
        Assert.Equal("2026-10-14T07:59:48Z", data.GetProperty("lastPollAt").GetString());
        Assert.Equal(0, data.GetProperty("pendingOutbox").GetInt32());
        Assert.Equal(HeartbeatJob.ServiceVersion, data.GetProperty("serviceVersion").GetString());
        Assert.DoesNotContain("+", HeartbeatJob.ServiceVersion, StringComparison.Ordinal);
        Assert.DoesNotContain("dc01", JsonSerializer.Serialize(root), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NextHeartbeat_ComesAfterTheInterval()
    {
        await _job.RunOnceAsync(Ct);
        _harness.Clock.Advance(TimeSpan.FromMinutes(14));

        var waiting = await _job.RunOnceAsync(Ct);
        Assert.Single((await _harness.Store.QueryAsync(new OutboxQuery(), Ct)).Items);
        Assert.Equal(TimeSpan.FromMinutes(1), waiting.NextRunIn);

        _harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await _job.RunOnceAsync(Ct);
        Assert.Equal(2, (await _harness.Store.QueryAsync(new OutboxQuery(), Ct)).Items.Count);
    }

    [Fact]
    public async Task NextRun_IsTheEarliestDueHeartbeat()
    {
        _harness.Config.Set(_harness.Current with
        {
            Channels =
            [
                new ChannelSettings
                {
                    Id = "siem-dr", Type = ChannelType.Webhook, Name = "SIEM – ośrodek zapasowy",
                    Webhook = new WebhookChannelOptions { HeartbeatInterval = TimeSpan.FromSeconds(20) },
                },
            ],
        });

        Assert.Equal(TimeSpan.FromMinutes(1), (await _job.RunOnceAsync(Ct)).NextRunIn);    // clamped to the minimum interval
    }

    [Fact]
    public async Task TeamsDisabledOrUnconfiguredChannels_GetNoHeartbeat()
    {
        _harness.Config.Set(_harness.Current with
        {
            Channels =
            [
                new ChannelSettings { Id = TeamsChannel, Type = ChannelType.Teams, Name = "Teams – dział IT" },
                new ChannelSettings
                {
                    Id = "siem-off", Type = ChannelType.Webhook, Name = "SIEM wyłączony", Enabled = false,
                    Webhook = new WebhookChannelOptions { HeartbeatInterval = TimeSpan.FromMinutes(5) },
                },
                new ChannelSettings { Id = "hook", Type = ChannelType.Webhook, Name = "Bez pulsu", Webhook = new WebhookChannelOptions() },
            ],
        });

        var result = await _job.RunOnceAsync(Ct);

        Assert.Empty((await _harness.Store.QueryAsync(new OutboxQuery(), Ct)).Items);
        Assert.Equal(TimeSpan.FromMinutes(1), result.NextRunIn);
    }

    [Fact]
    public void Job_IsHeartbeatWakingOnConfigurationChanges()
    {
        Assert.Equal(("heartbeat", TimeSpan.FromMinutes(1)), (_job.Name, _job.DefaultInterval));
        Assert.Equal([WatchdogSignal.ConfigChanged], _job.WakeSignals);
    }
}
