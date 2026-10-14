namespace AdMng.Watchdog.Service.Hosting;

/// <summary>The minimum ASP.NET Core runtime (spec 19.7): 10.0.13 fixes several ASP.NET Core CVEs; older runtimes start
/// with an Event Log warning and health Warning <c>runtime.outdated</c>.</summary>
public static class RuntimeVersionCheck
{
    public static readonly Version Minimum = new(10, 0, 13);

    public const string HealthCode = "runtime.outdated";

    public static bool IsOutdated(Version runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return new Version(runtime.Major, runtime.Minor, Math.Max(runtime.Build, 0)) < Minimum;
    }
}
