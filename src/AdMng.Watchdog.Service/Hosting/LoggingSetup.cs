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

    /// <summary>
    /// Categories whose Information entries belong in the Application log: 1000 service started (<see cref="StartupChecks"/>,
    /// whose only Information entry it is), 1006 configuration changed (<see cref="Api.ConfigChangeNotifier"/>) and the
    /// RODO access log of single events 2600/2601 (<see cref="Api.EventAccessLog"/>) – as a Windows service the Application
    /// log is the only sink, so a Warning+ filter would silently drop them.
    /// </summary>
    public static IReadOnlyList<string> EventLogInformationCategories { get; } =
    [
        typeof(StartupChecks).FullName!,
        typeof(Api.ConfigChangeNotifier).FullName!,
        typeof(Api.EventAccessLog).FullName!,
    ];

    /// <summary>The Application-log filter: Warning and above, plus Information of <see cref="EventLogInformationCategories"/>.</summary>
    public static bool EventLogFilter(string? category, LogLevel level) =>
        level is >= LogLevel.Warning and < LogLevel.None
        || (level == LogLevel.Information && category is not null && EventLogInformationCategories.Contains(category, StringComparer.Ordinal));

    [SupportedOSPlatform("windows")]
    private static void AddEventLog(ILoggingBuilder logging) =>
        logging.AddEventLog(settings =>
        {
            settings.SourceName = EventLogSource;
            settings.LogName = "Application";
            settings.Filter = EventLogFilter;
        });
}
