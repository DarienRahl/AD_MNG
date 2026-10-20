using System.Net;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Core.Detection;

namespace AdMng.Watchdog.Service.Attribution;

/// <summary>The writable DCs the WEF tailer accepts records from (stage B design 7.6 checks 2–4).</summary>
public interface ICollectorDomainControllerSource
{
    /// <summary>The writable DCs of the watched domain with their account names and addresses; empty while the DC list
    /// was never read.</summary>
    Task<IReadOnlyList<CollectorDomainController>> GetAsync(CancellationToken cancellationToken);
}

/// <summary>Forward DNS of a DC host name (check 4).</summary>
public interface IHostAddressResolver
{
    /// <summary>The A/AAAA records of <paramref name="host"/>; empty when it does not resolve.</summary>
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

/// <summary><see cref="IHostAddressResolver"/> over the system resolver (5 s per lookup).</summary>
public sealed class SystemHostAddressResolver(TimeProvider timeProvider) : IHostAddressResolver
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await Dns.GetHostAddressesAsync(host, linked.Token).ConfigureAwait(false);
        }
        catch (System.Net.Sockets.SocketException)
        {
            return [];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }
    }
}

/// <summary>
/// <see cref="ICollectorDomainControllerSource"/> over the detection's <see cref="DomainControllerDirectory"/> (writable DCs
/// of the domain, refreshed hourly through <see cref="IDomainControllerCatalog"/>): the account name is the
/// <c>sAMAccountName</c> of the server object's <c>serverReference</c> (read once per refresh; a renamed DC keeps its old
/// account name), falling back to the NetBIOS name + <c>$</c>; the addresses are resolved at every refresh and at least
/// every <see cref="AddressRefresh"/>.
/// </summary>
public sealed class DirectoryCollectorDcSource(
    IDomainControllerCatalog catalog,
    DomainControllerDirectory directory,
    IDirectorySessionProvider sessions,
    IHostAddressResolver resolver,
    TimeProvider timeProvider) : ICollectorDomainControllerSource
{
    /// <summary>Addresses are resolved again after this time even without a DC list refresh.</summary>
    public static readonly TimeSpan AddressRefresh = TimeSpan.FromMinutes(15);

    private const string ServerReference = "serverReference";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<CollectorDomainController> _current = [];
    private DateTimeOffset? _refreshedAt;
    private DateTimeOffset? _resolvedAt;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<CollectorDomainController>> GetAsync(CancellationToken cancellationToken)
    {
        await catalog.GetAttributionSourcesAsync(cancellationToken).ConfigureAwait(false);   // keeps the DC list fresh
        var now = timeProvider.GetUtcNow();
        if (directory.RefreshedAt == _refreshedAt && _resolvedAt is { } resolved && now - resolved < AddressRefresh)
        {
            return _current;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var refreshedAt = directory.RefreshedAt;
            var accounts = refreshedAt == _refreshedAt
                ? _current.ToDictionary(d => d.HostName, d => d.AccountName, StringComparer.OrdinalIgnoreCase)
                : null;
            var result = new List<CollectorDomainController>();
            foreach (var dc in directory.AttributionSources)
            {
                var host = HostNames.Normalize(dc.HostName!);
                var account = accounts?.GetValueOrDefault(host) ?? await AccountNameAsync(dc, cancellationToken).ConfigureAwait(false);
                var addresses = await resolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
                if (addresses.Count == 0 && _current.FirstOrDefault(c => c.HostName == host) is { } previous)
                {
                    addresses = [.. previous.Addresses];   // a failed lookup keeps the last known addresses
                }

                result.Add(new CollectorDomainController(host, account, addresses));
            }

            _current = result;
            _refreshedAt = refreshedAt;
            _resolvedAt = now;
            return _current;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> AccountNameAsync(KnownDomainController dc, CancellationToken cancellationToken)
    {
        var fallback = dc.NetBiosName + "$";
        try
        {
            if (DistinguishedName.Parse(dc.NtdsSettingsDn).Parent?.ToString() is not { } serverDn)
            {
                return fallback;
            }

            var session = await sessions.GetSessionAsync(cancellationToken).ConfigureAwait(false);
            var server = await session.GetAsync(serverDn, [ServerReference], cancellationToken).ConfigureAwait(false);
            if (server?.GetString(ServerReference) is not { } computerDn)
            {
                return fallback;
            }

            var computer = await session.GetAsync(computerDn, [Attr.SamAccountName], cancellationToken).ConfigureAwait(false);
            return computer?.GetString(Attr.SamAccountName) is { Length: > 1 } sam && sam.EndsWith('$') ? sam : fallback;
        }
        catch (DirectoryException)
        {
            return fallback;
        }
    }
}
