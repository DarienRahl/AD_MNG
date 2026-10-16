using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Core.Diagnostics;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// <c>--health</c> (spec 19.1): reads the bootstrap <c>Hosting.Urls</c>, GETs <c>/healthz</c> on the first listener (the
/// configured FQDN, <c>localhost</c> for a wildcard) with system certificate validation and a 10 s timeout, prints the
/// result and exits 0 (ok) or 1 – for monitoring scripts and the install script.
/// </summary>
public static class HealthCommand
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task<int> RunAsync(string[] args)
    {
        var configuration = new ConfigurationManager();
        configuration.SetBasePath(AppContext.BaseDirectory);
        configuration.AddJsonFile("appsettings.json", optional: true);
        ConfigurationSetup.AddSources(configuration, args, isWindowsService: false);
        var options = configuration.GetSection(ServiceOptions.SectionName).Get<ServiceOptions>() ?? new ServiceOptions();
        if (HealthUri(options) is not { } uri)
        {
            await Console.Error.WriteLineAsync("Hosting.Urls lists no valid listener.").ConfigureAwait(false);
            return 1;
        }

        using var handler = new SocketsHttpHandler { UseProxy = false, SslOptions = { EnabledSslProtocols = Notifications.DeliveryTls.Protocols } };
        using var client = new HttpClient(handler) { Timeout = Timeout };
        try
        {
            using var response = await client.GetAsync(uri).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var ok = response.IsSuccessStatusCode && string.Equals(body.Trim(), "ok", StringComparison.Ordinal);
            await Console.Out.WriteLineAsync($"{uri}: HTTP {(int)response.StatusCode} {(ok ? "ok" : "not healthy")}").ConfigureAwait(false);
            return ok ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            await Console.Out.WriteLineAsync($"{uri}: unreachable ({SafeExceptionLog.ErrorCode(ex)})").ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>The <c>/healthz</c> URL of the first configured listener.</summary>
    public static Uri? HealthUri(ServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        IReadOnlyList<ListenerEndpoint> endpoints;
        try
        {
            endpoints = ListenerEndpoint.ParseAll(options.Hosting.Urls);
        }
        catch (FormatException)
        {
            return null;
        }

        if (endpoints.Count == 0 || !Uri.TryCreate(endpoints[0].Url.Replace("://+", "://localhost", StringComparison.Ordinal)
                .Replace("://*", "://localhost", StringComparison.Ordinal)
                .Replace("://0.0.0.0", "://localhost", StringComparison.Ordinal)
                .Replace("://[::]", "://localhost", StringComparison.Ordinal), UriKind.Absolute, out var listener))
        {
            return null;
        }

        return new Uri(new UriBuilder(listener.Scheme, listener.Host, endpoints[0].Port).Uri, WatchdogRoutes.Health);
    }
}
