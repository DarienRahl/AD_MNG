using AdMng.Core.ActiveDirectory;
using AdMng.Core.Correspondence.Mailboxes;

namespace AdMng.Testing;

/// <summary>What the mailbox owner query should find for one mailbox of the sample (default options).</summary>
/// <param name="Mailbox">The DN of the mailbox account.</param>
/// <param name="Status">The status with the owner name in givenName/sn, functional accounts excluded and enabled ones preferred.</param>
/// <param name="Owner">The DN of the chosen owner (null when ambiguous, not found or without a name).</param>
/// <param name="Level">The level of the chosen owner, or null.</param>
public sealed record MailboxOwnerExpectation(string Mailbox, OwnerMatchStatus Status, string? Owner, OwnerMatchLevel? Level)
{
    public override string ToString() => $"{nameof(MailboxOwnerExpectation)} {Status}";
}

/// <summary>The objects of <see cref="FakeDirectoryBuilder.AddMailboxOwnerSample"/> (U5, design note section 10).</summary>
public sealed record MailboxOwnerSample(
    string Ou,
    string SekretariatIt,
    string Rekrutacja,
    string Faktury,
    string BiuroProjektu,
    string Archiwum,
    string Konferencja,
    string PomocTechniczna,
    string Wydarzenia,
    string Sala101,
    string StaraSkrzynka,
    string ProjektUe,
    string DzialKadr,
    string Laboratorium,
    string Wnioski,
    string JanNowakIt,
    string JanNowakSales,
    string AgnieszkaZolc,
    IReadOnlyList<MailboxOwnerExpectation> Expected)
{
    /// <summary>Every mailbox account in the order of the design note's table.</summary>
    public IReadOnlyList<string> Mailboxes =>
    [
        SekretariatIt, Rekrutacja, Faktury, BiuroProjektu, Archiwum, Konferencja, PomocTechniczna, Wydarzenia, Sala101,
        StaraSkrzynka, ProjektUe, DzialKadr, Laboratorium, Wnioski,
    ];

    /// <summary>The persons added for the sample (the two "Jan Nowak" and Agnieszka Żółć with a proxy address only).</summary>
    public IReadOnlyList<string> Persons => [JanNowakIt, JanNowakSales, AgnieszkaZolc];

    /// <summary>The expectation of one mailbox.</summary>
    public MailboxOwnerExpectation ExpectedFor(string mailbox) =>
        Expected.Single(e => string.Equals(e.Mailbox, mailbox, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Mailbox accounts whose givenName/sn name the person the mailbox is assigned to (U5, mailbox owner queries).</summary>
public sealed partial class FakeDirectoryBuilder
{
    /// <summary>Value of <c>msExchRecipientTypeDetails</c> of a shared mailbox.</summary>
    public const long SharedMailboxType = 4;

    /// <summary>Value of <c>msExchRecipientTypeDetails</c> of a room mailbox.</summary>
    public const long RoomMailboxType = 16;

    /// <summary>
    /// OU "Skrzynki" under the company with the mailboxes of the design note (section 10): disabled user accounts with
    /// <c>msExchRecipientTypeDetails</c> 4 (Sala 101: 16, a room), mail <c>&lt;name&gt;@domain</c> and the owner's name
    /// in givenName/sn – an exact name, one without diacritics, a swapped one, names of the salutation sample (a display
    /// name with titles), none at all, a delegate link, a disabled owner, an unknown person and two persons of the same
    /// name; plus the persons "Jan Nowak" (IT and Sprzedaż) and Agnieszka Żółć (Sprzedaż, primary SMTP proxy address
    /// only). Adds <see cref="AddSalutationSamples"/> when they are not there yet (call that first to get its record) and
    /// marks the session as having the Exchange schema (<see cref="FakeSchema.MailboxAttributes"/>).
    /// </summary>
    public MailboxOwnerSample AddMailboxOwnerSample(SampleCompany company)
    {
        ArgumentNullException.ThrowIfNull(company);
        var salutationOu = Dn("OU", "Zwroty grzecznościowe", company.CompanyOu);
        if (!Session.Exists(salutationOu))
        {
            AddSalutationSamples(company);
        }

        string Salutation(string name) => Dn("CN", name, salutationOu);
        var annaNowak = Salutation("Anna Nowak");
        var piotr = Salutation("Piotr Wiśniewski");
        var saszaKim = Salutation("Sasza Kim");
        var tomasz = Salutation("Tomasz Wójcik");
        var ewa = Salutation("Ewa Zielińska");

        // The persons of the sample: two of the same name, and one with a proxy address only.
        var janNowakIt = AddUser("Jan", "Nowak", company.ItOu, Person("IT", "Administrator baz danych", "Warszawa", "jan.nowak"));
        var janNowakSales = AddUser("Jan", "Nowak", company.SalesOu, Person("Sprzedaż", "Handlowiec", "Gdańsk", "jan.nowak2",
            (Attr.SamAccountName, "jan.nowak2"), (Attr.UserPrincipalName, $"jan.nowak2@{DomainDnsName}")));
        var agnieszka = AddUser("Agnieszka", "Żółć", company.SalesOu, Person("Sprzedaż", "Asystentka", "Łódź", "agnieszka.zolc",
            (Attr.Mail, null),
            (Attr.ProxyAddresses, new[] { $"smtp:a.zolc@{DomainDnsName}", $"SMTP:agnieszka.zolc@{DomainDnsName}", "X500:/o=Contoso/cn=Recipients/cn=azolc" })));

        var ou = AddOu("Skrzynki", company.CompanyOu);
        string Mailbox(string displayName, string mailbox, string? givenName, string? surname, long type = SharedMailboxType,
            params (string Attribute, object? Value)[] extra)
        {
            var attributes = new Dictionary<string, object?>
            {
                [Attr.ObjectClass] = new[] { "top", "person", "organizationalPerson", ObjectClasses.User },
                [Attr.GivenName] = givenName,
                [Attr.Sn] = surname,
                [Attr.DisplayName] = displayName,
                [Attr.Description] = type == RoomMailboxType ? $"Sala konferencyjna: {displayName}" : $"Skrzynka współdzielona: {displayName}",
                [Attr.SamAccountName] = mailbox.Length > 20 ? mailbox[..20] : mailbox,
                [Attr.UserPrincipalName] = $"{mailbox}@{DomainDnsName}",
                [Attr.Mail] = $"{mailbox}@{DomainDnsName}",
                [Attr.ProxyAddresses] = new[] { $"SMTP:{mailbox}@{DomainDnsName}" },
                [Attr.UserAccountControl] = (int)(UserAccountControl.NormalAccount | UserAccountControl.AccountDisable),
                [Attr.PwdLastSet] = DefaultPasswordLastSet,
                [Attr.AccountExpires] = AdConvert.FileTimeNever,
                [Attr.PrimaryGroupId] = WellKnownRids.DomainUsers,
                [Attr.ObjectSid] = NextSid(),
                [Attr.SamAccountType] = SamAccountType.UserObject,
                [MailboxTypes.RecipientTypeDetails] = type,
            };
            foreach (var (attribute, value) in extra)
            {
                attributes[attribute] = value;
            }

            return Seed(Dn("CN", displayName, ou), attributes, null);
        }

        var sekretariat = Mailbox("Sekretariat IT", "sekretariat.it", "Jan", "Kowalski");
        var rekrutacja = Mailbox("Rekrutacja", "rekrutacja", "Lukasz", "Wisniewski");
        var faktury = Mailbox("Księgowość – faktury", "faktury", "Wójcik", "Małgorzata");
        var biuro = Mailbox("Biuro projektu", "biuro.projektu", "Anna", "Nowak");
        var archiwum = Mailbox("Archiwum", "archiwum", "Piotr", "Wiśniewski");
        var konferencja = Mailbox("Konferencja 2026", "konferencja2026", "Sasza", "Kim");
        var pomoc = Mailbox("Pomoc techniczna", "pomoc", null, null);
        var wydarzenia = Mailbox("Wydarzenia", "wydarzenia", "Tomasz", "Wójcik", SharedMailboxType,
            (MailboxTypes.DelegateListLink, new[] { tomasz }));
        var sala = Mailbox("Sala 101", "sala101", "Paweł", "Lewandowski", RoomMailboxType);
        var stara = Mailbox("Stara skrzynka", "stara", "Zbigniew", "Kaźmierczak");
        var projekt = Mailbox("Projekt UE", "projekt.ue", "Karolina", "Malinowska");
        var kadry = Mailbox("Dział Kadr", "kadry", "Ewa", "Zielińska");
        var laboratorium = Mailbox("Laboratorium", "lab", "Jan", "Nowak");
        var wnioski = Mailbox("Wnioski", "wnioski", "Agnieszka", "Żółć");

        Session.Info = Session.Info with { HasExchangeSchema = true };

        MailboxOwnerExpectation Expect(string mailbox, OwnerMatchStatus status, string? owner = null, OwnerMatchLevel? level = null) =>
            new(mailbox, status, owner, owner is null ? null : level ?? OwnerMatchLevel.Exact);

        return new MailboxOwnerSample(
            ou, sekretariat, rekrutacja, faktury, biuro, archiwum, konferencja, pomoc, wydarzenia, sala, stara, projekt, kadry,
            laboratorium, wnioski, janNowakIt, janNowakSales, agnieszka,
            [
                Expect(sekretariat, OwnerMatchStatus.Matched, company.JanKowalski),
                Expect(rekrutacja, OwnerMatchStatus.Matched, company.LukaszWisniewski, OwnerMatchLevel.Folded),
                Expect(faktury, OwnerMatchStatus.Probable, company.MalgorzataWojcik, OwnerMatchLevel.Swapped),
                Expect(biuro, OwnerMatchStatus.Matched, annaNowak),
                Expect(archiwum, OwnerMatchStatus.Matched, piotr),
                Expect(konferencja, OwnerMatchStatus.Matched, saszaKim),
                Expect(pomoc, OwnerMatchStatus.NoName),
                Expect(wydarzenia, OwnerMatchStatus.MatchedByDelegate, tomasz),
                Expect(sala, OwnerMatchStatus.Matched, company.PawelLewandowski),
                Expect(stara, OwnerMatchStatus.OwnerDisabled, company.ZbigniewKazmierczak),
                Expect(projekt, OwnerMatchStatus.NotFound),
                Expect(kadry, OwnerMatchStatus.Matched, ewa),
                Expect(laboratorium, OwnerMatchStatus.Ambiguous),
                Expect(wnioski, OwnerMatchStatus.Matched, agnieszka),
            ]);
    }
}
