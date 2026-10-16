using System.Reflection;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>The service version (informational version without source-link metadata), for /status, User-Agent and logs.</summary>
public static class ServiceVersion
{
    public static string Current { get; } = Read();

    private static string Read()
    {
        var assembly = typeof(ServiceVersion).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }
}
