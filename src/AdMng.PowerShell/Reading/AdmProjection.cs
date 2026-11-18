using System.Text.RegularExpressions;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Schema;

namespace AdMng.PowerShell.Reading;

/// <summary>The kind of object a command returns by default; decides the default attributes.</summary>
internal enum AdmObjectType
{
    Object,
    User,
    Group,
    Computer,
}

/// <summary>
/// Which attributes a command asks the server for and which of them every output object carries: the defaults of the
/// command's type plus the operator's <c>-Properties</c> (<c>*</c> = everything the account may read). Names are the
/// schema's own spelling; a name the schema does not know is refused before anything is read.
/// </summary>
internal sealed partial class AdmProjection
{
    private AdmProjection(IReadOnlyList<string> defaults, IReadOnlyList<string> requested, IReadOnlyList<string> explicitNames, bool all)
    {
        Defaults = defaults;
        Request = requested;
        Explicit = explicitNames;
        All = all;
    }

    /// <summary>The attributes of the command's type, present as properties when the object has a value.</summary>
    public IReadOnlyList<string> Defaults { get; }

    /// <summary>What is sent to the server (the defaults, the operator's names, and <c>*</c> for everything).</summary>
    public IReadOnlyList<string> Request { get; }

    /// <summary>The names the operator asked for: always a property, <c>$null</c> when the object has no value.</summary>
    public IReadOnlyList<string> Explicit { get; }

    /// <summary><c>-Properties *</c>: every attribute the server returns becomes a property.</summary>
    public bool All { get; }

    /// <summary>The default attributes of <paramref name="type"/>.</summary>
    public static IReadOnlyList<string> DefaultsOf(AdmObjectType type) => type switch
    {
        AdmObjectType.User =>
        [
            Attr.ObjectClass, Attr.Name, Attr.SamAccountName, Attr.UserPrincipalName, Attr.GivenName, Attr.Sn, Attr.DisplayName,
            Attr.Mail, Attr.Title, Attr.Department, Attr.Description, Attr.UserAccountControl, Attr.LockoutTime,
            Attr.UserAccountControlComputed, Attr.ObjectGuid, Attr.ObjectSid,
        ],
        AdmObjectType.Computer =>
        [
            Attr.ObjectClass, Attr.Name, Attr.SamAccountName, Attr.DnsHostName, Attr.OperatingSystem, Attr.OperatingSystemVersion,
            Attr.Description, Attr.Location, Attr.UserAccountControl, Attr.LastLogonTimestamp, Attr.ObjectGuid, Attr.ObjectSid,
        ],
        AdmObjectType.Group =>
        [
            Attr.ObjectClass, Attr.Name, Attr.SamAccountName, Attr.GroupType, Attr.Description, Attr.ManagedBy, Attr.ObjectGuid, Attr.ObjectSid,
        ],
        _ =>
        [
            Attr.ObjectClass, Attr.Name, Attr.Description, Attr.ObjectGuid, Attr.ObjectSid, Attr.SamAccountName, Attr.UserAccountControl,
            Attr.GroupType, Attr.LockoutTime, Attr.UserAccountControlComputed,
        ],
    };

    /// <summary>
    /// The projection of <paramref name="type"/> with the operator's <paramref name="properties"/>; <paramref name="error"/> says
    /// why a name is refused (an unknown attribute, or a text that is no attribute name).
    /// </summary>
    public static AdmProjection? Create(AdmObjectType type, IReadOnlyList<string>? properties, ISchemaCatalog schema, out string? error)
    {
        ArgumentNullException.ThrowIfNull(schema);
        error = null;
        var defaults = DefaultsOf(type).Select(name => Canonical(name, schema)).ToList();
        var request = new List<string>(defaults);
        var requested = new List<string>();
        var all = false;
        foreach (var raw in properties ?? [])
        {
            var name = raw?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (name == "*")
            {
                all = true;
                continue;
            }

            if (string.Equals(name, Attr.DistinguishedName, StringComparison.OrdinalIgnoreCase))
            {
                // Every object has its distinguished name; it is not an attribute to ask for.
                continue;
            }

            if (!AttributeName().IsMatch(name))
            {
                error = $"'{name}' is not an attribute name (letters, digits and '-'; '*' for all attributes).";
                return null;
            }

            if (schema.Attributes.Count > 0 && schema.FindAttribute(name) is null)
            {
                error = $"The schema of this domain has no attribute '{name}'. Press Tab after -Properties to see the attribute names.";
                return null;
            }

            var canonical = Canonical(name, schema);
            if (!requested.Contains(canonical, StringComparer.OrdinalIgnoreCase))
            {
                requested.Add(canonical);
            }

            if (!request.Contains(canonical, StringComparer.OrdinalIgnoreCase))
            {
                request.Add(canonical);
            }
        }

        if (all)
        {
            request.Insert(0, "*");
        }

        return new AdmProjection(defaults, request, requested, all);
    }

    private static string Canonical(string name, ISchemaCatalog schema) => schema.FindAttribute(name)?.LdapName ?? name;

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9-]*$")]
    private static partial Regex AttributeName();
}
