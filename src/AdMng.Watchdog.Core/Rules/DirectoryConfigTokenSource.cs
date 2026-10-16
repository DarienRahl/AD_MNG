using AdMng.Core.Directory;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AdMng.Watchdog.Core.Rules;

/// <summary>
/// Token values from the live directory session (<see cref="IDirectorySessionProvider"/>): <c>{domain}</c> =
/// <c>SessionInfo.DefaultNamingContext</c>, <c>{domainSid}</c> = <c>objectSid</c> of the domain head,
/// <c>{rootDomainSid}</c> = <c>objectSid</c> of the forest root domain head (best effort). Read once per session
/// (server + connect time); a failed read keeps the previous values and is retried with the next call.
/// </summary>
public sealed class DirectoryConfigTokenSource : IConfigTokenSource, IDisposable
{
    private static readonly string[] SidAttribute = ["objectSid"];

    private readonly IDirectorySessionProvider _sessions;
    private readonly ILogger<DirectoryConfigTokenSource> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile ConfigTokens _current = ConfigTokens.Unknown;
    private (string Server, DateTimeOffset ConnectedAt)? _readFor;

    public DirectoryConfigTokenSource(IDirectorySessionProvider sessions, ILogger<DirectoryConfigTokenSource> logger)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(logger);
        _sessions = sessions;
        _logger = logger;
    }

    /// <inheritdoc/>
    public ConfigTokens Current => _current;

    /// <inheritdoc/>
    public async ValueTask<ConfigTokens> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IDirectorySession session;
            try
            {
                session = await _sessions.GetSessionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DirectoryException ex)
            {
                SafeExceptionLog.LogWarning(_logger, ex, "configuration token refresh (session)");
                return _current;
            }

            var key = (session.Info.Server, session.Info.ConnectedAt);
            if (_readFor == key && _current.DomainSid is not null)
            {
                return _current;
            }

            var domainDn = session.Info.DefaultNamingContext;
            var domainSid = await ReadSidAsync(session, domainDn, cancellationToken).ConfigureAwait(false);
            var rootDn = session.Info.RootDomainNamingContext;
            var rootSid = string.IsNullOrEmpty(rootDn) || AdMng.Core.Ldap.DistinguishedName.AreEqual(rootDn, domainDn)
                ? domainSid
                : await ReadSidAsync(session, rootDn, cancellationToken).ConfigureAwait(false);

            _current = new ConfigTokens
            {
                DomainDn = domainDn,
                DomainSid = domainSid ?? _current.DomainSid,
                RootDomainSid = rootSid ?? _current.RootDomainSid,
            };
            _readFor = key;
            return _current;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _gate.Dispose();

    private async Task<string?> ReadSidAsync(IDirectorySession session, string distinguishedName, CancellationToken cancellationToken)
    {
        try
        {
            var head = await session.GetAsync(distinguishedName, SidAttribute, cancellationToken).ConfigureAwait(false);
            return head?.ObjectSid;
        }
        catch (DirectoryException ex)
        {
            SafeExceptionLog.LogWarning(_logger, ex, "configuration token refresh (objectSid)");
            return null;
        }
    }
}
