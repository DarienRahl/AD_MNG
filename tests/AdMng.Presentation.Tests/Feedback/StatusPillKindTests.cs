using System.Globalization;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Readiness;
using AdMng.Presentation.Watchdog;
using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Presentation.Tests.Feedback;

/// <summary>
/// The kinds of the status pill (track V, stage V2b-5b): what the rows of the readiness report and the health checks of the
/// watchdog say about themselves, as one of five kinds with the word of the row. The pill itself is a control of the
/// application (<c>StatusPillUiTests</c>); here is what the view models hand it.
/// </summary>
public class StatusPillKindTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    [Theory]
    [InlineData(ReadinessStatus.Ok, StatusPillKind.Ok)]
    [InlineData(ReadinessStatus.Warning, StatusPillKind.Attention)]
    [InlineData(ReadinessStatus.Error, StatusPillKind.Error)]
    [InlineData(ReadinessStatus.NotChecked, StatusPillKind.Unknown)]
    public void AReadinessStatus_IsOneKind_ForTheRowAndForItsArea(ReadinessStatus status, StatusPillKind kind)
    {
        Assert.Equal(kind, ReadinessText.PillKind(status));

        var localizer = new ResxLocalizer(Polish);
        var row = new ReadinessRowViewModel(new ReadinessResult(ReadinessCodes.ConnectionLogon, status, "Logowanie", "Co znaleziono."), localizer);
        Assert.Equal(kind, row.PillKind);

        // The word of the pill is the word of the status, in the language of the interface.
        Assert.Equal(localizer[ReadinessText.StatusKey(status)], row.StatusText);

        // An area has the kind of its worst row.
        var group = new ReadinessGroupViewModel(ReadinessArea.Connection, [row], localizer);
        Assert.Equal(kind, group.PillKind);
    }

    [Fact]
    public void EveryReadinessStatus_HasAKind_AndTheKindsDiffer()
    {
        var kinds = Enum.GetValues<ReadinessStatus>().Select(ReadinessText.PillKind).ToList();

        Assert.Equal(Enum.GetValues<ReadinessStatus>().Length, kinds.Distinct().Count());
    }

    [Theory]
    [InlineData(HealthState.Ok, StatusPillKind.Ok)]
    [InlineData(HealthState.Warning, StatusPillKind.Attention)]
    [InlineData(HealthState.Error, StatusPillKind.Error)]
    [InlineData(HealthState.Unknown, StatusPillKind.Unknown)]
    [InlineData(HealthState.Disabled, StatusPillKind.Unknown)]
    public void AHealthState_IsOneKind_AlsoForTheRowOfACheck(HealthState state, StatusPillKind kind)
    {
        Assert.Equal(kind, WatchdogDisplay.PillKind(state));

        var localizer = new ResxLocalizer(Polish);
        var row = new WatchdogHealthCheckRowViewModel(new HealthCheckDto { Code = "directory.connection", State = state }, localizer, TimeZoneInfo.Utc);
        Assert.Equal(kind, row.PillKind);

        // The word is the localized name of the state - "disabled" and "unknown" are told apart by it, not by the kind.
        Assert.Equal(localizer.HealthStateName(state), row.StateText);
    }
}
