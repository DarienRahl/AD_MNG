using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;

namespace AdMng.PowerShell.Reading;

/// <summary>How <c>Search-AdmObject</c> compares the value with the attribute.</summary>
public enum AdmMatch
{
    /// <summary>The attribute equals the value; <c>*</c> in the value is a literal.</summary>
    Exact,

    /// <summary>The attribute starts with the value.</summary>
    StartsWith,

    /// <summary>The attribute contains the value.</summary>
    Contains,

    /// <summary>The attribute ends with the value.</summary>
    EndsWith,
}

/// <summary>
/// Every filter the read commands send is built here, from <see cref="LdapFilter"/> (CLAUDE.md rule 3): a value typed by the
/// operator is escaped (RFC 4515), so <c>Łukasz (Kraków)*\</c> matches only itself, a value that looks like a filter
/// (<c>x)(objectClass=*</c>) is a value, and a ready filter (<c>-LdapFilter</c>) is syntax-checked first.
/// </summary>
internal static class AdmFilterBuilder
{
    /// <summary>The filter of a ready LDAP filter text, or the diagnostic of the syntax check.</summary>
    public static string? CheckRaw(string? filter, out string wrapped)
    {
        wrapped = filter is null ? string.Empty : LdapFilter.Wrap(filter);
        return LdapFilter.Validate(wrapped);
    }

    /// <summary>The filter that finds the object named by <paramref name="identity"/>; null for a DN (read by its base).</summary>
    public static string? ForIdentity(AdmIdentity identity, ObjectKind kind)
    {
        ArgumentNullException.ThrowIfNull(identity);
        switch (identity.Kind)
        {
            case AdmIdentityKind.DistinguishedName:
                return null;
            case AdmIdentityKind.Guid:
                return LdapFilter.EqualGuid(Attr.ObjectGuid, identity.ObjectGuid!.Value);
            case AdmIdentityKind.Sid:
                return LdapFilter.EqualBinary(Attr.ObjectSid, identity.SidBytes!);
            default:
                var account = identity.Value;
                return kind switch
                {
                    ObjectKind.Computer => LdapFilter.Or(
                        LdapFilter.Equal(Attr.SamAccountName, account),
                        account.EndsWith('$') ? null : LdapFilter.Equal(Attr.SamAccountName, account + "$")),
                    ObjectKind.Group => LdapFilter.Equal(Attr.SamAccountName, account),
                    _ => LdapFilter.Or(
                        LdapFilter.Equal(Attr.SamAccountName, account),
                        LdapFilter.Equal(Attr.UserPrincipalName, account)),
                };
        }
    }

    /// <summary>All of the filters that are not null; <c>(objectClass=*)</c> when there are none.</summary>
    public static string All(params string?[] filters) =>
        filters.Any(f => !string.IsNullOrWhiteSpace(f)) ? LdapFilter.And(filters) : AnyObject;

    /// <summary>The filter that matches every object.</summary>
    public const string AnyObject = "(objectClass=*)";

    /// <summary>The filter that limits a search to objects of <paramref name="kind"/> (the program's own, indexed, filter); null for any object.</summary>
    public static string? ForKind(ObjectKind kind) => kind == ObjectKind.Unknown ? null : kind.ToLdapFilter();

    /// <summary><c>(attr=pattern)</c> with <c>*</c> as the wildcard; null when <paramref name="pattern"/> is null.</summary>
    public static string? ForWildcard(string attribute, string? pattern) => pattern is null ? null : LdapFilter.Pattern(attribute, pattern);

    /// <summary>Enabled accounts (<paramref name="enabled"/> true) or disabled ones; null when not asked.</summary>
    public static string? ForEnabled(bool? enabled) => enabled switch
    {
        true => LdapFilter.Not(LdapFilter.BitAnd(Attr.UserAccountControl, (long)UserAccountControl.AccountDisable)),
        false => LdapFilter.BitAnd(Attr.UserAccountControl, (long)UserAccountControl.AccountDisable),
        null => null,
    };

    /// <summary>The group scope as the bits of <c>groupType</c>; null when not asked.</summary>
    public static string? ForGroupScope(GroupScope? scope) => scope switch
    {
        GroupScope.Global => LdapFilter.BitAnd(Attr.GroupType, (long)GroupTypeFlags.Global),
        GroupScope.DomainLocal => LdapFilter.BitAnd(Attr.GroupType, (long)GroupTypeFlags.DomainLocal),
        GroupScope.Universal => LdapFilter.BitAnd(Attr.GroupType, (long)GroupTypeFlags.Universal),
        GroupScope.BuiltinLocal => LdapFilter.BitAnd(Attr.GroupType, (long)GroupTypeFlags.BuiltinLocal),
        _ => null,
    };

    /// <summary>Security groups (<paramref name="security"/> true) or distribution groups; null when not asked.</summary>
    public static string? ForGroupCategory(bool? security) => security switch
    {
        true => LdapFilter.BitAnd(Attr.GroupType, (long)GroupTypeFlags.Security),
        false => LdapFilter.Not(LdapFilter.BitAnd(Attr.GroupType, (long)GroupTypeFlags.Security)),
        null => null,
    };

    /// <summary>
    /// The filter of <c>Search-AdmObject</c>: any of the <paramref name="attributes"/> matches any of the
    /// <paramref name="values"/> the way <paramref name="match"/> says; every value is escaped.
    /// </summary>
    public static string Match(IReadOnlyList<string> attributes, IReadOnlyList<string> values, AdmMatch match)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(values);
        var terms = new List<string>(attributes.Count * values.Count);
        foreach (var attribute in attributes)
        {
            foreach (var value in values)
            {
                terms.Add(Term(attribute, value, match));
            }
        }

        return LdapFilter.Or(terms);
    }

    /// <summary>Restricts <paramref name="filter"/> to one object class; <paramref name="objectClass"/> null leaves it as it is.</summary>
    public static string WithObjectClass(string? filter, string? objectClass) =>
        LdapFilter.And(filter, objectClass is null ? null : LdapFilter.Equal(Attr.ObjectClass, objectClass));

    private static string Term(string attribute, string value, AdmMatch match) => match switch
    {
        AdmMatch.StartsWith => LdapFilter.StartsWith(attribute, value),
        AdmMatch.Contains => LdapFilter.Contains(attribute, value),
        AdmMatch.EndsWith => LdapFilter.EndsWith(attribute, value),
        _ => LdapFilter.Equal(attribute, value),
    };
}
