using System.Net;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>A writable DC of the watched domain as the collector checks its records (stage B design 7.6).</summary>
/// <param name="HostName">dNSHostName (normalized, lower case).</param>
/// <param name="AccountName">sAMAccountName of the DC's computer account, e.g. <c>DC01$</c>.</param>
/// <param name="Addresses">The DC's A/AAAA records (resolved at every DC directory refresh).</param>
public sealed record CollectorDomainController(string HostName, string AccountName, IReadOnlyCollection<IPAddress> Addresses);

/// <summary>Why a collector record was refused (<c>wec.&lt;dc&gt;.identityMismatch</c>, 7.6).</summary>
public enum CollectorRejection
{
    /// <summary>Accepted.</summary>
    None,

    /// <summary>Check 1: <c>meta.Subscription.Uuid</c> is not <c>Watchdog:Collector:SubscriptionUuid</c>.</summary>
    Subscription,

    /// <summary>Check 2: <c>meta.Client</c> is not <c>&lt;sAMAccountName&gt;@REALM</c> of a writable DC of the domain (an RODC,
    /// a DC of another domain, a member server), or the record is in another client's directory.</summary>
    Client,

    /// <summary>Check 3: <c>System/Computer</c> is not that DC's dNSHostName.</summary>
    Computer,

    /// <summary>Check 4: <c>meta.IpAddress</c> is neither one of the DC's addresses nor in
    /// <c>Watchdog:Collector:AdditionalSourceAddresses</c>.</summary>
    Address,

    /// <summary>Check 5: <c>System/Channel</c> is not <c>Security</c> or the provider fails <see cref="EventSourceGuard"/>.</summary>
    Channel,
}

/// <summary>
/// The identity checks of a collector record (stage B design 7.6), in two steps: <see cref="MatchClient"/> on the
/// OpenWEC metadata before the event XML is parsed (checks 1, 2 and 4) and <see cref="CheckRecord"/> on the parsed record
/// (checks 3 and 5). Every field read here is written by OpenWEC: the checks reject records of other forwarders that an
/// intact OpenWEC authenticated, not those of a compromised <c>openwec</c> user (4.3).
/// </summary>
public sealed class CollectorIdentityPolicy
{
    /// <summary>The channel every record must come from.</summary>
    public const string SecurityChannel = "Security";

    private readonly Dictionary<string, CollectorDomainController> _byAccount = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<IPAddress> _additional;

    /// <summary>Creates the policy of one ingestion pass.</summary>
    /// <param name="subscriptionUuid"><c>Watchdog:Collector:SubscriptionUuid</c>.</param>
    /// <param name="realm">The Kerberos realm of the domain (upper case DNS name).</param>
    /// <param name="domainControllers">The writable DCs of the domain.</param>
    /// <param name="additionalAddresses"><c>Watchdog:Collector:AdditionalSourceAddresses</c>.</param>
    public CollectorIdentityPolicy(Guid subscriptionUuid, string realm, IEnumerable<CollectorDomainController> domainControllers,
        IEnumerable<IPAddress> additionalAddresses)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(realm);
        ArgumentNullException.ThrowIfNull(domainControllers);
        ArgumentNullException.ThrowIfNull(additionalAddresses);
        SubscriptionUuid = subscriptionUuid;
        Realm = realm.Trim().TrimEnd('.').ToUpperInvariant();
        foreach (var dc in domainControllers)
        {
            _byAccount[dc.AccountName.Trim() + "@" + Realm] = dc with { HostName = HostNames.Normalize(dc.HostName), Addresses = [.. dc.Addresses.Select(Canonical)] };
        }

        _additional = [.. additionalAddresses.Select(Canonical)];
    }

    public Guid SubscriptionUuid { get; }

    public string Realm { get; }

    /// <summary>The DCs the policy accepts.</summary>
    public IReadOnlyCollection<CollectorDomainController> DomainControllers => _byAccount.Values;

    /// <summary>The OpenWEC client directory name of a principal: characters outside <c>[A-Za-z0-9.-_@]</c> removed
    /// (<c>DC01$@CONTOSO.COM</c> → <c>DC01@CONTOSO.COM</c>, pinned OpenWEC <c>sanitize_name</c>).</summary>
    public static string ClientDirectoryName(string principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return new string([.. principal.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '@').Take(255)]);
    }

    /// <summary>The DC whose client directory is <paramref name="clientDirectory"/>; null when none.</summary>
    public CollectorDomainController? ForDirectory(string clientDirectory)
    {
        ArgumentNullException.ThrowIfNull(clientDirectory);
        return _byAccount.Where(p => string.Equals(ClientDirectoryName(p.Key), clientDirectory, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Value).FirstOrDefault();
    }

    /// <summary>Checks 1, 2 and 4 on the metadata of <paramref name="record"/> found in <paramref name="clientDirectory"/>.</summary>
    public CollectorRejection MatchClient(WecRecord record, string clientDirectory, out CollectorDomainController? dc)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(clientDirectory);
        dc = null;
        if (record.SubscriptionUuid != SubscriptionUuid)
        {
            return CollectorRejection.Subscription;
        }

        var client = record.EffectiveClient?.Trim();
        if (string.IsNullOrEmpty(client) || !_byAccount.TryGetValue(client, out var found)
            || !string.Equals(ClientDirectoryName(client), clientDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return CollectorRejection.Client;
        }

        dc = found;
        if (!IPAddress.TryParse(record.IpAddress?.Trim(), out var address))
        {
            return CollectorRejection.Address;
        }

        address = Canonical(address);
        return found.Addresses.Contains(address) || _additional.Contains(address) ? CollectorRejection.None : CollectorRejection.Address;
    }

    /// <summary>Checks 3 and 5 on the parsed record of <paramref name="dc"/>.</summary>
    public static CollectorRejection CheckRecord(ParsedSecurityRecord record, CollectorDomainController dc)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(dc);
        if (!HostNames.AreEqual(record.Event.Computer, dc.HostName))
        {
            return CollectorRejection.Computer;
        }

        return string.Equals(record.Channel, SecurityChannel, StringComparison.OrdinalIgnoreCase)
            && EventSourceGuard.Accept(record, computer => HostNames.AreEqual(computer, dc.HostName))
            ? CollectorRejection.None
            : CollectorRejection.Channel;
    }

    private static IPAddress Canonical(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
