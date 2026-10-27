using AdMng.Core.Security;

namespace AdMng.Core.GroupPolicy.Management;

/// <summary>A trustee's permission on a GPO as GPMC names it.</summary>
public enum GpoPermissionLevel
{
    /// <summary>Read.</summary>
    Read,

    /// <summary>Read + Apply Group Policy (security filtering).</summary>
    Apply,

    /// <summary>Edit settings.</summary>
    Edit,

    /// <summary>Edit settings, delete, modify security.</summary>
    EditDeleteModifySecurity,

    /// <summary>ACEs that match no level (deny ACEs, partial rights): removed only as a whole.</summary>
    Custom,
}

/// <summary>One trustee of a GPO with its level.</summary>
/// <param name="Sid">Trustee SID.</param>
/// <param name="Level">The highest level the trustee's ACEs give.</param>
/// <param name="Inherited">The rights come only from inherited ACEs (they cannot be changed on the GPO).</param>
public sealed record GpoPermission(string Sid, GpoPermissionLevel Level, bool Inherited)
{
    /// <summary>
    /// The trustee is allowed Apply Group Policy (security filtering), whatever its level - a trustee with Edit settings and
    /// Apply is <see cref="GpoPermissionLevel.Edit"/> but in the security filtering too.
    /// </summary>
    public bool Applies { get; init; }
}

/// <summary>
/// The GPMC permission levels of a GPO's container (design note docs/dev/k-computers-and-gpo.md 5.3): <i>Read</i> =
/// generic read (0x20094); <i>Apply</i> = Read + the control access right Apply Group Policy; <i>Edit settings</i> = Read +
/// write property + self + create child + delete child (0x200BF); <i>Edit settings, delete, modify security</i> = Edit +
/// DELETE + WRITE_DAC + WRITE_OWNER (0xF00BF). The ACEs are container-inherit so they reach <c>CN=Machine</c> and
/// <c>CN=User</c>. A trustee whose ACEs match none of the levels is <see cref="GpoPermissionLevel.Custom"/>. The masks are
/// pinned by fixtures from GPMC in the pilot (design note 12).
/// </summary>
public static class GpoPermissionLevels
{
    public const int ReadMask = 0x00020094;
    public const int EditMask = 0x000200BF;
    public const int EditDeleteModifySecurityMask = 0x000F00BF;
    public const int ControlAccess = 0x00000100;

    /// <summary>Authenticated Users.</summary>
    public const string AuthenticatedUsersSid = GroupPolicySecurityFilter.AuthenticatedUsersSid;

    private const byte ContainerInherit = SecurityDescriptorAce.ContainerInheritFlag;

    /// <summary>The ACEs of a level for a trustee.</summary>
    public static IReadOnlyList<AceSpec> AcesFor(string sid, GpoPermissionLevel level, string? trusteeName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);
        AceSpec Plain(int mask) => new() { Qualifier = AceQualifier.Allow, Flags = ContainerInherit, Mask = mask, TrusteeSid = sid, TrusteeName = trusteeName };
        return level switch
        {
            GpoPermissionLevel.Read => [Plain(ReadMask)],
            GpoPermissionLevel.Apply => [Plain(ReadMask), ApplyAce(sid, trusteeName)],
            GpoPermissionLevel.Edit => [Plain(EditMask)],
            GpoPermissionLevel.EditDeleteModifySecurity => [Plain(EditDeleteModifySecurityMask)],
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, "A custom level has no ACEs of its own."),
        };
    }

    /// <summary>The Apply Group Policy ACE (control access, container inherit).</summary>
    public static AceSpec ApplyAce(string sid, string? trusteeName = null) => new()
    {
        Qualifier = AceQualifier.Allow,
        Flags = ContainerInherit,
        Mask = ControlAccess,
        TrusteeSid = sid,
        ObjectType = GroupPolicySecurityFilter.ApplyGroupPolicyRight,
        TrusteeName = trusteeName,
    };

    /// <summary>The trustees of a container's descriptor with their levels (explicit before inherited), sorted by SID.</summary>
    public static IReadOnlyList<GpoPermission> Read(byte[]? descriptor)
    {
        if (descriptor is null || !SecurityDescriptorSummary.TryParse(descriptor, out var summary) || summary is null)
        {
            return [];
        }

        return [.. summary.Aces
            .GroupBy(a => a.Sid, StringComparer.OrdinalIgnoreCase)
            .Select(g => new GpoPermission(g.Key, LevelOf(g.ToList()), g.All(a => a.IsInherited)) { Applies = AppliesOf(g.ToList()) })
            .OrderBy(p => p.Sid, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The level of one trustee's ACEs (inherit-only ACEs do not count for the container itself).</summary>
    public static GpoPermissionLevel LevelOf(IReadOnlyList<SecurityDescriptorAce> aces)
    {
        ArgumentNullException.ThrowIfNull(aces);
        if (aces.Any(a => a.IsDeny))
        {
            return GpoPermissionLevel.Custom;
        }

        var effective = aces.Where(a => a.IsAllow && !a.IsInheritOnly).ToList();
        var mask = effective.Where(a => a.ObjectType is null).Aggregate(0, (m, a) => m | DirectoryAccessMask.Normalize(a.Mask));
        var apply = effective.Any(a => (a.Mask & ControlAccess) != 0 && (a.ObjectType is null || a.ObjectType == GroupPolicySecurityFilter.ApplyGroupPolicyRight));
        if ((mask & EditDeleteModifySecurityMask) == EditDeleteModifySecurityMask)
        {
            return GpoPermissionLevel.EditDeleteModifySecurity;
        }

        if ((mask & EditMask) == EditMask)
        {
            return GpoPermissionLevel.Edit;
        }

        if ((mask & ReadMask) == ReadMask)
        {
            return apply ? GpoPermissionLevel.Apply : GpoPermissionLevel.Read;
        }

        return GpoPermissionLevel.Custom;
    }

    /// <summary>
    /// Whether one trustee's ACEs allow Apply Group Policy on the container itself (inherit-only ACEs do not count) and no
    /// deny ACE takes it away - independent of <see cref="LevelOf"/>.
    /// </summary>
    public static bool AppliesOf(IReadOnlyList<SecurityDescriptorAce> aces)
    {
        ArgumentNullException.ThrowIfNull(aces);
        var effective = aces.Where(a => !a.IsInheritOnly).ToList();
        return effective.Any(a => a.IsAllow && IsApply(a.Mask, a.ObjectType)) && !effective.Any(a => a.IsDeny && IsApply(a.Mask, a.ObjectType));
    }

    /// <summary>An ACE that grants (or denies) Apply Group Policy: control access for that right or for all extended rights.</summary>
    public static bool IsApply(int mask, Guid? objectType) =>
        (mask & ControlAccess) != 0 && (objectType is null || objectType == GroupPolicySecurityFilter.ApplyGroupPolicyRight);

    /// <summary>The explicit ACEs of a trustee in a descriptor (what a level change or a removal takes away).</summary>
    public static IReadOnlyList<AceSpec> ExplicitAcesOf(byte[]? descriptor, string sid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);
        if (descriptor is null || !SecurityDescriptorSummary.TryParse(descriptor, out var summary) || summary is null)
        {
            return [];
        }

        return [.. summary.Aces
            .Where(a => string.Equals(a.Sid, sid, StringComparison.OrdinalIgnoreCase))
            .Select(AceSpec.FromAce)
            .OfType<AceSpec>()];
    }

    /// <summary>
    /// MS16-072: true when Authenticated Users or Domain Computers (<paramref name="domainComputersSid"/>) can read the GPO
    /// with these permissions - clients read user-side GPOs in the computer's context.
    /// </summary>
    public static bool ComputersCanRead(IEnumerable<GpoPermission> permissions, string? domainComputersSid)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        return permissions.Any(p => p.Level != GpoPermissionLevel.Custom
            && (string.Equals(p.Sid, AuthenticatedUsersSid, StringComparison.OrdinalIgnoreCase)
                || (domainComputersSid is not null && string.Equals(p.Sid, domainComputersSid, StringComparison.OrdinalIgnoreCase))));
    }
}
