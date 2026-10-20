using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// Logging of the service (spec 19.1, stage B design 15): as a systemd unit the console with the systemd formatter
/// (<c>&lt;priority&gt;Category[EventId] message</c> lines that journald stores with their priority; identical warnings
/// throttled by <see cref="ThrottlingLoggerProvider"/>), otherwise the console only when interactive (or
/// <c>--console</c>), the Windows Application log (source <see cref="EventLogSource"/>, <see cref="EventLogFilter"/>,
/// throttled) on Windows – every provider wrapped by <see cref="RedactingLoggerProvider"/>. No file logs, no
/// request/response body logging.
/// </summary>
public static class LoggingSetup
{
    /// <summary>The Application log source created by the install script.</summary>
    public const string EventLogSource = "AdMng Watchdog";

    public static void Configure(WebApplicationBuilder builder, bool console) => Configure(builder, console, journald: false);

    /// <summary>The providers; <paramref name="journald"/> = running as a systemd unit (stdout goes to the journal).</summary>
    public static void Configure(WebApplicationBuilder builder, bool console, bool journald)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Logging.ClearProviders();
        if (journald)
        {
            AddJournal(builder.Logging);
        }
        else if (console)
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
    /// whose only Information entry it is), 1006 configuration changed (<see cref="Api.ConfigChangeNotifier"/>), the
    /// RODO access log of single events 2600/2601 (<see cref="Api.EventAccessLog"/>) and 2301 job recovered
    /// (<see cref="JobHost"/>, its only Information entry – warning 2300 is written only when a failure starts and then
    /// hourly, so without 2301 the log could not tell a recovered job from a failing one) – as a Windows service the
    /// Application log is the only sink, so a Warning+ filter would silently drop them.
    /// </summary>
    public static IReadOnlyList<string> EventLogInformationCategories { get; } =
    [
        typeof(StartupChecks).FullName!,
        typeof(Api.ConfigChangeNotifier).FullName!,
        typeof(Api.EventAccessLog).FullName!,
        typeof(JobHost).FullName!,
    ];

    /// <summary>The Application-log filter: Warning and above, plus Information of <see cref="EventLogInformationCategories"/>.</summary>
    public static bool EventLogFilter(string? category, LogLevel level) =>
        level is >= LogLevel.Warning and < LogLevel.None
        || (level == LogLevel.Information && category is not null && EventLogInformationCategories.Contains(category, StringComparer.Ordinal));

    /// <summary>
    /// journald through stdout: the console logger with the systemd formatter (syslog priority prefix, no timestamp – the
    /// journal adds it, one line per entry), behind <see cref="ThrottlingLoggerProvider"/> like the Application log: a DC
    /// outage would otherwise repeat the same warning every cycle.
    /// </summary>
    private static void AddJournal(ILoggingBuilder logging)
    {
        logging.AddSystemdConsole(options =>
        {
            options.IncludeScopes = false;
            options.TimestampFormat = null;
        });
        for (var i = 0; i < logging.Services.Count; i++)
        {
            var descriptor = logging.Services[i];
            if (descriptor.ServiceType == typeof(ILoggerProvider) && descriptor.ImplementationType == typeof(ConsoleLoggerProvider))
            {
                logging.Services[i] = ServiceDescriptor.Singleton<ILoggerProvider>(provider => new ThrottlingLoggerProvider(
                    new ConsoleLoggerProvider(provider.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>(), provider.GetServices<ConsoleFormatter>()),
                    provider.GetService<TimeProvider>() ?? TimeProvider.System));
            }
        }
    }

    /// <summary>The Application log, behind <see cref="ThrottlingLoggerProvider"/> (identical warnings once per window).</summary>
    [SupportedOSPlatform("windows")]
    private static void AddEventLog(ILoggingBuilder logging)
    {
        var settings = new EventLogSettings { SourceName = EventLogSource, LogName = "Application", Filter = EventLogFilter };
        logging.Services.AddSingleton<ILoggerProvider>(provider => new ThrottlingLoggerProvider(
            new EventLogLoggerProvider(settings),
            provider.GetService<TimeProvider>() ?? TimeProvider.System));
    }
}
