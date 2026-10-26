using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Diagnostics;
using AdMng.Watchdog.Service.Hosting;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Directory;

/// <summary>
/// The live LDAP session of the service (spec 6.2): one session pinned to one DC (CLAUDE.md rule 4) through
/// <see cref="IDirectoryConnector"/>. With <c>Directory.PreferPdc</c> and no fixed server it reads <c>fSMORoleOwner</c>
/// of the domain head after connecting and reconnects to the PDC emulator when the located DC is another one. A
/// transport failure reported by a job drops the session; the next call reconnects.
/// <para>
/// Linux (stage B design 5.3): <c>Directory.Servers</c> is the ordered DC list (no DC locator). The first reachable
/// server is used; with <c>PreferPdc</c> the provider reconnects to the PDC emulator when it is in the list. After a
/// failure the pinned DC is retried until <c>Directory.FailoverAfter</c> (runtime configuration) has passed, then the
/// next servers are tried. A Kerberos bind below the sealing SSF is refused by the connection factory and reported as
/// health Error <c>directory.notSealed</c>; every session reports its SSF in the Status section „Host” (5.1).
/// </para>
/// </summary>
public sealed partial class ServiceDirectorySessionProvider : IDirectorySessionProvider, IAsyncDisposable
{
    private readonly IDirectoryConnector _connector;
    private readonly DirectoryConnectionOptionsFactory _factory;
    private readonly IOptionsMonitor<ServiceOptions> _options;
    private readonly ILogger<ServiceDirectorySessionProvider> _logger;
    private readonly IHealthRegistry? _health;
    private readonly IWatchdogConfigProvider? _configuration;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IDirectorySession? _session;
    private volatile DirectoryConnectionState _state = DirectoryConnectionState.Disconnected;
    private string? _pinnedServer;
    private DateTimeOffset? _failingSince;

    public ServiceDirectorySessionProvider(
        IDirectoryConnector connector,
        DirectoryConnectionOptionsFactory factory,
        IOptionsMonitor<ServiceOptions> options,
        ILogger<ServiceDirectorySessionProvider> logger,
        IHealthRegistry? health = null,
        IWatchdogConfigProvider? configuration = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _connector = connector;
        _factory = factory;
        _options = options;
        _logger = logger;
        _health = health;
        _configuration = configuration;
        _time = time ?? TimeProvider.System;
    }

    public DirectoryConnectionState State => _state;

    public string? CurrentServer => Volatile.Read(ref _session)?.Info.Server;

    public async ValueTask<IDirectorySession> GetSessionAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _session) is { } existing)
        {
            return existing;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is { } current)
            {
                return current;
            }

            _state = DirectoryConnectionState.Connecting;
            var session = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _session, session);
            _state = DirectoryConnectionState.Connected;
            Log.Connected(_logger, session.Info.Server);
            return session;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            _state = DirectoryConnectionState.Failed;
            _failingSince ??= _time.GetUtcNow();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void ReportFailure(IDirectorySession session, Exception error)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(error);
        if (Interlocked.CompareExchange(ref _session, null, session) != session)
        {
            return;
        }

        _state = DirectoryConnectionState.Failed;
        _failingSince ??= _time.GetUtcNow();
        SafeExceptionLog.LogWarning(_logger, error, "directory session", new EventId(EventLogIds.DirectoryUnavailable));
        _ = session.DisposeAsync().AsTask().ContinueWith(static _ => { }, TaskScheduler.Default);
    }

    public async ValueTask DisposeAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }

    private async Task<IDirectorySession> ConnectAsync(CancellationToken cancellationToken)
    {
        var directory = _options.CurrentValue.Directory;
        var servers = ServerList(directory);
        IDirectorySession session;
        if (servers.Count == 0)
        {
            session = await ConnectLocatedAsync(directory, cancellationToken).ConfigureAwait(false);
            _pinnedServer = session.Info.Server;
        }
        else
        {
            (session, _pinnedServer) = await ConnectListedAsync(directory, servers, cancellationToken).ConfigureAwait(false);
        }

        _failingSince = null;
        ReportSession(session);
        return session;
    }

    /// <summary><c>Directory.Servers</c> (trimmed, distinct), else <c>Directory.Server</c> as a one-entry list; empty =
    /// DC locator (Windows).</summary>
    internal static IReadOnlyList<string> ServerList(DirectoryBootstrapOptions directory)
    {
        var listed = (directory.Servers ?? []).Select(s => s?.Trim().TrimEnd('.') ?? string.Empty).Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return listed.Count > 0 || string.IsNullOrWhiteSpace(directory.Server) ? listed : [directory.Server.Trim()];
    }

    /// <summary>The order in which <paramref name="servers"/> are tried: the pinned DC alone while it has failed for less
    /// than <paramref name="failoverAfter"/>, otherwise the pinned DC followed by the servers after it in list order.</summary>
    internal static IReadOnlyList<string> Candidates(IReadOnlyList<string> servers, string? pinned, TimeSpan? failingFor, TimeSpan failoverAfter)
    {
        var index = pinned is null ? -1 : servers.ToList().FindIndex(s => string.Equals(s, pinned, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return servers;
        }

        if (failingFor is { } failing && failing < failoverAfter)
        {
            return [servers[index]];
        }

        return [.. servers.Skip(index), .. servers.Take(index)];
    }

    /// <summary>The first reachable listed DC (then the PDC emulator when listed); returns the session and its list entry.</summary>
    private async Task<(IDirectorySession Session, string Server)> ConnectListedAsync(
        DirectoryBootstrapOptions directory, IReadOnlyList<string> servers, CancellationToken cancellationToken)
    {
        var failoverAfter = _configuration?.Current.Configuration.Directory.FailoverAfter ?? TimeSpan.FromMinutes(10);
        var failingFor = _failingSince is { } since ? _time.GetUtcNow() - since : (TimeSpan?)null;
        DirectoryException? last = null;
        IDirectorySession? session = null;
        string? connected = null;
        foreach (var server in Candidates(servers, _pinnedServer, failingFor, failoverAfter))
        {
            try
            {
                session = await ConnectToAsync(server, cancellationToken).ConfigureAwait(false);
                connected = server;
                if (_pinnedServer is not null && !string.Equals(_pinnedServer, server, StringComparison.OrdinalIgnoreCase))
                {
                    Log.FailedOver(_logger, _pinnedServer, server);
                }

                break;
            }
            catch (DirectoryException ex)
            {
                last = ex;
                SafeExceptionLog.LogWarning(_logger, ex, "directory connection", new EventId(EventLogIds.DirectoryUnavailable));
            }
        }

        if (session is null || connected is null)
        {
            throw last ?? new DirectoryException(DirectoryErrorKind.ServerUnavailable, "No domain controller of Directory.Servers is reachable.");
        }

        if (!directory.PreferPdc || servers.Count < 2)
        {
            return (session, connected);
        }

        string? pdc;
        try
        {
            pdc = await FindPdcAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryException ex)
        {
            SafeExceptionLog.LogWarning(_logger, ex, "PDC emulator lookup");
            return (session, connected);
        }

        var listedPdc = servers.FirstOrDefault(s => string.Equals(s, pdc?.TrimEnd('.'), StringComparison.OrdinalIgnoreCase));
        if (listedPdc is null || string.Equals(listedPdc, connected, StringComparison.OrdinalIgnoreCase))
        {
            return (session, connected);
        }

        try
        {
            var pdcSession = await ConnectToAsync(listedPdc, cancellationToken).ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
            return (pdcSession, listedPdc);
        }
        catch (DirectoryException ex)
        {
            SafeExceptionLog.LogWarning(_logger, ex, "PDC emulator connection");
            return (session, connected);
        }
    }

    private async Task<IDirectorySession> ConnectToAsync(string? server, CancellationToken cancellationToken)
    {
        var (options, credential) = _factory.Create(server);
        try
        {
            return await _connector.ConnectAsync(options, credential, cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.StrongAuthRequired && OperatingSystem.IsLinux()
            && options.Authentication == AuthenticationMode.CurrentUser && options.Security == TransportSecurity.SignAndSeal)
        {
            // Linux design 5.1: the factory refused a Kerberos session below SSF 56 – never run unsealed.
            _health?.Report(new HealthCheckDto
            {
                Code = HealthCheckCodes.DirectoryNotSealed,
                State = HealthState.Error,
                Detail = "The LDAP bind negotiated no confidentiality (SASL SSF below 56); the session was refused.",
                Args = server is null ? [] : [server],
                CheckedAt = _time.GetUtcNow(),
            });
            throw;
        }
    }

    private void ReportSession(IDirectorySession session)
    {
        if (_health is null || !OperatingSystem.IsLinux())
        {
            return;
        }

        var info = session.Info;
        if (info.Authentication == AuthenticationMode.CurrentUser && info.Security == TransportSecurity.SignAndSeal)
        {
            _health.Report(new HealthCheckDto
            {
                Code = HealthCheckCodes.DirectoryNotSealed,
                State = HealthState.Ok,
                Detail = "The LDAP session is sealed.",
                Args = info.SecurityStrengthFactor is { } ssf ? [ssf.ToString(System.Globalization.CultureInfo.InvariantCulture)] : [],
                CheckedAt = _time.GetUtcNow(),
            });
        }

        _health.ReportHost((_health.Host ?? new HostStatusDto()) with
        {
            LdapSsf = info.SecurityStrengthFactor,
            LdapSecurity = info.Security.ToString(),
        });
    }

    private async Task<IDirectorySession> ConnectLocatedAsync(DirectoryBootstrapOptions directory, CancellationToken cancellationToken)
    {
        var session = await ConnectToAsync(null, cancellationToken).ConfigureAwait(false);
        if (!directory.PreferPdc || !string.IsNullOrWhiteSpace(directory.Server))
        {
            return session;
        }

        string? pdc;
        try
        {
            pdc = await FindPdcAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryException ex)
        {
            SafeExceptionLog.LogWarning(_logger, ex, "PDC emulator lookup");
            return session;
        }

        if (pdc is null || string.Equals(pdc, session.Info.Server, StringComparison.OrdinalIgnoreCase))
        {
            return session;
        }

        try
        {
            var pdcSession = await ConnectToAsync(pdc, cancellationToken).ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
            return pdcSession;
        }
        catch (DirectoryException ex)
        {
            // The PDC is preferred, not required: keep the located DC (health shows IsPdc = false).
            SafeExceptionLog.LogWarning(_logger, ex, "PDC emulator connection");
            return session;
        }
    }

    /// <summary>DNS host name of the PDC emulator: fSMORoleOwner of the domain head → its server object → dNSHostName.</summary>
    internal static async Task<string?> FindPdcAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        var head = await session.GetAsync(session.Info.DefaultNamingContext, ["fSMORoleOwner"], cancellationToken).ConfigureAwait(false);
        var ntdsSettings = head?.GetString("fSMORoleOwner");
        if (ntdsSettings is null || !DistinguishedName.TryParse(ntdsSettings, out var settingsDn) || settingsDn.Parent is not { } serverDn)
        {
            return null;
        }

        var server = await session.GetAsync(serverDn.ToString(), ["dNSHostName"], cancellationToken).ConfigureAwait(false);
        return server?.GetString("dNSHostName");
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 2400, Level = LogLevel.Information, Message = "Directory session connected to {Server}.")]
        public static partial void Connected(ILogger logger, string server);

        [LoggerMessage(EventId = EventLogIds.DirectoryUnavailable, Level = LogLevel.Warning, Message = "Failover from {Previous} to {Server} (Directory.Servers).")]
        public static partial void FailedOver(ILogger logger, string previous, string server);
    }
}
