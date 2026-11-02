using System.Globalization;
using AdMng.Core.Security;

namespace AdMng.Core.GroupPolicy;

/// <summary>An ACE of a GPO's DACL that grants or denies reading the GPO or applying it, in the DACL's order.</summary>
/// <param name="Sid">The trustee.</param>
/// <param name="Deny">A deny ACE (else an allow ACE).</param>
/// <param name="Apply">It covers the Apply Group Policy right.</param>
/// <param name="Read">It covers reading the container (read property for every property, or a generic read).</param>
public sealed record GroupPolicyFilterAce(string Sid, bool Deny, bool Apply, bool Read);

/// <summary>
/// The security filtering of a GPO: the principals the DACL of its container allows or denies the <c>Apply Group
/// Policy</c> extended right ([MS-GPOL] 3.2.5.1.6) and reading it. Built from a descriptor, a token is checked like
/// AccessCheck does: the ACEs in the DACL's order, the first one of the token that covers the right decides (in a
/// canonical DACL the denies come first; a deny written after an allow – a non-canonical DACL – does not take back what the
/// allow granted). Built from the sets only, a denied SID wins. Read from <c>nTSecurityDescriptor</c> (DACL only), in
/// memory for one run.
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

    /// <summary>The ACEs about reading or applying, in the DACL's order (empty when the filter was built from the sets).</summary>
    public IReadOnlyList<GroupPolicyFilterAce> Aces { get; init; } = [];

    /// <summary>The filter knows who may read the GPO (built from a descriptor, or with readers given).</summary>
    public bool KnowsReaders => Aces.Count > 0 || ReadAllowed.Count > 0 || ReadDenied.Count > 0;

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
        var ordered = new List<GroupPolicyFilterAce>();
        foreach (var ace in summary.Aces)
        {
            // An inherit-only ACE does not apply to the container itself; audit ACEs decide nothing.
            if (ace.IsInheritOnly || !(ace.IsAllow || ace.IsDeny))
            {
                continue;
            }

            // Reading: a generic read, or read property for every property (an object ACE names one property set only).
            var read = (ace.Mask & GenericRead) != 0 || ((ace.Mask & ReadProperty) != 0 && ace.ObjectType is null);

            // Applying: control access for every right or for Apply Group Policy (an object ACE for another right does not).
            var apply = (ace.Mask & ControlAccess) != 0 && (ace.ObjectType is null || ace.ObjectType == ApplyGroupPolicyRight);
            if (read)
            {
                (ace.IsDeny ? readDenied : readAllowed).Add(ace.Sid);
            }

            if (apply)
            {
                (ace.IsDeny ? denied : allowed).Add(ace.Sid);
            }

            if (read || apply)
            {
                ordered.Add(new GroupPolicyFilterAce(ace.Sid, ace.IsDeny, apply, read));
            }
        }

        return new GroupPolicySecurityFilter(allowed, denied) { ReadAllowed = readAllowed, ReadDenied = readDenied, Aces = ordered };
    }

    /// <summary>A computer (or user) whose token holds <paramref name="tokenSids"/> has the Apply Group Policy right.</summary>
    public bool Applies(IReadOnlySet<string> tokenSids)
    {
        ArgumentNullException.ThrowIfNull(tokenSids);
        return Aces.Count > 0
            ? Decide(tokenSids, a => a.Apply)
            : !Denied.Any(tokenSids.Contains) && Allowed.Any(tokenSids.Contains);
    }

    /// <summary>A token holding <paramref name="tokenSids"/> may read the GPO.</summary>
    public bool CanRead(IReadOnlySet<string> tokenSids)
    {
        ArgumentNullException.ThrowIfNull(tokenSids);
        return Aces.Count > 0
            ? Decide(tokenSids, a => a.Read)
            : !ReadDenied.Any(tokenSids.Contains) && ReadAllowed.Any(tokenSids.Contains);
    }

    /// <summary>The first ACE of the token that covers the right decides (AccessCheck's order); none: not granted.</summary>
    private bool Decide(IReadOnlySet<string> tokenSids, Func<GroupPolicyFilterAce, bool> covers)
    {
        foreach (var ace in Aces)
        {
            if (covers(ace) && tokenSids.Contains(ace.Sid))
            {
                return !ace.Deny;
            }
        }

        return false;
    }

    /// <summary>Every computer of the domain processes the GPO (Authenticated Users, Everyone or This Organization allowed, nothing denied).</summary>
    public bool AppliesToEveryComputer =>
        Denied.Count == 0 && (Allowed.Contains(AuthenticatedUsersSid) || Allowed.Contains(EveryoneSid) || Allowed.Contains(ThisOrganizationSid));

    /// <summary>For diagnostics: the counts, never the SIDs.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"Apply: {Allowed.Count} allowed, {Denied.Count} denied");
}
