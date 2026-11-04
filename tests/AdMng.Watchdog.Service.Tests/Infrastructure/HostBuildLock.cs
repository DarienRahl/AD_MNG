namespace AdMng.Watchdog.Service.Tests.Infrastructure;

/// <summary>
/// Lets one host at a time be built in this test process. <see cref="WatchdogFactory"/> (WebApplicationFactory over the
/// minimal-hosting entry point) finds its host by listening to every <c>Microsoft.Extensions.Hosting</c> diagnostic
/// listener created while it waits, so a host another test builds at that moment can receive the factory's settings and
/// services (its terminator, its log) – a start check that failed in one test then reported another test's exit code, or
/// none. Every host built by these tests goes through <see cref="Build{T}"/>.
/// </summary>
public static class HostBuildLock
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Runs <paramref name="build"/> (building – and for the factory, starting – a host) while no other host is built.</summary>
    public static T Build<T>(Func<T> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        Gate.Wait();
        try
        {
            return build();
        }
        finally
        {
            Gate.Release();
        }
    }
}
