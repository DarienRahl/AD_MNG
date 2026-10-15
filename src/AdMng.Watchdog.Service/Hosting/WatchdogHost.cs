using AdMng.Watchdog.Core.Diagnostics;
using AdMng.Watchdog.Service.Api;
using AdMng.Watchdog.Service.Auth;
using AdMng.Watchdog.Service.Secrets;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// Composition root of the watchdog service (spec 19.1): configuration sources, logging, data folder, Data Protection,
/// authentication, Kestrel or HTTP.sys, <c>AddWatchdogCore()</c>, stores, transports, job hosts, start-up checks and
/// the API. <see cref="RunAsync"/> dispatches the command line (<c>--smoke-test</c>, <c>--check-config</c>,
/// <c>--health</c>) and maps failed start-up checks to their exit codes.
/// </summary>
public static class WatchdogHost
{
    /// <summary>The Windows service name.</summary>
    public const string ServiceName = "AdMngWatchdog";

    /// <summary>Builds the application; <paramref name="configure"/> runs before the registrations read the configuration
    /// (tests, smoke test).</summary>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null, bool console = false)
    {
        ArgumentNullException.ThrowIfNull(args);
        var isService = WindowsServiceHelpers.IsWindowsService();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = isService ? AppContext.BaseDirectory : null,
        });
        builder.Services.AddWindowsService(o => o.ServiceName = ServiceName);
        var extraConfig = ConfigurationSetup.AddSources(builder.Configuration, args, isService);
        configure?.Invoke(builder);

        var options = builder.Configuration.GetSection(ServiceOptions.SectionName).Get<ServiceOptions>() ?? new ServiceOptions();
        LoggingSetup.Configure(builder, console || (Environment.UserInteractive && !isService));

        var dataDirectory = new DataDirectory(DataDirectory.Resolve(options));
        dataDirectory.Ensure();
        DataProtectionSetup.Configure(builder.Services, dataDirectory);
        AuthenticationSetup.Configure(builder.Services, options);

        var context = CreateContext(options, isService, extraConfig);
        if (options.Hosting.Server == WatchdogServer.HttpSys && OperatingSystem.IsWindows())
        {
            HttpSysSetup.Configure(builder, options.Hosting, context.Endpoints);
        }
        else
        {
            KestrelSetup.Configure(builder, context.Endpoints);
        }

        ServiceRegistration.Add(builder.Services, builder.Configuration, dataDirectory, context, options);
        var app = builder.Build();
        ApiSetup.Map(app);
        return app;
    }

    /// <summary>The process entry: runs the requested command and returns the exit code.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var command = HostCommand.Parse(args);
        var hostArgs = command.HostArgs.ToArray();
        return command.Kind switch
        {
            HostCommandKind.SmokeTest => await SmokeTest.RunAsync(hostArgs).ConfigureAwait(false),
            HostCommandKind.CheckConfig => await CheckConfigCommand.RunAsync(hostArgs).ConfigureAwait(false),
            HostCommandKind.Health => await HealthCommand.RunAsync(hostArgs).ConfigureAwait(false),
            _ => await RunServiceAsync(hostArgs, command.Console).ConfigureAwait(false),
        };
    }

    /// <summary>The exit code of a failed start (the <see cref="StartupCheckException"/> inside, or 1).</summary>
    public static int ExitCodeOf(Exception exception) => exception switch
    {
        StartupCheckException check => check.ExitCode,
        AggregateException aggregate => aggregate.InnerExceptions.OfType<StartupCheckException>().Select(e => e.ExitCode).FirstOrDefault(StartupExitCodes.Fatal),
        _ => StartupExitCodes.Fatal,
    };

    /// <summary>
    /// Runs <paramref name="build"/>; any failure (malformed bootstrap JSON, an invalid setting value, an unusable data
    /// folder) goes to <paramref name="reportFailure"/> as a value-free error code and yields exit code 2 – never an
    /// unhandled crash. Returns 0 with the built <paramref name="app"/> on success.
    /// </summary>
    public static int TryBuild(Func<WebApplication> build, Action<string> reportFailure, out WebApplication? app)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(reportFailure);
        try
        {
            app = build();
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            app = null;
            reportFailure(SafeExceptionLog.ErrorCode(ex));
            return StartupExitCodes.InvalidSettings;
        }
    }

    private static async Task<int> RunServiceAsync(string[] args, bool console)
    {
        var built = TryBuild(() => Build(args, console: console), StartupFailureLog.Report, out var app);
        if (app is null)
        {
            return built;
        }

        await using (app.ConfigureAwait(false))
        {
            // Resolved up front: a failed RunAsync disposes the host's service provider.
            var terminator = app.Services.GetService<IProcessTerminator>();
            try
            {
                await app.RunAsync().ConfigureAwait(false);
                return Environment.ExitCode;
            }
            catch (Exception ex) when (ex is StartupCheckException or AggregateException)
            {
                // Through the terminator (Environment.Exit in production, recorded by the tests): the exit code reaches
                // the service control manager even when the host is driven by someone else (WebApplicationFactory).
                var exitCode = ExitCodeOf(ex);
                terminator?.Exit(exitCode);
                return exitCode;
            }
        }
    }

    private static StartupContext CreateContext(ServiceOptions options, bool isService, string? extraConfig)
    {
        IReadOnlyList<ListenerEndpoint> endpoints = [];
        string? error = null;
        try
        {
            endpoints = ListenerEndpoint.ParseAll(options.Hosting.Urls);
            if (endpoints.Count == 0)
            {
                error = "Hosting.Urls lists no listener.";
            }
        }
        catch (FormatException)
        {
            error = "Hosting.Urls contains an invalid URL.";
        }

        return new StartupContext
        {
            IsWindowsService = isService,
            Endpoints = endpoints,
            Server = options.Hosting.Server,
            ExtraConfigFile = extraConfig,
            BootstrapFile = ConfigurationSetup.BootstrapFilePath,
            EndpointError = error,
        };
    }
}
