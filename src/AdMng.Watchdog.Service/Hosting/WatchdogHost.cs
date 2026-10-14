using AdMng.Watchdog.Contracts;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// Composition root of the watchdog service (section 19.1). Scaffold version: a bare web host that maps
/// <c>/healthz</c> only – W-service adds configuration sources, logging, data directory, authentication, Kestrel/HTTP.sys,
/// <c>AddWatchdogCore()</c>, stores, transports, job hosts, startup checks and the API.
/// </summary>
public static class WatchdogHost
{
    /// <summary>Builds the application; <paramref name="configure"/> runs before <c>Build()</c> (tests, smoke test).</summary>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args });
        configure?.Invoke(builder);

        var app = builder.Build();
        app.MapGet(WatchdogRoutes.Health, () => Results.Text("ok", "text/plain"));
        return app;
    }
}
