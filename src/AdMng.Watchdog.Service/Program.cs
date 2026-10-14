using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Service.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

// Scaffold entry point (section 19.1): WatchdogHost.Build(args).Run / --smoke-test / --check-config / --health.
// Only --smoke-test and the plain run exist yet; W-service replaces this file.
if (args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
{
    // Loopback HTTP on an ephemeral port, GET /healthz, exit code 0/1.
    await using var smoke = WatchdogHost.Build(
        [.. args.Where(a => !string.Equals(a, "--smoke-test", StringComparison.OrdinalIgnoreCase))],
        builder => builder.WebHost.UseUrls("http://127.0.0.1:0"));
    await smoke.StartAsync();
    try
    {
        var address = smoke.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("The server reported no listening address.");
        using var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };
        var body = await http.GetStringAsync(WatchdogRoutes.Health);
        Console.WriteLine($"Smoke test: {WatchdogRoutes.Health} answered '{body}'.");
        return body == "ok" ? 0 : 1;
    }
    finally
    {
        await smoke.StopAsync();
    }
}

if (args.Any(a => string.Equals(a, "--check-config", StringComparison.OrdinalIgnoreCase)
    || string.Equals(a, "--health", StringComparison.OrdinalIgnoreCase)))
{
    Console.Error.WriteLine("--check-config and --health are not implemented in the scaffold.");
    return 2;
}

var app = WatchdogHost.Build(args);
await app.RunAsync();
return 0;
