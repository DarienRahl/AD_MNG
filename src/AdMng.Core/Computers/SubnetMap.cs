using System.Globalization;
using System.Net;
using System.Net.Sockets;
using AdMng.Core.Ldap;

namespace AdMng.Core.Computers;

/// <summary>One subnet object: <c>cn</c> = <c>10.1.0.0/16</c> (or IPv6), its site.</summary>
/// <param name="Name">The cn as written.</param>
/// <param name="Network">The network address (host bits cleared).</param>
/// <param name="PrefixLength">Prefix length.</param>
/// <param name="SiteName">The site of siteObject, null when the subnet has none.</param>
public sealed record SubnetEntry(string Name, IPAddress Network, int PrefixLength, string? SiteName);

/// <summary>Where an address belongs.</summary>
/// <param name="Address">The address (an IPv4-mapped IPv6 address as IPv4).</param>
/// <param name="Subnet">The longest matching subnet, null when no subnet contains the address.</param>
public sealed record SubnetMatch(IPAddress Address, SubnetEntry? Subnet)
{
    public string? SiteName => Subnet?.SiteName;
}

/// <summary>
/// The subnets of the forest (K1 design note 4.4) for "which site is this computer in": an address matches the subnet
/// with the longest prefix (IPv4 and IPv6, an IPv4-mapped IPv6 address as IPv4). Invalid <c>cn</c> values are kept in
/// <see cref="Invalid"/> (reported once). In memory only.
/// </summary>
public sealed class SubnetMap
{
    private readonly List<(byte[] Network, int Prefix, SubnetEntry Entry)> _v4 = [];
    private readonly List<(byte[] Network, int Prefix, SubnetEntry Entry)> _v6 = [];

    private SubnetMap(IReadOnlyList<string> invalid) => Invalid = invalid;

    /// <summary>An empty map (no subnets).</summary>
    public static SubnetMap Empty { get; } = new([]);

    /// <summary>The cn values that are not <c>address/prefix</c>.</summary>
    public IReadOnlyList<string> Invalid { get; }

    /// <summary>Every valid subnet.</summary>
    public IReadOnlyList<SubnetEntry> Subnets => [.. _v4.Concat(_v6).Select(e => e.Entry)];

    /// <summary>Builds the map from subnet objects (cn, siteObject DN).</summary>
    public static SubnetMap Build(IEnumerable<(string Cn, string? SiteDn)> subnets)
    {
        ArgumentNullException.ThrowIfNull(subnets);
        var invalid = new List<string>();
        var valid = new List<SubnetEntry>();
        foreach (var (cn, siteDn) in subnets)
        {
            if (TryParse(cn, out var network, out var prefix))
            {
                var site = siteDn is not null && DistinguishedName.TryParse(siteDn, out var dn) && !dn.IsRoot ? dn.Name : null;
                valid.Add(new SubnetEntry(cn, network!, prefix, site));
            }
            else
            {
                invalid.Add(cn);
            }
        }

        var map = new SubnetMap(invalid);
        foreach (var entry in valid)
        {
            var bytes = entry.Network.GetAddressBytes();
            (entry.Network.AddressFamily == AddressFamily.InterNetwork ? map._v4 : map._v6).Add((bytes, entry.PrefixLength, entry));
        }

        // Longest prefix first: the first match is the most specific.
        map._v4.Sort((a, b) => b.Prefix.CompareTo(a.Prefix));
        map._v6.Sort((a, b) => b.Prefix.CompareTo(a.Prefix));
        return map;
    }

    /// <summary>Parses <c>10.1.0.0/16</c> / <c>2001:db8::/32</c>; the host bits are cleared.</summary>
    public static bool TryParse(string? cn, out IPAddress? network, out int prefixLength)
    {
        network = null;
        prefixLength = 0;
        if (string.IsNullOrWhiteSpace(cn))
        {
            return false;
        }

        var slash = cn.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0
            || !IPAddress.TryParse(cn.AsSpan(0, slash), out var address)
            || !int.TryParse(cn.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out prefixLength))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6) || prefixLength > bytes.Length * 8)
        {
            return false;
        }

        Mask(bytes, prefixLength);
        network = new IPAddress(bytes);
        return true;
    }

    /// <summary>The longest matching subnet of <paramref name="address"/>.</summary>
    public SubnetMatch Match(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var candidate = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        var list = candidate.AddressFamily == AddressFamily.InterNetwork ? _v4 : _v6;
        var bytes = candidate.GetAddressBytes();
        foreach (var (network, prefix, entry) in list)
        {
            var masked = (byte[])bytes.Clone();
            Mask(masked, prefix);
            if (masked.AsSpan().SequenceEqual(network))
            {
                return new SubnetMatch(candidate, entry);
            }
        }

        return new SubnetMatch(candidate, null);
    }

    private static void Mask(byte[] bytes, int prefixLength)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            var bits = Math.Clamp(prefixLength - (i * 8), 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - bits));
        }
    }
}
