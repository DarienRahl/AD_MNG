using AdMng.Core.ActiveDirectory;
using AdMng.Core.Model;

namespace AdMng.Presentation.Icons;

/// <summary>Icon identifiers. The WPF app maps each value to a Fluent glyph/image resource.</summary>
public enum ObjectIcon
{
    Unknown,
    User,
    UserDisabled,
    UserLocked,
    Computer,
    ComputerDisabled,
    Group,
    GroupDistribution,
    Contact,
    OrganizationalUnit,
    Container,
    Domain,
    Builtin,
    ForeignPrincipal,
    ServiceAccount,
    Printer,
    SharedFolder,
    Query,
    QuickFilter,
    Pinned,
    Folder,
}

/// <summary>Chooses the icon of a directory object (ADUC conventions: down arrow for disabled accounts, padlock for locked).</summary>
public static class ObjectIcons
{
    /// <summary>Icon for an object read with at least objectClass (and userAccountControl/lockoutTime/groupType for overlays).</summary>
    public static ObjectIcon For(DirectoryObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var groupType = obj.GetInt32(Attr.GroupType);
        return For(obj.Kind, obj.IsDisabled, obj.IsLockedOut, groupType is null || GroupTypes.IsSecurity(groupType.Value));
    }

    /// <summary>Icon from already evaluated state (used when the lock state was computed against the lockout policy).</summary>
    public static ObjectIcon For(ObjectKind kind, bool disabled, bool locked, bool securityGroup = true) => kind switch
    {
        ObjectKind.User or ObjectKind.InetOrgPerson => locked ? ObjectIcon.UserLocked : disabled ? ObjectIcon.UserDisabled : ObjectIcon.User,
        ObjectKind.Computer => disabled ? ObjectIcon.ComputerDisabled : ObjectIcon.Computer,
        ObjectKind.ManagedServiceAccount or ObjectKind.GroupManagedServiceAccount => ObjectIcon.ServiceAccount,
        ObjectKind.Group => securityGroup ? ObjectIcon.Group : ObjectIcon.GroupDistribution,
        ObjectKind.Contact => ObjectIcon.Contact,
        ObjectKind.OrganizationalUnit => ObjectIcon.OrganizationalUnit,
        ObjectKind.Container or ObjectKind.LostAndFound => ObjectIcon.Container,
        ObjectKind.Domain => ObjectIcon.Domain,
        ObjectKind.BuiltinDomain => ObjectIcon.Builtin,
        ObjectKind.ForeignSecurityPrincipal => ObjectIcon.ForeignPrincipal,
        ObjectKind.PrintQueue => ObjectIcon.Printer,
        ObjectKind.SharedFolder => ObjectIcon.SharedFolder,
        _ => ObjectIcon.Unknown,
    };
}
