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

    /// <summary>The filter of a binary security descriptor; null when there is none or it cannot be parsed.</summary>
    public static GroupPolicySecurityFilter? FromDescriptor(byte[]? descriptor)
    {
        if (descriptor is null || !SecurityDescriptorSummary.TryParse(descriptor, out var summary) || summary is null)
        {
            return null;
        }

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var denied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ace in summary.Aces)
        {
            // An inherit-only ACE does not apply to the container itself; an object ACE for another right does not grant Apply.
            if (ace.IsInheritOnly || (ace.Mask & ControlAccess) == 0 || (ace.ObjectType is { } right && right != ApplyGroupPolicyRight))
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

        return new GroupPolicySecurityFilter(allowed, denied);
    }

    /// <summary>A computer whose token holds <paramref name="tokenSids"/> processes the GPO.</summary>
    public bool Applies(IReadOnlySet<string> tokenSids)
    {
        ArgumentNullException.ThrowIfNull(tokenSids);
        return !Denied.Any(tokenSids.Contains) && Allowed.Any(tokenSids.Contains);
    }

    /// <summary>Every computer of the domain processes the GPO (Authenticated Users, Everyone or This Organization allowed, nothing denied).</summary>
    public bool AppliesToEveryComputer =>
        Denied.Count == 0 && (Allowed.Contains(AuthenticatedUsersSid) || Allowed.Contains(EveryoneSid) || Allowed.Contains(ThisOrganizationSid));

    /// <summary>For diagnostics: the counts, never the SIDs.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"Apply: {Allowed.Count} allowed, {Denied.Count} denied");
}
