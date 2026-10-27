using System.Globalization;
using System.Net;
using AdMng.Core.Directory;

namespace AdMng.Core.Computers.Remote;

/// <summary>Why a remote action failed (K1 design note 4.7). The UI shows a localized text and a hint per kind.</summary>
public enum RemoteErrorKind
{
    /// <summary>The host does not answer (name not resolved, no route, port closed).</summary>
    Unreachable,

    /// <summary>The host answers but WinRM (WS-Management) does not: service stopped, no listener, firewall.</summary>
    WinRmNotAvailable,

    /// <summary>Kerberos failed: no SPN, clock skew, wrong account, NTLM-only target.</summary>
    AuthenticationFailed,

    /// <summary>The account is no local administrator of the target (Domain Admins on DCs).</summary>
    AccessDenied,

    Timeout,

    Cancelled,

    /// <summary>Not possible here: not Windows, no DNS name, an IP address, a simple-bind window, an older target.</summary>
    NotSupported,

    Failed,
}

/// <summary>
/// A failed remote action. The message is a fixed English text of the kind – it never carries the computer name, the
/// account or remote output (diagnostic logs stay value-free).
/// </summary>
public sealed class RemoteActionException : Exception
{
    public RemoteActionException(RemoteErrorKind kind, Exception? innerException = null)
        : base(DefaultMessage(kind), innerException)
    {
        Kind = kind;
    }

    public RemoteActionException()
        : this(RemoteErrorKind.Failed)
    {
    }

    public RemoteActionException(string message)
        : base(message)
    {
        Kind = RemoteErrorKind.Failed;
    }

    public RemoteActionException(string message, Exception innerException)
        : base(message, innerException)
    {
        Kind = RemoteErrorKind.Failed;
    }

    public RemoteErrorKind Kind { get; }

    /// <summary>A protocol or Windows error code when one is known (shown as a number only).</summary>
    public int? ErrorCode { get; init; }

    private static string DefaultMessage(RemoteErrorKind kind) => kind switch
    {
        RemoteErrorKind.Unreachable => "The computer does not answer.",
        RemoteErrorKind.WinRmNotAvailable => "WinRM is not available on the computer.",
        RemoteErrorKind.AuthenticationFailed => "Kerberos authentication to the computer failed.",
        RemoteErrorKind.AccessDenied => "Access to the computer was denied.",
        RemoteErrorKind.Timeout => "The remote operation timed out.",
        RemoteErrorKind.Cancelled => "The remote operation was cancelled.",
        RemoteErrorKind.NotSupported => "The remote operation is not supported for this computer or connection.",
        _ => "The remote operation failed.",
    };
}

/// <summary>The computer of a remote action: its DNS name from AD (validated), never an IP address.</summary>
public sealed record RemoteTarget
{
    public RemoteTarget(string distinguishedName, string name, string dnsHostName, ComputerType type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distinguishedName);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!HostNames.IsValid(dnsHostName))
        {
            throw new RemoteActionException(RemoteErrorKind.NotSupported);
        }

        DistinguishedName = distinguishedName;
        Name = name;
        DnsHostName = HostNames.Normalize(dnsHostName);
        Type = type;
    }

    public string DistinguishedName { get; }

    public string Name { get; }

    public string DnsHostName { get; }

    public ComputerType Type { get; }

    /// <summary>
    /// A restart needs the typed computer name for everything but a known workstation: servers, domain controllers and
    /// computers of unknown type (no operatingSystem – cluster name objects and their virtual computer objects restart the
    /// node that owns them).
    /// </summary>
    public bool RestartNeedsTypedName => Type is not ComputerType.Workstation;
}

/// <summary>Options of the remote client (settings).</summary>
public sealed record RemoteOptions
{
    public const int MinTimeoutSeconds = 5;
    public const int MaxTimeoutSeconds = 300;

    /// <summary>Connect and operation timeout (default 30 s, 5–300).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>HTTPS (5986) instead of HTTP with Kerberos message encryption (5985).</summary>
    public bool UseHttps { get; init; }

    /// <summary>A non-default WinRM port, null for 5985 / 5986.</summary>
    public int? Port { get; init; }

    /// <summary>Refresh the user policy of logged-on users through a one-time scheduled task (off until the pilot confirms it).</summary>
    public bool RefreshUserPolicy { get; init; }

    public int EffectivePort => Port ?? (UseHttps ? 5986 : 5985);
}

/// <summary>The operating system as the computer reports it (patch level AD does not have).</summary>
public sealed record RemoteOperatingSystem(string? Caption, string? Version, int? BuildNumber, int? UpdateBuildRevision, DateTimeOffset? LastBoot)
{
    /// <summary><c>10.0.26100.4652</c>.</summary>
    public string? FullBuild => Version is null ? null
        : UpdateBuildRevision is { } ubr ? string.Create(CultureInfo.InvariantCulture, $"{Version}.{ubr}") : Version;
}

/// <summary>A user logged on to the computer (an explorer.exe owner, or the console user).</summary>
/// <param name="Account">DOMAIN\user.</param>
/// <param name="SessionId">Windows session (1 = usually the console of a workstation).</param>
/// <param name="Since">Start of the user's explorer.exe.</param>
/// <param name="IsConsole">Win32_ComputerSystem.UserName names this account.</param>
public sealed record LoggedOnUser(string Account, int? SessionId, DateTimeOffset? Since, bool IsConsole);

/// <summary>A Windows service of the computer (read only in K1).</summary>
public sealed record RemoteService(string Name, string? DisplayName, string? State, string? StartMode, bool DelayedAutoStart, string? Account)
{
    /// <summary>Automatic but not running (the filter "automatyczne, zatrzymane").</summary>
    public bool IsAutomaticAndStopped =>
        string.Equals(StartMode, "Auto", StringComparison.OrdinalIgnoreCase) && !string.Equals(State, "Running", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What "Odśwież zasady grupy" refreshes.</summary>
public enum GroupPolicyRefreshTarget
{
    Computer,

    /// <summary>The computer and – with <see cref="RemoteOptions.RefreshUserPolicy"/> – the logged-on users.</summary>
    ComputerAndUsers,
}

/// <summary>The outcome of a policy refresh.</summary>
/// <param name="Completed">The computer policy processing started by the refresh ended (false: still running when the wait ended).</param>
/// <param name="FinishedAt">End of that processing (from the Group Policy state of the computer); null while it is still running.</param>
/// <param name="Status">Its status (0 = success); null while it is still running.</param>
/// <param name="UserRefreshScheduled">The scheduled task for the users was registered.</param>
public sealed record GroupPolicyRefreshResult(bool Completed, DateTimeOffset? FinishedAt, int? Status, bool UserRefreshScheduled);

/// <summary>The result of a ping (4 echoes).</summary>
public sealed record PingResult(string Host, IPAddress? Address, IReadOnlyList<TimeSpan?> RoundTrips)
{
    public int Received => RoundTrips.Count(r => r is not null);

    public bool Succeeded => Received > 0;

    /// <summary>Average round trip of the answered echoes.</summary>
    public TimeSpan? Average => Received == 0 ? null : TimeSpan.FromTicks((long)RoundTrips.Where(r => r is not null).Average(r => r!.Value.Ticks));
}

/// <summary>Resolves host names to addresses (the workstation's DNS resolver; 2 s timeout).</summary>
public interface IHostAddressResolver
{
    /// <summary>The addresses of <paramref name="host"/> (IPv4 first), empty when the name does not resolve.</summary>
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

/// <summary>ICMP echo (no credential).</summary>
public interface IPingService
{
    /// <summary>Sends <paramref name="count"/> echoes with <paramref name="timeout"/> each.</summary>
    Task<PingResult> PingAsync(string host, int count, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Remote actions over WinRM (WS-Management) with the WINDOW's credential (K1 design note 4.7): Kerberos only, the window's
/// Windows account or a copy of its in-memory password that lives as long as one CIM session.
/// </summary>
public interface IRemoteComputerClient
{
    /// <summary>Opens a session to the target with the credential of <paramref name="session"/>.</summary>
    /// <exception cref="RemoteActionException">Not possible (kind says why).</exception>
    Task<IRemoteComputerSession> ConnectAsync(RemoteTarget target, IDirectorySession session, RemoteOptions options, CancellationToken cancellationToken);
}

/// <summary>An open remote session (one action). Every call throws <see cref="RemoteActionException"/> on failure.</summary>
public interface IRemoteComputerSession : IAsyncDisposable
{
    Task<RemoteOperatingSystem> GetOperatingSystemAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<LoggedOnUser>> GetLoggedOnUsersAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<RemoteService>> GetServicesAsync(CancellationToken cancellationToken);

    /// <summary>Runs <c>gpupdate /force</c> for the computer (and schedules the users' refresh when enabled) and waits up to 10 minutes.</summary>
    Task<GroupPolicyRefreshResult> RefreshGroupPolicyAsync(GroupPolicyRefreshTarget target, CancellationToken cancellationToken);

    /// <summary>Schedules a restart in <paramref name="delay"/> with <paramref name="message"/> shown to the logged-on users.</summary>
    Task ScheduleRestartAsync(TimeSpan delay, string message, bool force, CancellationToken cancellationToken);

    /// <summary>
    /// Requests the cancellation of a scheduled restart: starts <c>shutdown /a</c> on the computer. Its exit code is not
    /// observed, so success means the request was started, not that a pending restart was cancelled.
    /// </summary>
    Task AbortRestartAsync(CancellationToken cancellationToken);
}

/// <summary>Local tools started for a computer (K1 design note 4.8).</summary>
public enum LocalTool
{
    /// <summary><c>mstsc.exe /v:host</c> – never with a credential.</summary>
    RemoteDesktop,

    /// <summary><c>mmc.exe compmgmt.msc /computer:host</c> with the window's credential (network only).</summary>
    ComputerManagement,

    /// <summary><c>eventvwr.exe host</c> with the window's credential (network only).</summary>
    EventViewer,
}

/// <summary>Starts local tools for a computer.</summary>
public interface ILocalToolLauncher
{
    /// <summary>Whether <paramref name="tool"/> runs with the window's credential (the UI asks first when it does).</summary>
    bool UsesWindowCredential(LocalTool tool, IDirectorySession session);

    /// <summary>Starts <paramref name="tool"/> for <paramref name="host"/>.</summary>
    /// <exception cref="RemoteActionException">The tool cannot be started (not Windows, invalid host, logon failed).</exception>
    Task LaunchAsync(LocalTool tool, string host, IDirectorySession session, CancellationToken cancellationToken);
}
