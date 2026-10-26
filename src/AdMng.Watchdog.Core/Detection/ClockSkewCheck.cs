using System.Globalization;
using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// Clock skew between the watched DC and this host (stage B design 4.7 item 6): RootDSE <c>currentTime</c> minus local
/// time, evaluated each detection cycle – Warning above 60 s, Error above 240 s (<c>host.clockSkew</c>; Kerberos fails at
/// 300 s). Args: the skew in whole seconds (positive: the DC is ahead) and the DC host name.
/// </summary>
public static class ClockSkewCheck
{
    public static readonly TimeSpan WarningThreshold = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ErrorThreshold = TimeSpan.FromSeconds(240);

    public static HealthCheckDto Evaluate(DateTimeOffset dcTime, DateTimeOffset localTime, string? dcHostName)
    {
        var skew = dcTime - localTime;
        var magnitude = skew.Duration();
        var state = magnitude > ErrorThreshold ? HealthState.Error : magnitude > WarningThreshold ? HealthState.Warning : HealthState.Ok;
        var seconds = ((long)Math.Round(skew.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        return new HealthCheckDto
        {
            Code = HealthCheckCodes.HostClockSkew,
            State = state,
            Detail = state == HealthState.Ok
                ? "The host clock agrees with the domain controller."
                : "The host clock differs from the domain controller's; Kerberos fails at 5 minutes (check chrony / NTP).",
            Args = dcHostName is null ? [seconds] : [seconds, dcHostName],
            CheckedAt = localTime,
        };
    }
}
