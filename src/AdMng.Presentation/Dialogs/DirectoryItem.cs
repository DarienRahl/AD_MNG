using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;

namespace AdMng.Presentation.Dialogs;

/// <summary>
/// A referenced directory object shown in a small list (members, memberships, direct reports, picker results,
/// batch targets): name, type and folder path. Immutable; lists are rebuilt when names get resolved.
/// </summary>
/// <param name="Dn">Distinguished name.</param>
/// <param name="Name">Display name (displayName, else the RDN value).</param>
/// <param name="Kind">Object kind (Unknown while not resolved / outside the domain).</param>
/// <param name="Icon">Icon (disabled/locked overlays included).</param>
/// <param name="TypeText">Localized ADUC type text ("User", "Security Group - Global").</param>
public sealed record DirectoryItem(string Dn, string Name, ObjectKind Kind, ObjectIcon Icon, string TypeText)
{
    /// <summary>Attributes needed by <see cref="From(DirectoryObject, ILocalizer)"/>.</summary>
    public static IReadOnlyList<string> Attributes { get; } =
    [
        Attr.ObjectClass, Attr.Name, Attr.DisplayName, Attr.UserAccountControl, Attr.LockoutTime, Attr.GroupType,
        Attr.Description, Attr.SamAccountName, Attr.Mail, Attr.ObjectSid,
    ];

    /// <summary>Folder (canonical path of the parent container), e.g. "contoso.test/Firma/IT".</summary>
    public string Path => DistinguishedName.TryParse(Dn, out var dn) && dn.Parent is { IsRoot: false } parent
        ? AddressResolver.ToDisplayPath(parent.ToString())
        : string.Empty;

    /// <summary>Description of the object when it was read with it.</summary>
    public string? Description { get; init; }

    /// <summary>E-mail address when it was read with it.</summary>
    public string? Mail { get; init; }

    /// <summary>sAMAccountName when it was read with it (pre-Windows 2000 logon name).</summary>
    public string? SamAccountName { get; init; }

    /// <summary>True when the object was found in the directory (false: only the DN is known).</summary>
    public bool IsResolved { get; init; } = true;

    /// <summary>Item for an object read with <see cref="Attributes"/>.</summary>
    public static DirectoryItem From(DirectoryObject obj, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(localizer);
        var name = obj.Kind is ObjectKind.User or ObjectKind.InetOrgPerson or ObjectKind.Contact
            && obj.GetString(Attr.DisplayName) is { Length: > 0 } display
            ? display
            : obj.Name;
        return new DirectoryItem(obj.DistinguishedName, name, obj.Kind, ObjectIcons.For(obj), localizer.TypeName(obj))
        {
            Description = obj.GetString(Attr.Description),
            Mail = obj.GetString(Attr.Mail),
            SamAccountName = obj.GetString(Attr.SamAccountName),
        };
    }

    /// <summary>Item for a DN that could not be read (RDN value as name, unknown type).</summary>
    public static DirectoryItem Unresolved(string distinguishedName, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(distinguishedName);
        ArgumentNullException.ThrowIfNull(localizer);
        var name = DistinguishedName.TryParse(distinguishedName, out var dn) && !dn.IsRoot ? dn.Name : distinguishedName;
        return new DirectoryItem(distinguishedName, name, ObjectKind.Unknown, ObjectIcon.Unknown, localizer.ObjectKindName(ObjectKind.Unknown))
        {
            IsResolved = false,
        };
    }
}
