using AdMng.Core.Directory;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// Degraded <see cref="IDomainControllerCatalog"/> (not authoritative) that the detection side's
/// <c>DomainControllerCatalog</c> falls back to while the forest's DC list could not be read: the attribution sources
/// are <c>Attribution.DomainControllers</c> or, when empty, the DC the directory session is pinned to (the PDC emulator by default); the known DCs are those
/// hosts; invocation ids are not resolved. Stricter, never looser, than the real directory: records of any other host
/// are rejected by <see cref="EventSourceGuard"/>.
/// </summary>
public sealed class SessionDomainControllerCatalog(IWatchdogConfigProvider configProvider, IDirectorySessionProvider sessions)
    : IDomainControllerCatalog
{
    /// <inheritdoc/>
    public bool IsAuthoritative => false;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> GetAttributionSourcesAsync(CancellationToken cancellationToken)
    {
        var configured = configProvider.Current.Configuration.Attribution?.DomainControllers ?? [];
        if (configured.Count > 0)
        {
            return [.. configured.Where(h => !string.IsNullOrWhiteSpace(h)).Select(HostNames.Normalize).Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        return await CurrentServerAsync(cancellationToken).ConfigureAwait(false) is { } server ? [server] : [];
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> GetKnownDomainControllersAsync(CancellationToken cancellationToken)
    {
        var hosts = new List<string>(await GetAttributionSourcesAsync(cancellationToken).ConfigureAwait(false));
        if (await CurrentServerAsync(cancellationToken).ConfigureAwait(false) is { } server && !hosts.Contains(server, StringComparer.OrdinalIgnoreCase))
        {
            hosts.Add(server);
        }

        return hosts;
    }

    /// <inheritdoc/>
    public Task<string?> ResolveHostAsync(Guid invocationId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    /// <inheritdoc/>
    public Task<string?> GetPdcHostAsync(CancellationToken cancellationToken) => CurrentServerAsync(cancellationToken);

    private async Task<string?> CurrentServerAsync(CancellationToken cancellationToken)
    {
        if (sessions.CurrentServer is { Length: > 0 } current)
        {
            return HostNames.Normalize(current);
        }

        try
        {
            var session = await sessions.GetSessionAsync(cancellationToken).ConfigureAwait(false);
            return HostNames.Normalize(session.Info.Server);
        }
        catch (DirectoryException)
        {
            return null;
        }
    }
}
