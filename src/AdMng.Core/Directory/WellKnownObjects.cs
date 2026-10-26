using System.Globalization;
using AdMng.Core.Ldap;
using AdMng.Core.Model;

namespace AdMng.Core.Directory;

/// <summary>
/// The standard containers the domain head lists in <c>wellKnownObjects</c> / <c>otherWellKnownObjects</c>
/// ([MS-ADTS] 6.1.1.4). Their DNs can be redirected (<c>redirusr</c>, <c>redircmp</c>), so only the GUID identifies them.
/// </summary>
public enum WellKnownContainer
{
    Users,
    Computers,
    DomainControllers,
    System,
    LostAndFound,
    DeletedObjects,
    Infrastructure,
    ForeignSecurityPrincipals,
    ProgramData,

    /// <summary>CN=Microsoft below CN=Program Data.</summary>
    Microsoft,
    NtdsQuotas,

    /// <summary>From <c>otherWellKnownObjects</c>.</summary>
    ManagedServiceAccounts,

    /// <summary>From <c>otherWellKnownObjects</c> (Windows Server 2016 forest level).</summary>
    Keys,
}

/// <summary>The well-known object GUIDs of <see cref="WellKnownContainer"/> as they appear in the values (32 upper-case hex digits).</summary>
public static class WellKnownGuids
{
    /// <summary>GUID_USERS_CONTAINER_W.</summary>
    public const string Users = "A9D1CA15768811D1ADED00C04FD8D5CD";

    /// <summary>GUID_COMPUTRS_CONTAINER_W.</summary>
    public const string Computers = "AA312825768811D1ADED00C04FD8D5CD";

    /// <summary>GUID_DOMAIN_CONTROLLERS_CONTAINER_W.</summary>
    public const string DomainControllers = "A361B2FFFFD211D1AA4B00C04FD7D83A";

    /// <summary>GUID_SYSTEMS_CONTAINER_W.</summary>
    public const string System = "AB1D30F3768811D1ADED00C04FD8D5CD";

    /// <summary>GUID_LOSTANDFOUND_CONTAINER_W.</summary>
    public const string LostAndFound = "AB8153B7768811D1ADED00C04FD8D5CD";

    /// <summary>GUID_DELETED_OBJECTS_CONTAINER_W.</summary>
    public const string DeletedObjects = "18E2EA80684F11D2B9AA00C04F79F805";

    /// <summary>GUID_INFRASTRUCTURE_CONTAINER_W.</summary>
    public const string Infrastructure = "2FBAC1870ADE11D297C400C04FD8D5CD";

    /// <summary>GUID_FOREIGNSECURITYPRINCIPALS_CONTAINER_W.</summary>
    public const string ForeignSecurityPrincipals = "22B70C67D56E4EFB91E9300FCA3DC1AA";

    /// <summary>GUID_PROGRAM_DATA_CONTAINER_W.</summary>
    public const string ProgramData = "09460C08AE1E4A4EA0F64AEE7DAA1E5A";

    /// <summary>GUID_MICROSOFT_PROGRAM_DATA_CONTAINER_W.</summary>
    public const string Microsoft = "F4BE92A4C777485E878E9421D53087DB";

    /// <summary>GUID_NTDS_QUOTAS_CONTAINER_W.</summary>
    public const string NtdsQuotas = "6227F0AF1FC2410D8E3BB10615BB5B0F";

    /// <summary>GUID_MANAGED_SERVICE_ACCOUNTS_CONTAINER_W (otherWellKnownObjects).</summary>
    public const string ManagedServiceAccounts = "1EB93889E40C45DF9F0C64D23BBB6237";

    /// <summary>GUID_KEYS_CONTAINER_W (otherWellKnownObjects).</summary>
    public const string Keys = "683A24E2E8164BD3AF86AC3C2CF3F981";

    private static readonly Dictionary<string, WellKnownContainer> ByGuid = new(StringComparer.OrdinalIgnoreCase)
    {
        [Users] = WellKnownContainer.Users,
        [Computers] = WellKnownContainer.Computers,
        [DomainControllers] = WellKnownContainer.DomainControllers,
        [System] = WellKnownContainer.System,
        [LostAndFound] = WellKnownContainer.LostAndFound,
        [DeletedObjects] = WellKnownContainer.DeletedObjects,
        [Infrastructure] = WellKnownContainer.Infrastructure,
        [ForeignSecurityPrincipals] = WellKnownContainer.ForeignSecurityPrincipals,
        [ProgramData] = WellKnownContainer.ProgramData,
        [Microsoft] = WellKnownContainer.Microsoft,
        [NtdsQuotas] = WellKnownContainer.NtdsQuotas,
        [ManagedServiceAccounts] = WellKnownContainer.ManagedServiceAccounts,
        [Keys] = WellKnownContainer.Keys,
    };

    /// <summary>The GUID (32 upper-case hex digits) of a container.</summary>
    public static string Of(WellKnownContainer container) =>
        ByGuid.First(p => p.Value == container).Key;

    /// <summary>The container of a GUID (case-insensitive hex); false for GUIDs this application does not know.</summary>
    public static bool TryGetContainer(string guidHex, out WellKnownContainer container) =>
        ByGuid.TryGetValue(guidHex ?? string.Empty, out container);
}

/// <summary>Parsing of DN-with-binary values of <c>wellKnownObjects</c> / <c>otherWellKnownObjects</c>.</summary>
public static class WellKnownObjects
{
    public const string WellKnownObjectsAttribute = "wellKnownObjects";
    public const string OtherWellKnownObjectsAttribute = "otherWellKnownObjects";

    /// <summary>The attributes to read from the domain head.</summary>
    public static IReadOnlyList<string> Attributes { get; } = [WellKnownObjectsAttribute, OtherWellKnownObjectsAttribute];

    /// <summary>
    /// Splits a DN-with-binary value <c>B:&lt;hex digits&gt;:&lt;hex&gt;:&lt;dn&gt;</c>. The DN part may be in Extended DN
    /// form (<c>&lt;GUID=…&gt;;dn</c>); the plain DN is returned. Never throws.
    /// </summary>
    /// <param name="value">The attribute value.</param>
    /// <param name="binaryHex">The binary part in upper case (32 digits for a GUID).</param>
    /// <param name="distinguishedName">The DN part as returned by the server (without Extended DN components).</param>
    /// <returns>False when the prefix, the length, the hex digits or the DN are malformed.</returns>
    public static bool TryParseValue(string? value, out string binaryHex, out string distinguishedName)
    {
        binaryHex = distinguishedName = string.Empty;
        if (value is null || !value.StartsWith("B:", StringComparison.Ordinal))
        {
            return false;
        }

        var lengthEnd = value.IndexOf(':', 2);
        if (lengthEnd < 0
            || !int.TryParse(value.AsSpan(2, lengthEnd - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var length)
            || length == 0
            || length % 2 != 0

            // Compared without adding to the length: a prefix near int.MaxValue must not overflow into a negative index.
            || length >= value.Length - lengthEnd - 1
            || value[lengthEnd + 1 + length] != ':')
        {
            return false;
        }

        var hex = value.AsSpan(lengthEnd + 1, length);
        foreach (var c in hex)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        var dnPart = value[(lengthEnd + 2 + length)..];
        if (dnPart.Length == 0
            || !ExtendedDistinguishedName.TryParse(dnPart, out var extended)
            || !DistinguishedName.TryParse(extended.Dn, out var parsed)
            || parsed.IsRoot)
        {
            return false;
        }

        binaryHex = hex.ToString().ToUpperInvariant();
        distinguishedName = extended.Dn;
        return true;
    }
}

/// <summary>
/// Which DN each standard container has in this domain (from the domain head, read once per session and kept in memory).
/// Lookups never guess by name: a container is well known only when the domain head says so.
/// </summary>
public sealed class WellKnownObjectMap
{
    private readonly Dictionary<WellKnownContainer, string> _byContainer;
    private readonly Dictionary<DistinguishedName, WellKnownContainer> _byDn;

    private WellKnownObjectMap(Dictionary<WellKnownContainer, string> byContainer)
    {
        _byContainer = byContainer;
        _byDn = [];
        foreach (var (container, dn) in byContainer)
        {
            _byDn.TryAdd(DistinguishedName.Parse(dn), container);
        }
    }

    /// <summary>No well-known containers known (attributes not readable or not present).</summary>
    public static WellKnownObjectMap Empty { get; } = new([]);

    public bool IsEmpty => _byContainer.Count == 0;

    public int Count => _byContainer.Count;

    /// <summary>The known containers and their DNs.</summary>
    public IReadOnlyDictionary<WellKnownContainer, string> Containers => _byContainer;

    /// <summary>
    /// Builds the map from the values of both attributes. Malformed values and GUIDs this application does not know
    /// are ignored; for a GUID listed twice the first value wins.
    /// </summary>
    public static WellKnownObjectMap Parse(IEnumerable<string>? wellKnownObjects, IEnumerable<string>? otherWellKnownObjects = null)
    {
        var map = new Dictionary<WellKnownContainer, string>();
        foreach (var value in (wellKnownObjects ?? []).Concat(otherWellKnownObjects ?? []))
        {
            if (WellKnownObjects.TryParseValue(value, out var hex, out var dn) && WellKnownGuids.TryGetContainer(hex, out var container))
            {
                map.TryAdd(container, dn);
            }
        }

        return map.Count == 0 ? Empty : new WellKnownObjectMap(map);
    }

    /// <summary>The map of a domain head read with <see cref="WellKnownObjects.Attributes"/>.</summary>
    public static WellKnownObjectMap FromDomainObject(DirectoryObject domain)
    {
        ArgumentNullException.ThrowIfNull(domain);
        return Parse(
            domain.GetStrings(WellKnownObjects.WellKnownObjectsAttribute),
            domain.GetStrings(WellKnownObjects.OtherWellKnownObjectsAttribute));
    }

    /// <summary>The DN of a container in this domain, or null when the domain head does not list it.</summary>
    public string? Find(WellKnownContainer container) => _byContainer.TryGetValue(container, out var dn) ? dn : null;

    /// <summary>The well-known role of a container (DNs compared parsed: case and escaping do not matter), or null.</summary>
    public WellKnownContainer? Identify(string? distinguishedName) =>
        distinguishedName is not null
        && _byDn.Count > 0
        && DistinguishedName.TryParse(distinguishedName, out var dn)
        && _byDn.TryGetValue(dn, out var container)
            ? container
            : null;
}
