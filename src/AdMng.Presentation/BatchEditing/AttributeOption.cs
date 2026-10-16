using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.BatchEditing;

/// <summary>An attribute offered by attribute pickers (autocomplete): "Department [department]".</summary>
/// <param name="LdapName">LDAP display name written to the operation.</param>
/// <param name="DisplayName">Friendly, localized name.</param>
public sealed record AttributeOption(string LdapName, string DisplayName)
{
    /// <summary>"Display Name [ldapName]", or the display name alone when it only differs from the LDAP name in case.</summary>
    public string Text => string.Equals(DisplayName, LdapName, StringComparison.OrdinalIgnoreCase) ? DisplayName : $"{DisplayName} [{LdapName}]";

    public override string ToString() => Text;

    /// <summary>
    /// Writable, non-binary attributes allowed for objects of the given kinds according to the schema, sorted by
    /// friendly name. Empty when the schema is not available (free text input then).
    /// </summary>
    public static IReadOnlyList<AttributeOption> ForKinds(ISchemaCatalog schema, ILocalizer localizer, IEnumerable<ObjectKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(kinds);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kind in kinds.Distinct())
        {
            names.UnionWith(schema.GetAllowedAttributes(ClassesOf(kind)));
        }

        var comparer = StringComparer.Create(localizer.Culture, ignoreCase: true);
        return [.. names
            .Select(schema.FindAttribute)
            .OfType<AttributeDefinition>()
            .Where(d => !d.IsReadOnly && !schema.IsBinary(d.LdapName) && !IsNamingOrSecret(d.LdapName))
            .Select(d => new AttributeOption(d.LdapName, localizer.AttributeName(d.LdapName, schema)))
            .OrderBy(o => o.DisplayName, comparer)];
    }

    /// <summary>The objectClass chain of an object kind (structural classes; auxiliaries are added by the schema).</summary>
    public static IReadOnlyList<string> ClassesOf(ObjectKind kind) => kind switch
    {
        ObjectKind.User => ["top", "person", "organizationalPerson", "user"],
        ObjectKind.InetOrgPerson => ["top", "person", "organizationalPerson", "user", "inetOrgPerson"],
        ObjectKind.Computer => ["top", "person", "organizationalPerson", "user", "computer"],
        ObjectKind.Group => ["top", "group"],
        ObjectKind.Contact => ["top", "person", "organizationalPerson", "contact"],
        ObjectKind.OrganizationalUnit => ["top", "organizationalUnit"],
        ObjectKind.Container => ["top", "container"],
        _ => ["top"],
    };

    /// <summary>Attributes that are never set through a generic attribute operation (naming, class, secrets).</summary>
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "cn", "name", "ou", "dc", "distinguishedName", "objectClass", "unicodePwd", "userPassword",
    };

    private static bool IsNamingOrSecret(string attribute) => Excluded.Contains(attribute);
}
