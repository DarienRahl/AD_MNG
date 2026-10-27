namespace AdMng.Core.PrivilegedAccess;

/// <summary>
/// How much a right lets an account do beyond a plain domain user (README U4, design docs/dev/u4-privileged-access.md).
/// The order is the order of severity; an account's level is the highest level of its findings.
/// </summary>
public enum PrivilegeLevel
{
    /// <summary>Nothing beyond a plain domain user (never the level of a finding).</summary>
    None = 0,

    /// <summary>"Drobny": a small extra right (read event logs, DCOM, performance counters, an unusual group).</summary>
    Minor = 1,

    /// <summary>"Podwyższony": access to servers or a risky account setting (RDP, WinRM, SPN, no pre-authentication).</summary>
    Elevated = 2,

    /// <summary>"Wysoki": control over many objects or a path to Tier 0 (GPO creation, certificates, delegation).</summary>
    High = 3,

    /// <summary>Tier 0: control of the domain or the forest (Domain Admins, operators, DnsAdmins, unconstrained delegation).</summary>
    Tier0 = 4,
}

/// <summary>Helpers of <see cref="PrivilegeLevel"/>.</summary>
public static class PrivilegeLevels
{
    /// <summary>The levels a finding can have, highest first (tiles, filters).</summary>
    public static IReadOnlyList<PrivilegeLevel> Descending { get; } =
        [PrivilegeLevel.Tier0, PrivilegeLevel.High, PrivilegeLevel.Elevated, PrivilegeLevel.Minor];

    /// <summary>The higher of two levels.</summary>
    public static PrivilegeLevel Max(PrivilegeLevel a, PrivilegeLevel b) => a >= b ? a : b;

    /// <summary>The highest level of the values; <see cref="PrivilegeLevel.None"/> for none.</summary>
    public static PrivilegeLevel Max(IEnumerable<PrivilegeLevel> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        var max = PrivilegeLevel.None;
        foreach (var level in levels)
        {
            if (level > max)
            {
                max = level;
            }
        }

        return max;
    }

    /// <summary>A level from a hand-edited settings file brought into range (unknown or None becomes Minor).</summary>
    public static PrivilegeLevel Normalize(PrivilegeLevel level) =>
        Enum.IsDefined(level) && level != PrivilegeLevel.None ? level : PrivilegeLevel.Minor;
}
