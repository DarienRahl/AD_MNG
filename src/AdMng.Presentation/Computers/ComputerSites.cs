using System.Net;
using System.Net.Sockets;
using AdMng.Core.Computers;
using AdMng.Core.Computers.Remote;
using AdMng.Core.Directory;
using AdMng.Presentation.Connection;
using AdMng.Presentation.Lists;

namespace AdMng.Presentation.Computers;

/// <summary>The sites and subnets of the forest and the DCs of the domain (their sites), read once per use.</summary>
/// <param name="Topology">Sites and subnets.</param>
/// <param name="DomainControllers">The DCs with the sites of their server objects.</param>
public sealed record SiteData(SiteTopology Topology, IReadOnlyList<DirectoryDomainController> DomainControllers);

/// <summary>
/// "Ustal lokację" (K1 design note 4.4), shared by the review and the property sheet: a DC's site is its server object's;
/// any other computer's DNS name is resolved by the workstation's resolver and the address matched with the subnets of
/// the forest (longest prefix). Results exist only in memory.
/// </summary>
public static class ComputerSites
{
    /// <summary>DNS lookups at the same time.</summary>
    public const int Parallelism = 16;

    public static async Task<SiteData> ReadTopologyAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var topology = await SiteTopology.ReadAsync(session, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<DirectoryDomainController> controllers = [];
        try
        {
            controllers = await DomainControllerCatalog.ReadAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryException)
        {
            // Without the DC list the DCs are resolved through DNS like members.
        }

        return new SiteData(topology, controllers);
    }

    /// <summary>The site of each computer, keyed by <see cref="DnKey.For"/> of its DN.</summary>
    public static async Task<Dictionary<string, SiteResolution>> ResolveAsync(
        IHostAddressResolver resolver, SiteData topology, IReadOnlyList<ComputerFacts> computers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(computers);
        var result = new Dictionary<string, SiteResolution>(StringComparer.Ordinal);
        var gate = new Lock();
        await Parallel.ForEachAsync(computers, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken }, async (facts, ct) =>
        {
            var site = await ResolveOneAsync(resolver, topology, facts, ct).ConfigureAwait(false);
            lock (gate)
            {
                result[DnKey.For(facts.DistinguishedName)] = site;
            }
        }).ConfigureAwait(false);
        return result;
    }

    private static async Task<SiteResolution> ResolveOneAsync(IHostAddressResolver resolver, SiteData topology, ComputerFacts facts, CancellationToken cancellationToken)
    {
        var controller = facts.Type.IsDomainController() && facts.DnsHostName is { } dcHost
            ? topology.DomainControllers.FirstOrDefault(d => DomainControllerCatalog.IsSameHost(d.HostName, dcHost))
            : null;
        if (controller?.Site is { } dcSite)
        {
            return new SiteResolution(SiteResolutionState.DomainController, null, null, dcSite);
        }

        if (!HostNames.IsValid(facts.DnsHostName))
        {
            return new SiteResolution(SiteResolutionState.NotResolved, null, null, null);
        }

        IReadOnlyList<IPAddress> addresses;
        try
        {
            addresses = await resolver.ResolveAsync(facts.DnsHostName!, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or RemoteActionException or ArgumentException or TimeoutException)
        {
            addresses = [];
        }

        var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
        if (address is null)
        {
            return new SiteResolution(SiteResolutionState.NotResolved, null, null, null);
        }

        var match = topology.Topology.Subnets.Match(address);
        return match.Subnet is { } subnet
            ? new SiteResolution(SiteResolutionState.Resolved, match.Address, subnet.Name, subnet.SiteName)
            : new SiteResolution(SiteResolutionState.NoSubnet, match.Address, null, null);
    }
}
