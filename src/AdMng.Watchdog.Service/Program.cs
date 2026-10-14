using AdMng.Watchdog.Service.Hosting;

// Entry point (spec 19.1): the service, --smoke-test, --check-config or --health; the exit code is the result.
return await WatchdogHost.RunAsync(args);

/// <summary>The entry point type (<c>WebApplicationFactory&lt;Program&gt;</c> in the service tests).</summary>
public partial class Program;
