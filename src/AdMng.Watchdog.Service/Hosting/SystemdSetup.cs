using Microsoft.Extensions.Hosting.Systemd;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// The service as a systemd unit (stage B design 12.1, 15): <see cref="SystemdLifetime"/> (<c>READY=1</c> once the host –
/// start-up checks, migrations, listeners, job hosts – has started, <c>STOPPING=1</c> on shutdown, SIGTERM), the
/// <see cref="SystemdWatchdogNotifier"/> and, in <see cref="LoggingSetup"/>, the console logger with the systemd formatter
/// (journald). This is what <c>UseSystemd()</c> / <c>AddSystemd()</c> register, with one difference: those decide by
/// <c>SystemdHelpers.IsSystemdService()</c>, which reads <c>/proc/&lt;parent&gt;/comm</c> – and the unit's
/// <c>ProtectProc=invisible</c> hides PID 1 (root) from the service user, so they would see no systemd, never send
/// <c>READY=1</c> and the <c>Type=notify</c> start would time out. Detection here reads the environment systemd gives
/// every unit process instead (<c>INVOCATION_ID</c>, and <c>NOTIFY_SOCKET</c> for <c>Type=notify</c>) together with the
/// process's own parent id (<c>/proc/self/stat</c>, visible under <c>ProcSubset=pid</c>): only the main process of a
/// system unit is a child of PID 1 – a CI step or test run below a runner that itself runs as a unit inherits
/// <c>INVOCATION_ID</c> but has another parent.
/// </summary>
public static class SystemdSetup
{
    /// <summary>Registers the systemd lifetime, notifier and watchdog when <paramref name="isSystemdService"/>.</summary>
    public static void Configure(IServiceCollection services, bool isSystemdService)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!isSystemdService)
        {
            return;
        }

        services.AddSingleton<ISystemdNotifier, SystemdNotifySocket>();
        services.AddSingleton<IHostLifetime, SystemdLifetime>();
        services.AddSingleton(_ => SystemdWatchdogSettings.FromEnvironment());
        services.AddSingleton<IJobProgressSource>(provider => provider.GetRequiredService<JobHostService>());
        services.AddHostedService<SystemdWatchdogNotifier>();
    }

    /// <summary>Set by systemd for every process of a unit (systemd 232+).</summary>
    public const string InvocationIdVariable = "INVOCATION_ID";

    /// <summary>The notification socket of a <c>Type=notify</c> unit.</summary>
    public const string NotifySocketVariable = "NOTIFY_SOCKET";

    /// <summary>True on Linux when the process runs as the main process of a systemd unit.</summary>
    public static bool IsSystemdService() =>
        OperatingSystem.IsLinux() && IsSystemdService(Environment.GetEnvironmentVariable, ParentProcessId());

    /// <summary><see cref="IsSystemdService()"/> over an explicit environment and parent process id (tests).</summary>
    public static bool IsSystemdService(Func<string, string?> environment, int? parentProcessId)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return parentProcessId == 1
            && (!string.IsNullOrWhiteSpace(environment(InvocationIdVariable)) || !string.IsNullOrWhiteSpace(environment(NotifySocketVariable)));
    }

    /// <summary>The parent process id from <paramref name="statFile"/> (<c>/proc/self/stat</c>: the fourth field, after the
    /// parenthesised command name, which may itself contain spaces and parentheses); null when unknown.</summary>
    public static int? ParentProcessId(string statFile = "/proc/self/stat")
    {
        try
        {
            var stat = File.ReadAllText(statFile);
            var end = stat.LastIndexOf(')');
            if (end < 0)
            {
                return null;
            }

            var fields = stat[(end + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return fields.Length > 1 && int.TryParse(fields[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ppid)
                ? ppid
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
