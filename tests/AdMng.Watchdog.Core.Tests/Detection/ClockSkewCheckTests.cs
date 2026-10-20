using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Detection;

namespace AdMng.Watchdog.Core.Tests.Detection;

/// <summary>host.clockSkew (stage B design 4.7 item 6): Warning above 60 s, Error above 240 s, both directions.</summary>
public sealed class ClockSkewCheckTests
{
    private static readonly DateTimeOffset Local = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, HealthState.Ok)]
    [InlineData(60, HealthState.Ok)]
    [InlineData(61, HealthState.Warning)]
    [InlineData(-90, HealthState.Warning)]
    [InlineData(240, HealthState.Warning)]
    [InlineData(241, HealthState.Error)]
    [InlineData(-300, HealthState.Error)]
    public void Thresholds(int dcAheadSeconds, HealthState expected)
    {
        var check = ClockSkewCheck.Evaluate(Local.AddSeconds(dcAheadSeconds), Local, "dc01.contoso.com");

        Assert.Equal(HealthCheckCodes.HostClockSkew, check.Code);
        Assert.Equal(expected, check.State);
        Assert.Equal([dcAheadSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "dc01.contoso.com"], check.Args);
    }
}
