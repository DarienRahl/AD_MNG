using AdMng.Core.ActiveDirectory;

namespace AdMng.Presentation.Lists;

/// <summary>
/// Default column sets (attribute LDAP names). Two pseudo columns exist: <see cref="TypeColumn"/>
/// (objectClass shown as the ADUC "Type" text) and <see cref="LocationColumn"/> (parent path computed from the DN,
/// not requested from the server).
/// </summary>
public static class ColumnSets
{
    /// <summary>"Type" column (objectClass rendered as "User", "Security Group - Global"...).</summary>
    public const string TypeColumn = Attr.ObjectClass;

    /// <summary>"Location" column: canonical path of the parent container (client side, no LDAP attribute).</summary>
    public const string LocationColumn = "$location";

    public const string ContainerLayoutKey = "container";
    public const string SearchLayoutKey = "search";
    public const string UsersLayoutKey = "users";
    public const string ComputersLayoutKey = "computers";
    public const string GroupsLayoutKey = "groups";

    /// <summary>ADUC default: Name, Type, Description.</summary>
    public static IReadOnlyList<string> Container { get; } = [Attr.Name, TypeColumn, Attr.Description];

    public static IReadOnlyList<string> Search { get; } = [Attr.Name, TypeColumn, LocationColumn, Attr.Description];

    public static IReadOnlyList<string> Users { get; } = [Attr.Name, Attr.SamAccountName, Attr.Title, Attr.Department, Attr.Mail];

    public static IReadOnlyList<string> Computers { get; } = [Attr.Name, Attr.OperatingSystem, Attr.LastLogonTimestamp, Attr.Description];

    public static IReadOnlyList<string> Groups { get; } = [Attr.Name, Attr.SamAccountName, Attr.GroupType, Attr.Description];

    /// <summary>Accounts with logon activity (inactive / never logged on views).</summary>
    public static IReadOnlyList<string> Activity { get; } =
        [Attr.Name, Attr.SamAccountName, Attr.LastLogonTimestamp, Attr.WhenCreated, LocationColumn];

    /// <summary>Accounts with expiry (expired / expiring views).</summary>
    public static IReadOnlyList<string> Expiry { get; } = [Attr.Name, Attr.SamAccountName, Attr.AccountExpires, LocationColumn];

    public static IReadOnlyList<string> Lockout { get; } = [Attr.Name, Attr.SamAccountName, Attr.LockoutTime, Attr.Department, LocationColumn];

    /// <summary>Attributes every list requests regardless of columns (icons, state, quick filter, type text).</summary>
    public static IReadOnlyList<string> BaseAttributes { get; } =
    [
        Attr.ObjectClass, Attr.Name, Attr.UserAccountControl, Attr.LockoutTime, Attr.Description,
        Attr.DisplayName, Attr.SamAccountName, Attr.GroupType,
    ];

    /// <summary>True for columns that are computed on the client (never sent to the server as attributes).</summary>
    public static bool IsPseudo(string attribute) => attribute.StartsWith('$');

    /// <summary>Columns the server can sort on (real, single valued-ish attributes; not Type/Location).</summary>
    public static bool IsServerSortable(string attribute) =>
        !IsPseudo(attribute) && !string.Equals(attribute, TypeColumn, StringComparison.OrdinalIgnoreCase);
}
