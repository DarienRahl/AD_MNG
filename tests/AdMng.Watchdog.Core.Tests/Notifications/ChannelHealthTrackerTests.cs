using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Notifications;
using static AdMng.Watchdog.Core.Tests.Notifications.NotificationHarness;

namespace AdMng.Watchdog.Core.Tests.Notifications;

public sealed class ChannelHealthTrackerTests
{
    private readonly HealthRegistry _registry = new();
    private readonly ChannelHealthTracker _tracker;

    public ChannelHealthTrackerTests()
    {
        _tracker = new ChannelHealthTracker(_registry);
    }

    [Fact]
    public void TransientFailures_TurnWarningAtThree_AndASuccessResets()
    {
        Assert.False(_tracker.Failure(TeamsChannel, DeliveryChannelType.Teams, HealthState.Ok, "http.503", Now));
        Assert.False(_tracker.Failure(TeamsChannel, DeliveryChannelType.Teams, HealthState.Ok, "http.503", Now.AddSeconds(5)));
        Assert.Equal(HealthState.Ok, State());
        Assert.False(_tracker.Failure(TeamsChannel, DeliveryChannelType.Teams, HealthState.Ok, "http.502", Now.AddSeconds(35)));

        var warning = _registry.GetChannel(TeamsChannel)!;
        Assert.Equal((HealthState.Warning, 3, "http.502", Now.AddSeconds(35)), (warning.State, warning.ConsecutiveFailures, warning.LastErrorCode, warning.LastFailureAt));

        _tracker.Success(TeamsChannel, DeliveryChannelType.Teams, Now.AddMinutes(2));
        var ok = _registry.GetChannel(TeamsChannel)!;
        Assert.Equal((HealthState.Ok, 0, Now.AddMinutes(2)), (ok.State, ok.ConsecutiveFailures, ok.LastSuccessAt));
    }

    [Fact]
    public void ErrorLevelFailure_EntersErrorOnce_AndLastsAnHourWithoutNewErrors()
    {
        Assert.True(_tracker.Failure(TeamsChannel, DeliveryChannelType.Teams, HealthState.Error, "http.404", Now));
        Assert.False(_tracker.Failure(TeamsChannel, DeliveryChannelType.Teams, HealthState.Error, "http.404", Now.AddMinutes(30)));
        Assert.False(_tracker.Failure(TeamsChannel, DeliveryChannelType.Teams, HealthState.Warning, "http.413", Now.AddMinutes(45)));
        Assert.Equal(HealthState.Error, State());

        Assert.False(_tracker.Failure(TeamsChannel, DeliveryChannelType.Teams, HealthState.Warning, "http.413", Now.AddMinutes(91)));
        Assert.Equal(HealthState.Warning, State());
        Assert.True(_tracker.Failure(TeamsChannel, DeliveryChannelType.Teams, HealthState.Error, "auth", Now.AddMinutes(92)));
    }

    [Fact]
    public void PermanentFailure_IsAWarning()
    {
        Assert.False(_tracker.Failure("email", DeliveryChannelType.Email, HealthState.Warning, "smtp.5.1.1", Now));

        Assert.Equal(HealthState.Warning, _registry.GetChannel("email")!.State);
    }

    [Fact]
    public void Channels_AreTrackedSeparately()
    {
        _tracker.Failure(TeamsChannel, DeliveryChannelType.Teams, HealthState.Error, "http.404", Now);

        Assert.True(_tracker.Failure(SiemChannel, DeliveryChannelType.Webhook, HealthState.Error, "http.410", Now));
        Assert.Equal(2, _registry.Channels.Count);
    }

    [Fact]
    public void UpdatePending_ReportsKnownChannelsAndAddsWaitingOnesAsUnknown()
    {
        _tracker.Success("email", DeliveryChannelType.Email, Now);

        _tracker.UpdatePending(
            new Dictionary<string, int> { ["email"] = 4, [SiemChannel] = 2, ["usuniety"] = 1 },
            id => id == SiemChannel ? DeliveryChannelType.Webhook : null);

        Assert.Equal((HealthState.Ok, 4), (_registry.GetChannel("email")!.State, _registry.GetChannel("email")!.Pending));
        Assert.Equal((HealthState.Unknown, 2, DeliveryChannelType.Webhook),
            (_registry.GetChannel(SiemChannel)!.State, _registry.GetChannel(SiemChannel)!.Pending, _registry.GetChannel(SiemChannel)!.ChannelType));
        Assert.Null(_registry.GetChannel("usuniety"));

        _tracker.UpdatePending(new Dictionary<string, int>(), _ => null);
        Assert.Equal(0, _registry.GetChannel("email")!.Pending);
    }

    private HealthState State() => _registry.GetChannel(TeamsChannel)!.State;
}
