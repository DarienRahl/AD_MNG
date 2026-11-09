using System.Collections.Frozen;
using System.Text;
using AdMng.Core.Model;
using AdMng.Core.Templates;

namespace AdMng.Core.Batch.Creation;

/// <summary>One entry of the header synonym table of docs/dev/phase3-bulk-create.md 2.1.</summary>
/// <param name="Folded">The folded header (<see cref="ColumnSynonyms.Fold"/>), e.g. <c>telefonkomorkowy</c>.</param>
/// <param name="Target">The suggested target.</param>
/// <param name="OnlyFor">The object kind the entry applies to (kind-dependent entries such as <em>nazwa</em>); null = every kind.</param>
public sealed record ColumnSynonym(string Folded, ColumnSuggestion Target, ObjectKind? OnlyFor)
{
    /// <summary>The target and the kind only (the synonym itself is a header text).</summary>
    public override string ToString() => $"Synonym → {Target} ({OnlyFor?.ToString() ?? "every kind"})";
}

/// <summary>
/// Automatic column mapping by Polish and English header synonyms and by content (docs/dev/phase3-bulk-create.md 2.1,
/// owner B1). Exact matches of the folded header only (no substring matching), never a blocked attribute, never a
/// password-like column; pure and thread-safe.
/// </summary>
/// <remarks>
/// A target the object kind cannot have is never suggested: users get every entry except the computers' <c>location</c>;
/// contacts no account attributes (<c>sAMAccountName</c>, UPN, home folder, logon script, profile, <c>ExpiresAt</c>);
/// groups only <c>cn</c>, <c>sAMAccountName</c>, <c>mail</c>, <c>displayName</c>, <c>description</c>, <c>info</c>,
/// <c>wWWHomePage</c>; computers only <c>cn</c>, <c>sAMAccountName</c>, <c>displayName</c>, <c>description</c>,
/// <c>location</c>; every kind <c>Manager</c>, <c>Groups</c> and <em>Tylko we wzorcach</em>. With the attribute catalogue
/// of the class (<c>attributeNames</c>) an attribute target must also be in it.
/// </remarks>
public static class ColumnSynonyms
{
    /// <summary>The <em>telefon</em> family whose content decides between <c>telephoneNumber</c> and <c>mobile</c> (2.1).</summary>
    private static readonly FrozenSet<string> TelephoneFamily = new[] { "telefon", "tel", "nrtel", "nrtelefonu", "numertelefonu" }
        .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The folded header that is a department unless 80 % of its samples are phone numbers (<em>Komórka</em>).</summary>
    private const string Komorka = "komorka";

    private static readonly string[] PasswordSubstrings = ["haslo", "hasla", "password", "passwd", "pwd", "kennwort"];

    private static readonly FrozenSet<string> PasswordExact = new[] { "pin", "kodpin", "pass" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> IdentifierExact = new[]
    {
        "pesel", "nrpesel", "numerpesel", "nip", "nrnip", "numernip", "dowod", "nrdowodu", "numerdowodu", "dowodosobisty",
        "nrdowoduosobistego", "dataurodzenia", "birthdate", "dateofbirth", "ssn", "iban",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly string[] IdentifierSubstrings = ["pesel", "dataurodzenia", "birthdate", "dateofbirth", "dowodosobist"];

    private static readonly FrozenSet<string> GroupAttributes = new[]
    {
        "cn", "sAMAccountName", "mail", "displayName", "description", "info", "wWWHomePage",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> ComputerAttributes = new[]
    {
        "cn", "sAMAccountName", "displayName", "description", "location",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> NotForContacts = new[]
    {
        "sAMAccountName", "userPrincipalName", "homeDirectory", "homeDrive", "scriptPath", "profilePath", "location",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyList<ColumnSynonym> Table = BuildTable();

    /// <summary>Folded header → its entries (one per kind for the kind-dependent ones), in table order.</summary>
    private static readonly FrozenDictionary<string, ColumnSynonym[]> ByFolded = Table
        .GroupBy(e => e.Folded, StringComparer.Ordinal)
        .ToFrozenDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

    /// <summary>The folded headers and the folded LDAP names (3+ characters, plus <c>cn</c> and <c>sn</c>) of the table's targets – what header detection counts as a known header.</summary>
    private static readonly FrozenSet<string> KnownHeaders = Table.Select(e => e.Folded)
        .Concat(Table.Select(e => e.Target.Attribute).OfType<string>().Select(Fold).Where(f => f.Length >= 3))
        .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The synonym table (2.1).</summary>
    public static IReadOnlyList<ColumnSynonym> Entries => Table;

    /// <summary>
    /// Lower-case invariant, diacritics removed, every character that is not a letter or digit removed
    /// (<em>Tel. kom.</em> → <c>telkom</c>, <em>Adres e-mail</em> → <c>adresemail</c>).
    /// </summary>
    public static string Fold(string header)
    {
        ArgumentNullException.ThrowIfNull(header);
        var text = Diacritics.Remove(header.ToLowerInvariant());
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// The target of a column: the folded header matched exactly against the synonym table, then against
    /// <paramref name="attributeNames"/> (LDAP names of the attribute catalogue), refined by <paramref name="samples"/> (the
    /// first 20 non-empty cells, e.g. a <em>Telefon</em> column of mobile numbers → <c>mobile</c>); null = no suggestion.
    /// </summary>
    /// <remarks>
    /// Password-like and personal-identifier headers get no suggestion. The <em>telefon</em> family becomes <c>mobile</c>
    /// (<see cref="SuggestionNote.MobileNumbersDetected"/>) when 80 % of the samples are Polish mobile numbers;
    /// <em>Komórka</em> is <c>mobile</c> only when 80 % of them are phone numbers, else <c>department</c>; a phone target
    /// whose samples include <c>48xxxxxxxxx</c> gets <see cref="SuggestionNote.PhonesWithoutPlus"/> unless the target was
    /// changed by the content (that note wins).
    /// </remarks>
    public static ColumnSuggestion? Suggest(
        string header,
        ObjectKind kind,
        IReadOnlyCollection<string>? attributeNames = null,
        IReadOnlyList<string>? samples = null)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (IsPasswordLike(header) || IsPersonalIdentifier(header))
        {
            return null;
        }

        var folded = Fold(header);
        if (folded.Length == 0)
        {
            return null;
        }

        var values = NonEmpty(samples);
        var suggestion = FromTable(folded, kind, attributeNames, values);
        if (suggestion is null && attributeNames is not null)
        {
            var name = attributeNames.FirstOrDefault(n => n.Length > 0 && string.Equals(Fold(n), folded, StringComparison.Ordinal)
                && !CreationValues.IsNamedBlockedAttribute(n));
            suggestion = name is null ? null : new ColumnSuggestion(ColumnTargetKind.Attribute, name);
        }

        if (suggestion is { Kind: ColumnTargetKind.Attribute, Attribute: { } attribute, Note: SuggestionNote.None }
            && CreationValues.IsPhoneAttribute(attribute) && values.Any(CellText.IsPolishNumberWithoutPlus))
        {
            suggestion = suggestion with { Note = SuggestionNote.PhonesWithoutPlus };
        }

        return suggestion;
    }

    /// <summary>
    /// A suggestion for a column without a header from its samples, with <see cref="SuggestionNote.ContentBased"/>: 80 %
    /// e-mail addresses → <c>mail</c>, Polish mobile numbers → <c>mobile</c>, other phones → <c>telephoneNumber</c>, else null.
    /// </summary>
    public static ColumnSuggestion? SuggestFromContent(IReadOnlyList<string> samples, ObjectKind kind)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var values = NonEmpty(samples);
        if (values.Count == 0)
        {
            return null;
        }

        string? attribute = null;
        if (MostAre(values, CellText.LooksLikeEmail))
        {
            attribute = "mail";
        }
        else if (MostAre(values, CellText.LooksLikePolishMobile))
        {
            attribute = "mobile";
        }
        else if (MostAre(values, CellText.LooksLikePhone))
        {
            attribute = "telephoneNumber";
        }

        if (attribute is null)
        {
            return null;
        }

        var suggestion = new ColumnSuggestion(ColumnTargetKind.Attribute, attribute, Note: SuggestionNote.ContentBased);
        return AppliesTo(suggestion, kind) ? suggestion : null;
    }

    /// <summary>
    /// A password-like header (substrings <c>haslo</c>, <c>hasla</c>, <c>password</c>, <c>passwd</c>, <c>pwd</c>,
    /// <c>kennwort</c>; exact <c>pin</c>, <c>kodpin</c>, <c>pass</c>): the column is never mapped, masked and unreachable from
    /// patterns.
    /// </summary>
    public static bool IsPasswordLike(string header)
    {
        ArgumentNullException.ThrowIfNull(header);
        var folded = Fold(header);
        return folded.Length > 0
            && (PasswordExact.Contains(folded) || PasswordSubstrings.Any(s => folded.Contains(s, StringComparison.Ordinal)));
    }

    /// <summary>
    /// A personal identifier header (PESEL, NIP, ID card, birth date, SSN, IBAN – exact folded names such as <c>pesel</c>,
    /// <c>nip</c>, <c>nrdowodu</c>, <c>iban</c>, plus headers containing <c>pesel</c>, <c>dataurodzenia</c>,
    /// <c>birthdate</c>): ignored unless mapped explicitly.
    /// </summary>
    public static bool IsPersonalIdentifier(string header)
    {
        ArgumentNullException.ThrowIfNull(header);
        var folded = Fold(header);
        return folded.Length > 0
            && (IdentifierExact.Contains(folded) || IdentifierSubstrings.Any(s => folded.Contains(s, StringComparison.Ordinal)));
    }

    /// <summary>
    /// A header the reader recognises without a schema (header detection, 2.1): a synonym of the table for any kind, the
    /// LDAP name of one of its targets, or a password-like or personal-identifier header.
    /// </summary>
    internal static bool IsKnownHeader(string header)
    {
        var folded = Fold(header);
        return folded.Length > 0 && (KnownHeaders.Contains(folded) || IsPasswordLike(header) || IsPersonalIdentifier(header));
    }

    /// <summary>Whether the object kind can have the attribute as a suggestion target (the remarks of the class; <see cref="CreationValues.IsBlockedAttribute"/> without a schema).</summary>
    internal static bool AttributeAppliesTo(string attribute, ObjectKind kind) =>
        AppliesTo(new ColumnSuggestion(ColumnTargetKind.Attribute, attribute), kind);

    private static ColumnSuggestion? FromTable(string folded, ObjectKind kind, IReadOnlyCollection<string>? attributeNames, IReadOnlyList<string> samples)
    {
        if (!ByFolded.TryGetValue(folded, out var entries))
        {
            return null;
        }

        var effective = kind == ObjectKind.InetOrgPerson ? ObjectKind.User : kind;
        var entry = entries.FirstOrDefault(e => e.OnlyFor is null || e.OnlyFor == effective);
        if (entry is null)
        {
            return null;
        }

        var target = entry.Target;
        if (TelephoneFamily.Contains(folded) && samples.Count > 0 && MostAre(samples, CellText.LooksLikePolishMobile))
        {
            target = new ColumnSuggestion(ColumnTargetKind.Attribute, "mobile", Note: SuggestionNote.MobileNumbersDetected);
        }
        else if (folded == Komorka && samples.Count > 0 && MostAre(samples, CellText.LooksLikePhone))
        {
            target = new ColumnSuggestion(ColumnTargetKind.Attribute, "mobile", Note: SuggestionNote.MobileNumbersDetected);
        }

        if (!AppliesTo(target, kind))
        {
            return null;
        }

        return target is { Kind: ColumnTargetKind.Attribute, Attribute: { } attribute }
            && attributeNames is not null && !attributeNames.Contains(attribute, StringComparer.OrdinalIgnoreCase)
            ? null
            : target;
    }

    /// <summary>Whether the object kind can have the target (see the remarks of the class).</summary>
    private static bool AppliesTo(ColumnSuggestion target, ObjectKind kind)
    {
        if (target.Attribute is { } named && CreationValues.IsNamedBlockedAttribute(named))
        {
            return false;
        }

        switch (target.Kind)
        {
            case ColumnTargetKind.Manager or ColumnTargetKind.Groups or ColumnTargetKind.PatternOnly or ColumnTargetKind.Ignore:
                return true;
            case ColumnTargetKind.FullName or ColumnTargetKind.Country:
                return kind is ObjectKind.User or ObjectKind.InetOrgPerson or ObjectKind.Contact;
            case ColumnTargetKind.ExpiresAt:
                return kind is ObjectKind.User or ObjectKind.InetOrgPerson;
        }

        var attribute = target.Attribute ?? string.Empty;
        return kind switch
        {
            ObjectKind.User or ObjectKind.InetOrgPerson => !string.Equals(attribute, "location", StringComparison.OrdinalIgnoreCase),
            ObjectKind.Contact => !NotForContacts.Contains(attribute),
            ObjectKind.Group => GroupAttributes.Contains(attribute),
            ObjectKind.Computer => ComputerAttributes.Contains(attribute),
            _ => false,
        };
    }

    private static List<string> NonEmpty(IReadOnlyList<string>? samples) =>
        samples is null ? [] : [.. samples.Where(s => !string.IsNullOrWhiteSpace(s)).Take(20)];

    /// <summary>At least 80 % of <paramref name="values"/> (non-empty) satisfy <paramref name="test"/>.</summary>
    private static bool MostAre(IReadOnlyList<string> values, Func<string, bool> test) =>
        values.Count > 0 && values.Count(test) * 5 >= values.Count * 4;

    private static List<ColumnSynonym> BuildTable()
    {
        var table = new List<ColumnSynonym>();

        void Attribute(string attribute, params string[] folded) => Add(new ColumnSuggestion(ColumnTargetKind.Attribute, attribute), null, folded);

        void Target(ColumnTargetKind kind, params string[] folded) => Add(new ColumnSuggestion(kind), null, folded);

        void Add(ColumnSuggestion target, ObjectKind? onlyFor, params string[] folded)
        {
            foreach (var header in folded)
            {
                if (!table.Any(e => e.Folded == header && e.OnlyFor == onlyFor))
                {
                    table.Add(new ColumnSynonym(header, target, onlyFor));
                }
            }
        }

        var givenFirst = new ColumnSuggestion(ColumnTargetKind.FullName, Order: NameOrder.GivenNameFirst);
        var cn = new ColumnSuggestion(ColumnTargetKind.Attribute, "cn");
        var office = new ColumnSuggestion(ColumnTargetKind.Attribute, "physicalDeliveryOfficeName");

        Attribute("givenName", "imie", "imiona", "pierwszeimie", "firstname", "givenname", "forename");
        Attribute("sn", "nazwisko", "lastname", "surname", "familyname", "sn");
        Add(givenFirst, null, "imieinazwisko", "imienazwisko", "pracownik", "osoba", "fullname", "employeename");
        Add(givenFirst, ObjectKind.User, "nazwa", "name");
        Add(givenFirst, ObjectKind.Contact, "nazwa", "name");
        Add(new ColumnSuggestion(ColumnTargetKind.FullName, Order: NameOrder.SurnameFirst), null, "nazwiskoiimie", "nazwiskoimie");
        Attribute("initials", "inicjaly", "inicjal", "initials", "middleinitial");
        Attribute("middleName", "drugieimie", "middlename");
        Attribute("displayName", "nazwawyswietlana", "displayname");
        Add(cn, null, "nazwaobiektu", "nazwagrupy", "nazwakomputera", "komputer", "nazwahosta", "cn", "commonname", "groupname",
            "computername", "hostname");
        Add(cn, ObjectKind.Group, "nazwa", "name");
        Add(cn, ObjectKind.Computer, "nazwa", "name");
        Attribute("sAMAccountName", "login", "nazwauzytkownika", "nazwalogowania", "konto", "loginwindows", "samaccountname", "username",
            "userid", "logon", "logonname", "account");
        Attribute("userPrincipalName", "upn", "loginupn", "userprincipalname");
        Attribute("mail", "email", "mail", "adresemail", "adresmailowy", "poczta", "pocztaemail", "adrespoczty", "emailsluzbowy",
            "mailsluzbowy", "adresemailsluzbowy", "emailaddress", "workemail");
        Target(ColumnTargetKind.PatternOnly, "emailprywatny", "mailprywatny", "prywatnyemail", "adresprywatny", "emaildomowy",
            "privateemail", "personalemail", "homeemail");
        Attribute("mobile", "telefonkomorkowy", "komorkowy", "telkom", "telefonkom", "telkomorkowy", "nrkomorki", "numerkomorki", "nrtelkom",
            "nrtelefonukomorkowego", "numertelefonukomorkowego", "komorkasluzbowa", "gsm", "mobile", "mobilephone", "cellphone", "cell",
            "mobilenumber");
        Attribute("telephoneNumber", "telefon", "tel", "nrtel", "nrtelefonu", "numertelefonu", "telefonstacjonarny", "telefonsluzbowy",
            "telsluzbowy", "telefonbiurowy", "nrwewnetrzny", "wewnetrzny", "phone", "telephone", "telephonenumber", "officephone",
            "businessphone", "workphone", "extension");
        Attribute("homePhone", "telefondomowy", "homephone");
        Attribute("ipPhone", "telefonip", "ipphone");
        Attribute("facsimileTelephoneNumber", "faks", "fax", "nrfaksu", "faxnumber");
        Attribute("title", "stanowisko", "funkcja", "title", "jobtitle", "position");
        Attribute("department", "dzial", "departament", "wydzial", "komorkaorganizacyjna", Komorka, "department", "dept");
        Attribute("company", "firma", "spolka", "pracodawca", "company", "employer", "organization", "organisation");
        Add(office, null, "biuro", "pokoj", "siedziba", "miejscepracy", "office", "room");
        Add(office, ObjectKind.User, "lokalizacja", "location");
        Add(office, ObjectKind.Contact, "lokalizacja", "location");
        Add(new ColumnSuggestion(ColumnTargetKind.Attribute, "location"), ObjectKind.Computer, "lokalizacja", "location");
        Attribute("employeeID", "idpracownika", "identyfikatorpracownika", "employeeid");
        Attribute("employeeNumber", "numerpracownika", "nrpracownika", "nrewidencyjny", "numerewidencyjny", "nrkadrowy", "employeenumber",
            "personnelnumber", "staffnumber");
        Attribute("employeeType", "typpracownika", "rodzajzatrudnienia", "employeetype");
        Target(ColumnTargetKind.Manager, "menedzer", "przelozony", "bezposredniprzelozony", "kierownik", "szef", "manager", "supervisor",
            "reportsto");
        Add(new ColumnSuggestion(ColumnTargetKind.ExpiresAt), ObjectKind.User, "umowado", "datakoncaumowy", "datazakonczenia", "kontowygasa",
            "wygasa", "datawygasniecia", "enddate", "contractend", "expires", "expirationdate");
        Target(ColumnTargetKind.Country, "kraj", "panstwo", "krajregion", "country", "countryregion");
        Attribute("description", "opis", "description");
        Attribute("info", "uwagi", "notatki", "notatka", "notes", "comment", "comments", "info");
        Attribute("streetAddress", "ulica", "adres", "street", "streetaddress", "address");
        Attribute("l", "miasto", "miejscowosc", "city", "town", "locality");
        Attribute("postalCode", "kodpocztowy", "kod", "postalcode", "zip", "zipcode", "postcode");
        Attribute("st", "wojewodztwo", "region", "state", "province");
        Attribute("wWWHomePage", "stronawww", "www", "stronainternetowa", "homepage", "website", "webpage", "web");
        Attribute("homeDirectory", "folderdomowy", "katalogdomowy", "homedirectory", "homefolder");
        Attribute("homeDrive", "dyskdomowy", "homedrive");
        Attribute("scriptPath", "skryptlogowania", "scriptpath", "logonscript");
        Attribute("profilePath", "sciezkaprofilu", "profilepath");
        Target(ColumnTargetKind.Groups, "grupy", "grupa", "czlonkostwo", "grupyad", "groups", "group", "memberof");
        return table;
    }
}
