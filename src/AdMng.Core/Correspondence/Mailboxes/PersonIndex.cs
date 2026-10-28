using System.Globalization;
using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Salutations;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Core.Templates;

namespace AdMng.Core.Correspondence.Mailboxes;

/// <summary>A person account that may own a mailbox (read once into memory, decision D9).</summary>
/// <param name="DistinguishedName">The DN of the account.</param>
/// <param name="GivenName">givenName (empty when missing).</param>
/// <param name="Surname">sn (empty when missing).</param>
/// <param name="DisplayName">displayName, else the RDN value.</param>
/// <param name="Address">mail, else the primary SMTP proxy address; null without a valid one.</param>
/// <param name="IsDisabled">The account is disabled.</param>
/// <param name="Type">The person's own mailbox type (a functional mailbox account is no person).</param>
/// <param name="Department">department.</param>
/// <param name="Title">title (the job title).</param>
/// <param name="Login">sAMAccountName.</param>
public sealed record PersonCandidate(string DistinguishedName, string GivenName, string Surname, string DisplayName,
    string? Address, bool IsDisabled, MailboxType Type, string? Department, string? Title, string? Login)
{
    private static readonly string[] BaseAttributes =
    [
        Attr.GivenName, Attr.Sn, Attr.DisplayName, Attr.Mail, Attr.ProxyAddresses, Attr.UserAccountControl, Attr.Department,
        Attr.Title, Attr.SamAccountName,
    ];

    /// <summary>
    /// The attributes <see cref="From"/> reads: names, addresses, account state, department, job title and login, plus
    /// <c>msExchRecipientTypeDetails</c> when <paramref name="schema"/> has it (or no schema is given).
    /// </summary>
    public static IReadOnlyList<string> AttributesFor(ISchemaCatalog? schema) =>
        [.. BaseAttributes, .. Optional(schema, MailboxTypes.RecipientTypeDetails)];

    /// <summary>A candidate from an account read with <see cref="AttributesFor"/>.</summary>
    public static PersonCandidate From(DirectoryObject person)
    {
        ArgumentNullException.ThrowIfNull(person);
        return new PersonCandidate(
            person.DistinguishedName,
            person.GetString(Attr.GivenName)?.Trim() ?? string.Empty,
            person.GetString(Attr.Sn)?.Trim() ?? string.Empty,
            NonEmpty(person.GetString(Attr.DisplayName)) ?? person.Dn.Name,
            AddressOf(person),
            person.IsDisabled,
            MailboxTypes.Of(person),
            NonEmpty(person.GetString(Attr.Department)),
            NonEmpty(person.GetString(Attr.Title)),
            NonEmpty(person.GetString(Attr.SamAccountName)));
    }

    public override string ToString() => nameof(PersonCandidate);

    /// <summary><c>mail</c> when it is a valid address, else the valid primary SMTP proxy address, else null.</summary>
    internal static string? AddressOf(DirectoryObject account)
    {
        if (UserMessageRules.TryParseAddress(account.GetString(Attr.Mail), out var mail))
        {
            return mail;
        }

        return UserMessageRules.PrimarySmtpAddress(account.GetStrings(Attr.ProxyAddresses)) is { } proxy
            && UserMessageRules.TryParseAddress(proxy, out var primary)
            ? primary
            : null;
    }

    /// <summary>The attributes of <paramref name="names"/> the schema has (all of them without a schema).</summary>
    internal static IEnumerable<string> Optional(ISchemaCatalog? schema, params string[] names) =>
        names.Where(n => schema is null || schema.FindAttribute(n) is not null);

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// The matching key of person names: diacritics removed (<c>Ł</c> → <c>l</c>, <c>ż</c> → <c>z</c>), lower case, runs of
/// white space, hyphens, dashes and commas collapsed into one space ("Kowalska-Nowak" = "kowalska nowak"), typographic
/// apostrophes as <c>'</c>, invisible format and control characters (soft hyphen, zero-width space) dropped.
/// </summary>
public static class PersonNameKey
{
    /// <summary>"  Łukasz  Wiśniewski-Żółć " → "lukasz wisniewski zolc"; null or blank → "".</summary>
    public static string Fold(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var text = Diacritics.Remove(name.Normalize(NormalizationForm.FormC)).ToLowerInvariant();
        var builder = new StringBuilder(text.Length);
        var separator = false;
        foreach (var c in text)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (char.IsWhiteSpace(c) || c is ',' or '\u2212' || category == UnicodeCategory.DashPunctuation)
            {
                separator = builder.Length > 0;
                continue;
            }

            if (category is UnicodeCategory.Format or UnicodeCategory.Control)
            {
                continue;
            }

            if (separator)
            {
                builder.Append(' ');
                separator = false;
            }

            builder.Append(c is '\u2018' or '\u2019' or '\u02BC' or '`' or '\u00B4' ? '\'' : c);
        }

        return builder.ToString();
    }

    /// <summary>The words of a folded name ("kowalska nowak" → kowalska, nowak).</summary>
    internal static string[] Parts(string folded) =>
        folded.Length == 0 ? [] : folded.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// The persons of the chosen base, indexed for local matching with diacritic folding (decision D9): the folded given name
/// with the surname (both orders), the folded display name without titles and remarks, and the folded surname and the
/// parts of a compound surname – alone and with the first given name. Built once per query (linear); <see cref="Lookup"/>
/// is a few dictionary reads plus the level check of the persons found. Persons are kept in input order and de-duplicated
/// by DN. Lookups are thread-safe once the index is built.
/// </summary>
public sealed class PersonIndex
{
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<string, List<Entry>> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Entry>> _byDisplayName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Entry>> _bySurname = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Entry>> _byFirstGivenAndSurname = new(StringComparer.Ordinal);

    /// <summary>Indexes the folded given name, surname, both orders, display name and surname parts.</summary>
    public PersonIndex(IEnumerable<PersonCandidate> persons)
    {
        ArgumentNullException.ThrowIfNull(persons);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var person in persons)
        {
            if (person is null || string.IsNullOrWhiteSpace(person.DistinguishedName) || !seen.Add(person.DistinguishedName.Trim()))
            {
                continue;
            }

            var entry = new Entry(person, _entries.Count);
            _entries.Add(entry);
            if (entry.Given.Length > 0 || entry.Surname.Length > 0)
            {
                Add(_byName, NameKey(entry.Given, entry.Surname), entry);
            }

            Add(_byDisplayName, entry.DisplayName, entry);

            // Partial: the surname and the parts of a compound surname, alone (owners without a given name) and with the
            // first given name (every other owner – a common surname alone would make every lookup scan thousands).
            foreach (var surname in entry.SurnameKeys)
            {
                Add(_bySurname, surname, entry);
                if (entry.FirstGiven.Length > 0)
                {
                    Add(_byFirstGivenAndSurname, NameKey(entry.FirstGiven, surname), entry);
                }
            }
        }
    }

    /// <summary>The number of persons indexed.</summary>
    public int Count => _entries.Count;

    /// <summary>The union of all levels, distinct, in the order the persons were given; empty for an empty name.</summary>
    public IReadOnlyList<PersonCandidate> Lookup(OwnerName name) => [.. Find(name).Select(f => f.Person)];

    public override string ToString() => nameof(PersonIndex);

    /// <summary>The persons whose name matches <paramref name="name"/> at some level, with the best level of each.</summary>
    internal IReadOnlyList<(PersonCandidate Person, OwnerMatchLevel Level)> Find(OwnerName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.IsEmpty)
        {
            return [];
        }

        var owner = new OwnerKeys(name);
        var found = new HashSet<Entry>();
        Collect(_byName, NameKey(owner.Given, owner.Surname));
        Collect(_byName, NameKey(owner.Surname, owner.Given));
        Collect(_byDisplayName, owner.GivenThenSurname);
        Collect(_byDisplayName, owner.SurnameThenGiven);
        foreach (var surname in owner.SurnameKeys)
        {
            if (owner.FirstGiven.Length == 0)
            {
                Collect(_bySurname, surname);
            }
            else
            {
                Collect(_byFirstGivenAndSurname, NameKey(owner.FirstGiven, surname));
            }
        }

        return
        [
            .. found
                .Select(e => (Entry: e, Level: OwnerNameLevels.Of(owner, e)))
                .Where(m => m.Level is not null)
                .OrderBy(m => m.Entry.Order)
                .Select(m => (m.Entry.Person, m.Level!.Value)),
        ];

        void Collect(Dictionary<string, List<Entry>> index, string key)
        {
            if (key.Length > 0 && index.TryGetValue(key, out var entries))
            {
                found.UnionWith(entries);
            }
        }
    }

    private static string NameKey(string given, string surname) => given + "\u0001" + surname;

    private static void Add(Dictionary<string, List<Entry>> index, string key, Entry entry)
    {
        if (key.Length == 0)
        {
            return;
        }

        if (!index.TryGetValue(key, out var entries))
        {
            index[key] = entries = [];
        }

        if (entries.Count == 0 || !ReferenceEquals(entries[^1], entry))
        {
            entries.Add(entry);
        }
    }

    /// <summary>A person with the keys of its name, computed once.</summary>
    internal sealed class Entry
    {
        public Entry(PersonCandidate person, int order)
        {
            Person = person;
            Order = order;
            ExactGiven = OwnerNameLevels.Exact(person.GivenName);
            ExactSurname = OwnerNameLevels.Exact(person.Surname);
            Given = PersonNameKey.Fold(person.GivenName);
            Surname = PersonNameKey.Fold(person.Surname);
            SurnameParts = PersonNameKey.Parts(Surname);
            SurnameKeys = OwnerNameLevels.SurnameKeys(Surname, SurnameParts);
            FirstGiven = PersonNameKey.Parts(Given).FirstOrDefault() ?? string.Empty;
            DisplayName = OwnerNameLevels.DisplayNameKey(person.DisplayName, OwnerNameLevels.Join(Given, Surname), OwnerNameLevels.Join(Surname, Given));
        }

        public PersonCandidate Person { get; }

        public int Order { get; }

        public string ExactGiven { get; }

        public string ExactSurname { get; }

        public string Given { get; }

        public string Surname { get; }

        public string[] SurnameParts { get; }

        /// <summary>The surname and, for a compound surname, its parts.</summary>
        public string[] SurnameKeys { get; }

        public string FirstGiven { get; }

        /// <summary>The folded display name without titles and "(…)" remarks.</summary>
        public string DisplayName { get; }
    }
}

/// <summary>The keys of an owner's name (see <see cref="PersonIndex.Entry"/>).</summary>
internal sealed class OwnerKeys
{
    public OwnerKeys(OwnerName name)
    {
        ExactGiven = OwnerNameLevels.Exact(name.GivenName);
        ExactSurname = OwnerNameLevels.Exact(name.Surname);
        Given = PersonNameKey.Fold(name.GivenName);
        Surname = PersonNameKey.Fold(name.Surname);
        SurnameParts = PersonNameKey.Parts(Surname);
        SurnameKeys = OwnerNameLevels.SurnameKeys(Surname, SurnameParts);
        FirstGiven = PersonNameKey.Parts(Given).FirstOrDefault() ?? string.Empty;
        GivenThenSurname = OwnerNameLevels.Join(Given, Surname);
        SurnameThenGiven = OwnerNameLevels.Join(Surname, Given);
    }

    public string ExactGiven { get; }

    public string ExactSurname { get; }

    public string Given { get; }

    public string Surname { get; }

    public string[] SurnameParts { get; }

    public string[] SurnameKeys { get; }

    public string FirstGiven { get; }

    public string GivenThenSurname { get; }

    public string SurnameThenGiven { get; }
}

/// <summary>The levels of design note 3.7, step 3.</summary>
internal static class OwnerNameLevels
{
    /// <summary>The best level of <paramref name="person"/> for the owner, or null when the names do not match.</summary>
    public static OwnerMatchLevel? Of(OwnerKeys owner, PersonIndex.Entry person)
    {
        // Exact: given and surname equal ignoring case (white space collapsed, Unicode composed).
        if (string.Equals(owner.ExactGiven, person.ExactGiven, StringComparison.OrdinalIgnoreCase)
            && string.Equals(owner.ExactSurname, person.ExactSurname, StringComparison.OrdinalIgnoreCase))
        {
            return OwnerMatchLevel.Exact;
        }

        if (owner.Given == person.Given && owner.Surname == person.Surname)
        {
            return OwnerMatchLevel.Folded;
        }

        // Swapping needs both parts: "Wójcik Małgorzata" in givenName/sn for Małgorzata Wójcik.
        if (owner.Given.Length > 0 && owner.Surname.Length > 0 && owner.Given == person.Surname && owner.Surname == person.Given)
        {
            return OwnerMatchLevel.Swapped;
        }

        if (person.DisplayName.Length > 0 && (person.DisplayName == owner.GivenThenSurname || person.DisplayName == owner.SurnameThenGiven))
        {
            return OwnerMatchLevel.DisplayName;
        }

        return IsPartial(owner, person) ? OwnerMatchLevel.Partial : null;
    }

    /// <summary>
    /// Partial: the first given names are equal (an owner without a given name – a single word – accepts any) and the
    /// surnames are equal, or one surname is a part of the other's compound surname ("Nowak" / "Kowalska-Nowak", either
    /// side). A surname alone with another given name is no candidate ("Piotr Wiśniewski" is not "Łukasz Wiśniewski").
    /// </summary>
    private static bool IsPartial(OwnerKeys owner, PersonIndex.Entry person)
    {
        if (owner.Surname.Length == 0 || person.Surname.Length == 0)
        {
            return false;
        }

        var givenCompatible = owner.FirstGiven.Length == 0 || owner.FirstGiven == person.FirstGiven;
        if (!givenCompatible)
        {
            return false;
        }

        return owner.Surname == person.Surname
            || (person.SurnameParts.Length > 1 && person.SurnameParts.Contains(owner.Surname, StringComparer.Ordinal))
            || (owner.SurnameParts.Length > 1 && owner.SurnameParts.Contains(person.Surname, StringComparer.Ordinal));
    }

    /// <summary>The form of the Exact comparison: trimmed, white space collapsed, Unicode composed.</summary>
    public static string Exact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (value.IsNormalized(NormalizationForm.FormC) && !char.IsWhiteSpace(value[0]) && !char.IsWhiteSpace(value[^1]) && !HasRunOfWhiteSpace(value))
        {
            return value;
        }

        return string.Join(' ', value.Normalize(NormalizationForm.FormC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Two folded keys joined by a space (an empty one left out).</summary>
    public static string Join(string first, string second) =>
        first.Length == 0 ? second : second.Length == 0 ? first : $"{first} {second}";

    /// <summary>The folded surname and, for a compound surname, its parts (distinct).</summary>
    public static string[] SurnameKeys(string surname, string[] parts) =>
        surname.Length == 0 ? [] : parts.Length > 1 ? [surname, .. parts.Distinct(StringComparer.Ordinal)] : [surname];

    private static bool HasRunOfWhiteSpace(string value)
    {
        for (var i = 1; i < value.Length; i++)
        {
            if (char.IsWhiteSpace(value[i]) && (char.IsWhiteSpace(value[i - 1]) || value[i] != ' '))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The folded display name without "(…)" remarks and titles ("dr hab. Ewa Zielińska, prof. UW" → "ewa zielinska"). A
    /// display name that already is one of <paramref name="nameKeys"/> (the folded name in either order – most of them) is
    /// not parsed for titles: it adds nothing to the name levels.
    /// </summary>
    public static string DisplayNameKey(string? displayName, params string[] nameKeys)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return string.Empty;
        }

        var folded = PersonNameKey.Fold(displayName);
        if (folded.Length == 0 || nameKeys.Contains(folded, StringComparer.Ordinal))
        {
            return folded;
        }

        var text = displayName.Contains('(', StringComparison.Ordinal) || displayName.Contains(')', StringComparison.Ordinal)
            ? OwnerNameParser.WithoutRemarks(displayName)
            : displayName;
        folded = PersonNameKey.Fold(text);
        if (folded.Length == 0 || nameKeys.Contains(folded, StringComparer.Ordinal))
        {
            return folded;
        }

        return PersonNameKey.Fold(TitleParser.WithoutTitles(text, TitleTable.Default) ?? text);
    }
}
