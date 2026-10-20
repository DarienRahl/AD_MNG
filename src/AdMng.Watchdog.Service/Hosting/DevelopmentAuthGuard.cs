namespace AdMng.Watchdog.Service.Hosting;

/// <summary>Where the Development authentication scheme is refused (spec 8.4, 17.3): outside the Development
/// environment, under a service manager (Windows SCM, systemd), and with any listener that is not loopback.</summary>
public static class DevelopmentAuthGuard
{
    /// <summary>Null when allowed, otherwise the constant reason.</summary>
    public static string? Check(ServiceOptions options, string environmentName, StartupContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        if (options.Authentication != WatchdogAuthenticationMode.Development)
        {
            return null;
        }

        if (!string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase))
        {
            return "Development authentication requires ASPNETCORE_ENVIRONMENT=Development.";
        }

        if (context.IsWindowsService)
        {
            return "Development authentication is never allowed for the Windows service.";
        }

        if (context.IsSystemdService)
        {
            return "Development authentication is never allowed for the systemd unit.";
        }

        if (context.Endpoints.Count == 0 || context.Endpoints.Any(e => !e.IsLoopback))
        {
            return "Development authentication requires loopback-only listeners (Hosting.Urls).";
        }

        return null;
    }
}
