using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Rules;

namespace AdMng.Watchdog.Core.Tests.Rules;

public class RolloutModeTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 10, 7, 9, 15, 0, TimeSpan.Zero);

    [Fact]
    public void ActiveForRolloutDaysAfterTheBaseline_ThenEndsByItself()
    {
        var general = new GeneralSettings();

        Assert.Equal(Baseline.AddDays(7), RolloutMode.Until(general, Baseline));
        Assert.True(RolloutMode.IsActive(general, Baseline, Baseline.AddDays(7).AddTicks(-1)));
        Assert.False(RolloutMode.IsActive(general, Baseline, Baseline.AddDays(7)));
        Assert.Equal(Baseline.AddDays(7), RolloutMode.ActiveUntil(general, Baseline, Baseline.AddDays(1)));
        Assert.Null(RolloutMode.ActiveUntil(general, Baseline, Baseline.AddDays(8)));
    }

    [Fact]
    public void OffWhenEndedEarlyDisabledOrBeforeTheBaseline()
    {
        Assert.Null(RolloutMode.Until(new GeneralSettings { RolloutEnded = true }, Baseline));
        Assert.Null(RolloutMode.Until(new GeneralSettings { RolloutDays = 0 }, Baseline));
        Assert.Null(RolloutMode.Until(new GeneralSettings(), null));
        Assert.Null(RolloutMode.Until(null, Baseline));
        Assert.False(RolloutMode.IsActive(new GeneralSettings { RolloutEnded = true }, Baseline, Baseline.AddHours(1)));
    }

    [Theory]
    [InlineData("tier0-privilege", true)]
    [InlineData("tier0-własna", true)]
    [InlineData("bursts", true)]
    [InlineData("HEALTH", true)]
    [InlineData("new-accounts", false)]
    [InlineData("lockouts", false)]
    [InlineData(null, false)]
    public void OnlyTier0BurstsAndHealthNotifyImmediately(string? ruleId, bool allowed) =>
        Assert.Equal(allowed, RolloutMode.AllowsImmediate(ruleId));
}
