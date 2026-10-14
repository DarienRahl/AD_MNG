using AdMng.Core.Directory;
using AdMng.Core.Ldap;
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
/// </summary>
public sealed partial class ServiceDirectorySessionProvider : IDirectorySessionProvider, IAsyncDisposable
{
    private readonly IDirectoryConnector _connector;
    private readonly DirectoryConnectionOptionsFactory _factory;
    private readonly IOptionsMonitor<ServiceOptions> _options;
    private readonly ILogger<ServiceDirectorySessionProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IDirectorySession? _session;
    private volatile DirectoryConnectionState _state = DirectoryConnectionState.Disconnected;

    public ServiceDirectorySessionProvider(
        IDirectoryConnector connector,
        DirectoryConnectionOptionsFactory factory,
        IOptionsMonitor<ServiceOptions> options,
        ILogger<ServiceDirectorySessionProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _connector = connector;
        _factory = factory;
        _options = options;
        _logger = logger;
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
        var (options, credential) = _factory.Create();
        var session = await _connector.ConnectAsync(options, credential, cancellationToken).ConfigureAwait(false);
        var directory = _options.CurrentValue.Directory;
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
            var (pdcOptions, pdcCredential) = _factory.Create(pdc);
            var pdcSession = await _connector.ConnectAsync(pdcOptions, pdcCredential, cancellationToken).ConfigureAwait(false);
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
    }
}
