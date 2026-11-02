using System.Globalization;
using AdMng.Core.Security;

namespace AdMng.Core.GroupPolicy;

/// <summary>
/// The security filtering of a GPO: the principals the DACL of its container allows or denies the <c>Apply Group
/// Policy</c> extended right ([MS-GPOL] 3.2.5.1.6). A computer processes the GPO only when its token holds an allowed SID
/// and no denied one (deny ACEs win, as in the canonical order of a DACL). Read from <c>nTSecurityDescriptor</c> (DACL
/// only), in memory for one run.
/// </summary>
/// <param name="Allowed">SIDs with an allow ACE for Apply Group Policy (or full control access).</param>
/// <param name="Denied">SIDs with a deny ACE for it.</param>
public sealed record GroupPolicySecurityFilter(IReadOnlySet<string> Allowed, IReadOnlySet<string> Denied)
{
    private static readonly IReadOnlySet<string> NoSids = new HashSet<string>();

    /// <summary>The Apply-Group-Policy extended right.</summary>
    public static readonly Guid ApplyGroupPolicyRight = new("edacfd8f-ffb3-11d1-b41d-00a0c968f939");

    /// <summary>Everyone.</summary>
    public const string EveryoneSid = "S-1-1-0";

    /// <summary>Enterprise Domain Controllers: in the token of every writable DC.</summary>
    public const string EnterpriseDomainControllersSid = "S-1-5-9";

    /// <summary>Authenticated Users.</summary>
    public const string AuthenticatedUsersSid = "S-1-5-11";

    /// <summary>This Organization.</summary>
    public const string ThisOrganizationSid = "S-1-5-15";

    /// <summary>ADS_RIGHT_DS_CONTROL_ACCESS.</summary>
    private const int ControlAccess = 0x100;

    /// <summary>ADS_RIGHT_DS_READ_PROPERTY.</summary>
    private const int ReadProperty = 0x10;

    /// <summary>GENERIC_ALL | GENERIC_READ (generic rights of an ACE before mapping).</summary>
    private const int GenericRead = 0x10000000 | unchecked((int)0x80000000);

    /// <summary>
    /// SIDs allowed to read the GPO's container (read property for every property, or a generic read): since MS16-072 a
    /// computer reads the GPOs of its user's policy with its own token, so the computer must be one of them (K5).
    /// </summary>
    public IReadOnlySet<string> ReadAllowed { get; init; } = NoSids;

    /// <summary>SIDs denied reading the container.</summary>
    public IReadOnlySet<string> ReadDenied { get; init; } = NoSids;

    /// <summary>The filter of a binary security descriptor; null when there is none or it cannot be parsed.</summary>
    public static GroupPolicySecurityFilter? FromDescriptor(byte[]? descriptor)
    {
        if (descriptor is null || !SecurityDescriptorSummary.TryParse(descriptor, out var summary) || summary is null)
        {
            return null;
        }

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var denied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var readAllowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var readDenied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ace in summary.Aces)
        {
            // An inherit-only ACE does not apply to the container itself.
            if (ace.IsInheritOnly)
            {
                continue;
            }

            // Reading: a generic read, or read property for every property (an object ACE names one property set only).
            if ((ace.Mask & GenericRead) != 0 || ((ace.Mask & ReadProperty) != 0 && ace.ObjectType is null))
            {
                (ace.IsDeny ? readDenied : ace.IsAllow ? readAllowed : null)?.Add(ace.Sid);
            }

            // An object ACE for another right does not grant Apply.
            if ((ace.Mask & ControlAccess) == 0 || (ace.ObjectType is { } right && right != ApplyGroupPolicyRight))
            {
                continue;
            }

            if (ace.IsDeny)
            {
                denied.Add(ace.Sid);
            }
            else if (ace.IsAllow)
            {
                allowed.Add(ace.Sid);
            }
        }

        return new GroupPolicySecurityFilter(allowed, denied) { ReadAllowed = readAllowed, ReadDenied = readDenied };
    }

    /// <summary>A computer whose token holds <paramref name="tokenSids"/> processes the GPO.</summary>
    public bool Applies(IReadOnlySet<string> tokenSids)
    {
        ArgumentNullException.ThrowIfNull(tokenSids);
        return !Denied.Any(tokenSids.Contains) && Allowed.Any(tokenSids.Contains);
    }

    /// <summary>A token holding <paramref name="tokenSids"/> may read the GPO (an allowed SID and no denied one).</summary>
    public bool CanRead(IReadOnlySet<string> tokenSids)
    {
        ArgumentNullException.ThrowIfNull(tokenSids);
        return !ReadDenied.Any(tokenSids.Contains) && ReadAllowed.Any(tokenSids.Contains);
    }

    /// <summary>Every computer of the domain processes the GPO (Authenticated Users, Everyone or This Organization allowed, nothing denied).</summary>
    public bool AppliesToEveryComputer =>
        Denied.Count == 0 && (Allowed.Contains(AuthenticatedUsersSid) || Allowed.Contains(EveryoneSid) || Allowed.Contains(ThisOrganizationSid));

    /// <summary>For diagnostics: the counts, never the SIDs.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"Apply: {Allowed.Count} allowed, {Denied.Count} denied");
}
