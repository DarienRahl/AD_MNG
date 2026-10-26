namespace AdMng.Core.PrivilegedAccess;

/// <summary>
/// A right in an ACL that the privileged access review reports (README U4b, design docs/dev/u4-privileged-access.md
/// section 6). The values of an <see cref="PrivilegeFindingKind.AclRight"/> finding are these names, optionally with a
/// detail (<see cref="AclRights.Format"/>); the UI shows them in its language (resource keys
/// <c>PrivilegedAccess.AclRight.{AclRight}</c>).
/// </summary>
public enum AclRight
{
    /// <summary>Full control (GENERIC_ALL or every specific right).</summary>
    GenericAll,

    /// <summary>WRITE_DAC: may grant itself any right.</summary>
    WriteDacl,

    /// <summary>WRITE_OWNER: may take ownership (and then change the DACL).</summary>
    WriteOwner,

    /// <summary>The owner of the object (implicitly may change the DACL).</summary>
    Owner,

    /// <summary>Every extended right (control access without an object type): reset password, replication, LAPS …</summary>
    AllExtendedRights,

    /// <summary>Write every property (write property without an object type, GENERIC_WRITE).</summary>
    WriteAllProperties,

    /// <summary>DS-Replication-Get-Changes-All on the domain head: with Get-Changes, DCSync of every secret.</summary>
    ReplicationGetChangesAll,

    /// <summary>DS-Replication-Get-Changes on the domain head (without -All: no secrets).</summary>
    ReplicationGetChanges,

    /// <summary>DS-Replication-Get-Changes-In-Filtered-Set on the domain head.</summary>
    ReplicationGetChangesInFilteredSet,

    /// <summary>User-Force-Change-Password: reset the password without knowing it.</summary>
    ResetPassword,

    /// <summary>Write the <c>member</c> attribute (or the Membership property set): change the members of groups.</summary>
    WriteMember,

    /// <summary>Self-Membership validated write: add or remove itself to/from groups.</summary>
    AddSelfToGroup,

    /// <summary>Write <c>gPLink</c>: link any GPO to the container.</summary>
    WriteGpLink,

    /// <summary>Write a property of a group policy container (edits the GPO).</summary>
    WriteGpoProperty,

    /// <summary>Read the LAPS password (control access on <c>ms-Mcs-AdmPwd</c>, <c>msLAPS-Password</c> …).</summary>
    ReadLapsPassword,

    /// <summary>Write <c>msDS-KeyCredentialLink</c> (shadow credentials).</summary>
    WriteKeyCredentialLink,

    /// <summary>Write <c>msDS-AllowedToActOnBehalfOfOtherIdentity</c> (configure resource-based delegation).</summary>
    WriteAllowedToActOnBehalf,

    /// <summary>Listed in <c>msDS-AllowedToActOnBehalfOfOtherIdentity</c> of the object: may impersonate any user to it.</summary>
    ActOnBehalfOfOtherIdentity,

    /// <summary>Create child objects (of a class, or of any class).</summary>
    CreateChild,

    /// <summary>Delete child objects (of a class, or of any class).</summary>
    DeleteChild,
}

/// <summary>The value format of <see cref="AclRight"/>s in <see cref="PrivilegeFinding.Values"/>: <c>Right</c> or <c>Right:detail</c>.</summary>
public static class AclRights
{
    /// <summary>
    /// <c>ResetPassword</c>, <c>ResetPassword:user</c> (the class the ACE applies to), <c>CreateChild:computer</c> (the
    /// class that may be created), <c>ReadLapsPassword:msLAPS-Password</c> (the attribute).
    /// </summary>
    public static string Format(AclRight right, string? detail = null) =>
        string.IsNullOrEmpty(detail) ? right.ToString() : $"{right}:{detail}";

    /// <summary>Reads a value made by <see cref="Format"/>; false for any other value.</summary>
    public static bool TryParse(string? value, out AclRight right, out string? detail)
    {
        right = default;
        detail = null;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var separator = value.IndexOf(':', StringComparison.Ordinal);
        var name = separator < 0 ? value : value[..separator];
        if (!Enum.TryParse(name, ignoreCase: false, out right) || !Enum.IsDefined(right) || char.IsDigit(name[0]))
        {
            return false;
        }

        detail = separator < 0 ? null : value[(separator + 1)..];
        return true;
    }
}
