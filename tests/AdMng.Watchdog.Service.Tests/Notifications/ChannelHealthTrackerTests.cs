using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Notifications;
using AdMng.Watchdog.Service.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Watchdog.Service.Tests.Notifications;

/// <summary>Channel health after delivery attempts (spec 15.8).</summary>
public sealed class ChannelHealthTrackerTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.Zero));
    private readonly FakeHealthRegistry _registry = new();
    private readonly TestLog _log = new();
    private readonly ChannelHealthTracker _tracker;

    public ChannelHealthTrackerTests()
    {
        _tracker = new ChannelHealthTracker(_registry, _clock, _log.For<ChannelHealthTracker>());
    }

    [Fact]
    public void Success_IsOk()
    {
        _tracker.Record(DeliveryChannelType.Email, "email", DeliveryResult.Delivered);

        var health = _registry.Channel("email")!;
        Assert.Equal(HealthState.Ok, health.State);
        Assert.Equal(_clock.GetUtcNow(), health.LastSuccessAt);
        Assert.Equal(DeliveryChannelType.Email, health.ChannelType);
    }

    [Fact]
    public void ThreeTransientFailuresInARow_AreAWarning()
    {
        _tracker.Record(DeliveryChannelType.Teams, "teams-pomoc", DeliveryResult.Delivered);
        for (var i = 1; i <= 3; i++)
        {
            _tracker.Record(DeliveryChannelType.Teams, "teams-pomoc", Failure(DeliveryOutcome.Transient, "http.503"));
            Assert.Equal(i < 3 ? HealthState.Ok : HealthState.Warning, _registry.Channel("teams-pomoc")!.State);
        }

        Assert.Equal(3, _registry.Channel("teams-pomoc")!.ConsecutiveFailures);
        Assert.Equal("http.503", _registry.Channel("teams-pomoc")!.LastErrorCode);
    }

    [Fact]
    public void BrokenChannel_IsAnError_UntilTheNextSuccess_AndIsLoggedOnce()
    {
        _tracker.Record(DeliveryChannelType.Webhook, "siem", Failure(DeliveryOutcome.ChannelBroken, "http.404"));
        _tracker.Record(DeliveryChannelType.Webhook, "siem", Failure(DeliveryOutcome.Transient, "http.503"));
        Assert.Equal(HealthState.Error, _registry.Channel("siem")!.State);

        _tracker.Record(DeliveryChannelType.Webhook, "siem", DeliveryResult.Delivered);

        Assert.Equal(HealthState.Ok, _registry.Channel("siem")!.State);
        Assert.Equal(0, _registry.Channel("siem")!.ConsecutiveFailures);
        var entry = Assert.Single(_log.Entries, e => e.EventId.Id == EventLogIds.DeliveryFailing);
        Assert.Contains("siem", entry.Message, StringComparison.Ordinal);
        Assert.Contains("http.404", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthenticationFailure_IsAnError()
    {
        _tracker.Record(DeliveryChannelType.Email, "email", Failure(DeliveryOutcome.AuthenticationFailed, "smtp.535.5.7.139"));

        Assert.Equal(HealthState.Error, _registry.Channel("email")!.State);
    }

    [Fact]
    public void Deferrals_ChangeNothing()
    {
        _tracker.Record(DeliveryChannelType.Email, "email", new DeliveryResult { Outcome = DeliveryOutcome.Deferred, RetryAfter = TimeSpan.FromSeconds(3) });

        Assert.Null(_registry.Channel("email"));
    }

    private static DeliveryResult Failure(DeliveryOutcome outcome, string code) => new() { Outcome = outcome, ErrorCode = code };
}
