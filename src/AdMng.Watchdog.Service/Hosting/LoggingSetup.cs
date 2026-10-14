using System.Runtime.Versioning;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// Logging of the service (spec 19.1): console only when interactive (or <c>--console</c>), the Windows Application log
/// (source <see cref="EventLogSource"/>, Warning and above) on Windows, both wrapped by
/// <see cref="RedactingLoggerProvider"/>. No file logs, no request/response body logging.
/// </summary>
public static class LoggingSetup
{
    /// <summary>The Application log source created by the install script.</summary>
    public const string EventLogSource = "AdMng Watchdog";

    public static void Configure(WebApplicationBuilder builder, bool console)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Logging.ClearProviders();
        if (console)
        {
            builder.Logging.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
                options.UseUtcTimestamp = true;
            });
        }

        if (OperatingSystem.IsWindows())
        {
            AddEventLog(builder.Logging);
        }

        // Categories that write full request/target URLs (query strings carry search texts, Teams SAS signatures):
        // never below Warning, whatever the configuration says (more specific than "Microsoft.AspNetCore").
        foreach (var category in UrlLoggingCategories)
        {
            builder.Logging.AddFilter(category, LogLevel.Warning);
        }

        RedactingLoggerProvider.WrapRegisteredProviders(builder.Services);
    }

    /// <summary>Framework log categories that include request or target URLs at Information level.</summary>
    public static IReadOnlyList<string> UrlLoggingCategories { get; } =
    [
        "Microsoft.AspNetCore.Hosting.Diagnostics",
        "Microsoft.AspNetCore.HttpLogging",
        "Microsoft.AspNetCore.W3CLogging",
        "System.Net.Http.HttpClient",
    ];

    [SupportedOSPlatform("windows")]
    private static void AddEventLog(ILoggingBuilder logging) =>
        logging.AddEventLog(settings =>
        {
            settings.SourceName = EventLogSource;
            settings.LogName = "Application";
            settings.Filter = (_, level) => level >= LogLevel.Warning;
        });
}
