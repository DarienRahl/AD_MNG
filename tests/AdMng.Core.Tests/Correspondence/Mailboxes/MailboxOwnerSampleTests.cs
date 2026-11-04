using AdMng.Core.ActiveDirectory;
using AdMng.Core.Correspondence.Mailboxes;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Testing;

namespace AdMng.Core.Tests.Correspondence.Mailboxes;

/// <summary>
/// The table of design note section 10 end to end in pure form: the sample directory, mailboxes and persons read with
/// plain searches, the person index, the matcher.
/// </summary>
public class MailboxOwnerSampleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record SampleDirectory(FakeDirectorySession Session, SampleCompany Company, SalutationSample Salutations, MailboxOwnerSample Sample)
    {
        public string Dn(string key) => key switch
        {
            "SekretariatIt" => Sample.SekretariatIt,
            "Rekrutacja" => Sample.Rekrutacja,
            "Faktury" => Sample.Faktury,
            "BiuroProjektu" => Sample.BiuroProjektu,
            "Archiwum" => Sample.Archiwum,
            "Konferencja" => Sample.Konferencja,
            "PomocTechniczna" => Sample.PomocTechniczna,
            "Wydarzenia" => Sample.Wydarzenia,
            "Sala101" => Sample.Sala101,
            "StaraSkrzynka" => Sample.StaraSkrzynka,
            "ProjektUe" => Sample.ProjektUe,
            "DzialKadr" => Sample.DzialKadr,
            "Laboratorium" => Sample.Laboratorium,
            "Wnioski" => Sample.Wnioski,
            "JanKowalski" => Company.JanKowalski,
            "LukaszWisniewski" => Company.LukaszWisniewski,
            "MalgorzataWojcik" => Company.MalgorzataWojcik,
            "PawelLewandowski" => Company.PawelLewandowski,
            "ZbigniewKazmierczak" => Company.ZbigniewKazmierczak,
            "AnnaNowak" => Salutations.Doctor,
            "PiotrWisniewski" => Salutations.Engineer,
            "SaszaKim" => Salutations.SaszaKim,
            "TomaszWojcik" => Salutations.Priest,
            "EwaZielinska" => Salutations.Associate,
            "AgnieszkaZolc" => Sample.AgnieszkaZolc,
            "JanNowakIt" => Sample.JanNowakIt,
            "JanNowakSales" => Sample.JanNowakSales,
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
        };
    }

    private static SampleDirectory Build()
    {
        var builder = new FakeDirectoryBuilder();
        var company = builder.AddSampleCompany();
        var salutations = builder.AddSalutationSamples(company);
        var sample = builder.AddMailboxOwnerSample(company);
        return new SampleDirectory(builder.Session, company, salutations, sample);
    }

    private static async Task<List<DirectoryObject>> SearchAsync(IDirectorySession session, string baseDn, string filter, IReadOnlyList<string> attributes)
    {
        var result = new List<DirectoryObject>();
        await foreach (var item in session.SearchAsync(new DirectorySearch { BaseDn = baseDn, Filter = filter, Attributes = attributes }, Ct))
        {
            result.Add(item);
        }

        return result;
    }

    /// <summary>Reads the mailboxes of the selection under the sample OU and the persons of the domain, then matches every mailbox.</summary>
    private static async Task<Dictionary<string, (MailboxEntry Mailbox, OwnerName? Name, OwnerMatch Match)>> RunAsync(
        SampleDirectory directory,
        MailboxSelection selection = MailboxSelection.SharedRoomsAndEquipment,
        OwnerNameSource source = OwnerNameSource.GivenNameAndSurname,
        OwnerMatchOptions? options = null)
    {
        var session = directory.Session;
        var mailboxObjects = await SearchAsync(session, directory.Sample.Ou,
            LdapFilter.And(ObjectKind.User.ToLdapFilter(), MailboxTypes.Filter(selection)),
            [.. MailboxEntry.AttributesFor(session.Schema), .. OwnerNameParser.AttributesFor(source, null)]);
        var personObjects = await SearchAsync(session, session.Info.DefaultNamingContext,
            LdapFilter.And(ObjectKind.User.ToLdapFilter(), LdapFilter.Or(LdapFilter.Present(Attr.GivenName), LdapFilter.Present(Attr.Sn), LdapFilter.Present(Attr.DisplayName))),
            PersonCandidate.AttributesFor(session.Schema));
        var persons = new PersonIndex(personObjects.Select(PersonCandidate.From));

        var results = new Dictionary<string, (MailboxEntry, OwnerName?, OwnerMatch)>(StringComparer.OrdinalIgnoreCase);
        foreach (var mailboxObject in mailboxObjects)
        {
            var mailbox = MailboxEntry.From(mailboxObject);
            var name = OwnerNameParser.Parse(mailboxObject, source, null);
            results[mailbox.DistinguishedName] = (mailbox, name, MailboxOwnerMatcher.Match(mailbox, name, persons, options ?? new OwnerMatchOptions()));
        }

        return results;
    }

    [Theory]
    [InlineData("SekretariatIt", OwnerMatchStatus.Matched, "JanKowalski", OwnerMatchLevel.Exact)]
    [InlineData("Rekrutacja", OwnerMatchStatus.Matched, "LukaszWisniewski", OwnerMatchLevel.Folded)]
    [InlineData("Faktury", OwnerMatchStatus.Probable, "MalgorzataWojcik", OwnerMatchLevel.Swapped)]
    [InlineData("BiuroProjektu", OwnerMatchStatus.Matched, "AnnaNowak", OwnerMatchLevel.Exact)]
    [InlineData("Archiwum", OwnerMatchStatus.Matched, "PiotrWisniewski", OwnerMatchLevel.Exact)]
    [InlineData("Konferencja", OwnerMatchStatus.Matched, "SaszaKim", OwnerMatchLevel.Exact)]
    [InlineData("PomocTechniczna", OwnerMatchStatus.NoName, null, null)]
    [InlineData("Wydarzenia", OwnerMatchStatus.MatchedByDelegate, "TomaszWojcik", OwnerMatchLevel.Exact)]
    [InlineData("Sala101", OwnerMatchStatus.Matched, "PawelLewandowski", OwnerMatchLevel.Exact)]
    [InlineData("StaraSkrzynka", OwnerMatchStatus.OwnerDisabled, "ZbigniewKazmierczak", OwnerMatchLevel.Exact)]
    [InlineData("ProjektUe", OwnerMatchStatus.NotFound, null, null)]
    [InlineData("DzialKadr", OwnerMatchStatus.Matched, "EwaZielinska", OwnerMatchLevel.Exact)]
    [InlineData("Laboratorium", OwnerMatchStatus.Ambiguous, null, null)]
    [InlineData("Wnioski", OwnerMatchStatus.Matched, "AgnieszkaZolc", OwnerMatchLevel.Exact)]
    public async Task EveryRowOfTheTable(string mailboxKey, OwnerMatchStatus status, string? ownerKey, OwnerMatchLevel? level)
    {
        var directory = Build();
        var results = await RunAsync(directory);
        var mailboxDn = directory.Dn(mailboxKey);

        var (mailbox, _, match) = results[mailboxDn];

        Assert.Equal(status, match.Status);
        if (ownerKey is null)
        {
            Assert.Null(match.Best);
        }
        else
        {
            Assert.True(DistinguishedName.AreEqual(directory.Dn(ownerKey), match.Best!.Person.DistinguishedName), match.Best.Person.DistinguishedName);
            Assert.Equal(level, match.Best.Level);
        }

        // The sample's own expectations say the same.
        var expected = directory.Sample.ExpectedFor(mailboxDn);
        Assert.Equal(status, expected.Status);
        Assert.Equal(level, expected.Level);
        Assert.True(DistinguishedName.AreEqual(ownerKey is null ? null : directory.Dn(ownerKey), expected.Owner));
        Assert.True(mailbox.IsDisabled);
        Assert.Equal(mailboxKey == "Sala101" ? MailboxType.Room : MailboxType.Shared, mailbox.Type);
    }

    [Fact]
    public async Task TheSampleExpectations_CoverEveryMailbox_AndAllHold()
    {
        var directory = Build();
        var results = await RunAsync(directory);

        Assert.Equal(14, results.Count);
        Assert.Equal(directory.Sample.Mailboxes.Order(StringComparer.OrdinalIgnoreCase), results.Keys.Order(StringComparer.OrdinalIgnoreCase));
        foreach (var expected in directory.Sample.Expected)
        {
            var match = results[expected.Mailbox].Match;
            Assert.Equal(expected.Status, match.Status);
            Assert.True(DistinguishedName.AreEqual(expected.Owner, match.Best?.Person.DistinguishedName), expected.Mailbox);
            Assert.Equal(expected.Level, match.Best?.Level);
        }
    }

    [Fact]
    public async Task Details_OfTheInterestingRows()
    {
        var directory = Build();
        var results = await RunAsync(directory);

        var wnioski = results[directory.Sample.Wnioski].Match;
        Assert.Equal("agnieszka.zolc@contoso.test", wnioski.Best!.Person.Address);
        Assert.Equal("Sprzedaż", wnioski.Best.Person.Department);

        var laboratorium = results[directory.Sample.Laboratorium].Match;
        Assert.Equal([directory.Sample.JanNowakIt, directory.Sample.JanNowakSales], laboratorium.Candidates.Select(c => c.Person.DistinguishedName));
        Assert.All(laboratorium.Candidates, c => Assert.Equal((OwnerMatchLevel.Exact, 65), (c.Level, c.Score)));
        Assert.Equal(["IT", "Sprzedaż"], laboratorium.Candidates.Select(c => c.Person.Department));

        var wydarzenia = results[directory.Sample.Wydarzenia].Match;
        Assert.True(wydarzenia.Best!.IsDelegate);
        Assert.Equal(105, wydarzenia.Best.Score);
        Assert.Equal([directory.Salutations.Priest], results[directory.Sample.Wydarzenia].Mailbox.DelegateDns);

        var faktury = results[directory.Sample.Faktury];
        Assert.Equal(("Wójcik", "Małgorzata"), (faktury.Name!.GivenName, faktury.Name.Surname));
        Assert.Equal(50, faktury.Match.Best!.Score);
        Assert.Single(faktury.Match.Candidates);

        var biuro = results[directory.Sample.BiuroProjektu].Match;
        Assert.Equal("dr Anna Nowak", biuro.Best!.Person.DisplayName);
        Assert.Single(biuro.Candidates);

        var archiwum = results[directory.Sample.Archiwum].Match;
        Assert.DoesNotContain(archiwum.Candidates, c => c.Person.DistinguishedName == directory.Company.LukaszWisniewski);

        var stara = results[directory.Sample.StaraSkrzynka].Match;
        Assert.True(stara.Best!.Person.IsDisabled);

        Assert.Equal("dr hab. Ewa Zielińska, prof. UW", results[directory.Sample.DzialKadr].Match.Best!.Person.DisplayName);
        Assert.Null(results[directory.Sample.PomocTechniczna].Name);
    }

    [Fact]
    public async Task DisplayNameSource_PomocTechniczna_IsNotFound()
    {
        var directory = Build();
        var results = await RunAsync(directory, source: OwnerNameSource.DisplayName);

        var pomoc = results[directory.Sample.PomocTechniczna];
        Assert.Equal(("Pomoc", "techniczna"), (pomoc.Name!.GivenName, pomoc.Name.Surname));
        Assert.Equal(OwnerMatchStatus.NotFound, pomoc.Match.Status);

        // Even when mailbox accounts may be owners, its own account is never its owner.
        var withMailboxes = await RunAsync(directory, source: OwnerNameSource.DisplayName, options: new OwnerMatchOptions { ExcludeMailboxAccounts = false });
        Assert.Equal(OwnerMatchStatus.NotFound, withMailboxes[directory.Sample.PomocTechniczna].Match.Status);
    }

    [Fact]
    public async Task Selections_RoomsOnlyWithSharedRoomsAndEquipment()
    {
        var directory = Build();

        var shared = await RunAsync(directory, MailboxSelection.Shared);
        var functional = await RunAsync(directory, MailboxSelection.SharedRoomsAndEquipment);
        var all = await RunAsync(directory, MailboxSelection.AllMailboxes);
        var accounts = await RunAsync(directory, MailboxSelection.AllAccounts);

        Assert.Equal(13, shared.Count);
        Assert.DoesNotContain(directory.Sample.Sala101, shared.Keys);
        Assert.Equal(14, functional.Count);
        Assert.Contains(directory.Sample.Sala101, functional.Keys);
        Assert.Equal(14, all.Count);
        Assert.Equal(14, accounts.Count);
    }

    [Fact]
    public async Task PersonsInMailboxAccounts_AreExcludedByDefault()
    {
        var directory = Build();

        var excluded = await RunAsync(directory);
        var included = await RunAsync(directory, options: new OwnerMatchOptions { ExcludeMailboxAccounts = false });

        // No mailbox in the sample carries the name of another mailbox's owner, so including them changes nothing …
        foreach (var dn in directory.Sample.Mailboxes)
        {
            Assert.Equal(excluded[dn].Match.Status, included[dn].Match.Status);
        }

        // … and the mailbox accounts are in the person index all the same (their own givenName/sn), but never candidates.
        Assert.All(excluded.Values, r => Assert.DoesNotContain(r.Match.Candidates, c => MailboxTypes.IsFunctional(c.Person.Type)));
    }
}
