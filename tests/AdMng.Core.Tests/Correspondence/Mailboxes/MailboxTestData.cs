using AdMng.Core.ActiveDirectory;
using AdMng.Core.Correspondence.Mailboxes;
using AdMng.Core.Ldap;
using AdMng.Core.Model;

namespace AdMng.Core.Tests.Correspondence.Mailboxes;

/// <summary>Persons, mailboxes and owner names of the mailbox owner tests (Polish names with diacritics on purpose).</summary>
internal static class MailboxTestData
{
    public const string DomainDn = "DC=contoso,DC=test";
    public const string PeopleOu = "OU=Pracownicy,OU=Firma," + DomainDn;
    public const string MailboxOu = "OU=Skrzynki,OU=Firma," + DomainDn;

    /// <summary>A person in OU=Pracownicy: enabled, with an address derived from the name unless told otherwise.</summary>
    public static PersonCandidate Person(
        string givenName,
        string surname,
        string? displayName = null,
        string? ou = null,
        bool disabled = false,
        bool withAddress = true,
        MailboxType type = MailboxType.None,
        string? cn = null)
    {
        var fullName = $"{givenName} {surname}".Trim();
        var rdn = cn ?? (fullName.Length > 0 ? fullName : displayName ?? "Osoba");
        var dn = DistinguishedName.Parse(ou ?? PeopleOu).Child("CN", rdn).ToString();
        var address = withAddress ? $"{PersonNameKey.Fold(fullName.Length > 0 ? fullName : displayName).Replace(' ', '.')}@contoso.test" : null;
        return new PersonCandidate(dn, givenName, surname, displayName ?? fullName, address, disabled, type, "IT", "Specjalista", null);
    }

    /// <summary>A shared mailbox in OU=Skrzynki with the given delegates.</summary>
    public static MailboxEntry Mailbox(string name = "Sekretariat", params string[] delegates) =>
        new(DistinguishedName.Parse(MailboxOu).Child("CN", name).ToString(), name, $"{PersonNameKey.Fold(name).Replace(' ', '.')}@contoso.test",
            MailboxType.Shared, delegates, IsDisabled: true);

    public static OwnerName Name(string givenName, string surname) => new(givenName, surname, $"{givenName} {surname}".Trim());

    public static DirectoryObject Object(string dn, params (string Attribute, object Value)[] values) =>
        new(dn, values.Select(v => new AttributeValues(v.Attribute, v.Value is string[] many ? many : [v.Value])));

    public static DirectoryObject Account(string cn, params (string Attribute, object Value)[] values) =>
        Object(DistinguishedName.Parse(MailboxOu).Child("CN", cn).ToString(),
            [(Attr.ObjectClass, new[] { "top", "person", "organizationalPerson", ObjectClasses.User }), .. values]);

    public static OwnerMatch Match(MailboxEntry mailbox, OwnerName? name, IEnumerable<PersonCandidate> persons, OwnerMatchOptions? options = null) =>
        MailboxOwnerMatcher.Match(mailbox, name, new PersonIndex(persons), options ?? new OwnerMatchOptions());
}
