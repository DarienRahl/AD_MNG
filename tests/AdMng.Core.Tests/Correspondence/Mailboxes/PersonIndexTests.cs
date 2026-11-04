using AdMng.Core.Correspondence.Mailboxes;
using static AdMng.Core.Tests.Correspondence.Mailboxes.MailboxTestData;

namespace AdMng.Core.Tests.Correspondence.Mailboxes;

/// <summary>The in-memory person index: every level found by dictionary keys, in input order, nothing else.</summary>
public class PersonIndexTests
{
    private static readonly PersonCandidate JanKowalski = Person("Jan", "Kowalski");
    private static readonly PersonCandidate LukaszWisniewski = Person("Łukasz", "Wiśniewski");
    private static readonly PersonCandidate PiotrWisniewski = Person("Piotr", "Wiśniewski");
    private static readonly PersonCandidate KowalskiJanSwapped = Person("Kowalski", "Jan", cn: "Kowalski Jan");
    private static readonly PersonCandidate DisplayOnly = Person("", "", displayName: "dr Jan Kowalski", cn: "JK");
    private static readonly PersonCandidate Compound = Person("Jan", "Kowalski-Nowak");
    private static readonly PersonCandidate SecondGiven = Person("Jan Maria", "Kowalski");
    private static readonly PersonCandidate OtherKowalski = Person("Anna", "Kowalska");
    private static readonly PersonCandidate AnnaKowalski = Person("Anna", "Kowalski");

    private static PersonIndex Index() => new(
    [
        JanKowalski, LukaszWisniewski, PiotrWisniewski, KowalskiJanSwapped, DisplayOnly, Compound, SecondGiven, OtherKowalski,
        AnnaKowalski,
    ]);

    [Fact]
    public void Lookup_IsTheUnionOfAllLevels_InInputOrder()
    {
        var found = Index().Lookup(Name("Jan", "Kowalski"));

        Assert.Equal([JanKowalski, KowalskiJanSwapped, DisplayOnly, Compound, SecondGiven], found);
    }

    [Fact]
    public void Lookup_FoldsDiacritics_AndNeverReturnsASurnameAloneWithAnotherGivenName()
    {
        var index = Index();

        Assert.Equal([LukaszWisniewski], index.Lookup(Name("Lukasz", "Wisniewski")));
        Assert.Equal([LukaszWisniewski], index.Lookup(Name("ŁUKASZ", "WIŚNIEWSKI")));
        Assert.Equal([PiotrWisniewski], index.Lookup(Name("Piotr", "Wiśniewski")));
        Assert.Empty(index.Lookup(Name("Karolina", "Malinowska")));
        Assert.Empty(index.Lookup(Name("Zofia", "Wiśniewski")));
    }

    [Fact]
    public void Lookup_CompoundSurnames_OnEitherSide()
    {
        var index = new PersonIndex([Person("Anna", "Kowalska-Nowak"), Person("Anna", "Nowak"), Person("Anna", "Kowalska"), Person("Anna", "Nowak-Zielińska")]);

        // "Nowak" is a part of "Kowalska-Nowak" and of "Nowak-Zielińska"; "Kowalska-Nowak" contains "Nowak" and "Kowalska".
        Assert.Equal(["Kowalska-Nowak", "Nowak", "Nowak-Zielińska"], index.Lookup(Name("Anna", "Nowak")).Select(p => p.Surname));
        Assert.Equal(["Kowalska-Nowak", "Nowak", "Kowalska"], index.Lookup(Name("Anna", "Kowalska - Nowak")).Select(p => p.Surname));
        Assert.Empty(index.Lookup(Name("Jan", "Nowak")));
    }

    [Fact]
    public void Lookup_SurnameOnly_FindsEveryPersonOfThatSurname()
    {
        var found = Index().Lookup(Name("", "Kowalski"));

        Assert.Equal([JanKowalski, Compound, SecondGiven, AnnaKowalski], found);
    }

    [Fact]
    public void Lookup_DisplayNameWithTitlesAndRemarks()
    {
        var professor = Person("", "", displayName: "prof. dr hab. Stanisław Kowalczyk (Rektorat)", cn: "SK");
        var reversed = Person("", "", displayName: "Kowalczyk, Stanisław", cn: "SK2");
        var index = new PersonIndex([professor, reversed, Person("Stanisław", "Kowalczyk-Ćwik", displayName: "Stanisław Kowalczyk-Ćwik")]);

        var found = index.Lookup(Name("Stanisław", "Kowalczyk"));

        Assert.Equal(3, found.Count);
        Assert.Equal(professor, found[0]);
        Assert.Equal(reversed, found[1]);
    }

    [Fact]
    public void Count_SkipsNullsAndRepeatedDns()
    {
        var copy = JanKowalski with { DistinguishedName = JanKowalski.DistinguishedName.ToUpperInvariant(), GivenName = "Janusz" };
        var index = new PersonIndex([JanKowalski, null!, copy, LukaszWisniewski, JanKowalski with { DistinguishedName = " " }]);

        Assert.Equal(2, index.Count);
        Assert.Equal([JanKowalski], index.Lookup(Name("Jan", "Kowalski")));
        Assert.Empty(index.Lookup(Name("Janusz", "Kowalski")));
        Assert.Equal(nameof(PersonIndex), index.ToString());
    }

    [Fact]
    public void Lookup_EmptyName_IsEmpty_AndNullsThrow()
    {
        Assert.Empty(Index().Lookup(new OwnerName(" ", "", "")));
        Assert.Throws<ArgumentNullException>(() => Index().Lookup(null!));
        Assert.Throws<ArgumentNullException>(() => new PersonIndex(null!));
        Assert.Equal(0, new PersonIndex([]).Count);
        Assert.Empty(new PersonIndex([]).Lookup(Name("Jan", "Kowalski")));
    }

    [Fact]
    public void Lookup_InALargeIndex_FindsOnlyTheMatchingPersons()
    {
        var persons = Enumerable.Range(0, 20_000)
            .Select(i => Person($"Osoba{i}", $"Nazwisko{i % 500}", cn: $"Osoba {i}"))
            .Append(Person("Żaneta", "Szymańska"))
            .ToList();

        var index = new PersonIndex(persons);

        Assert.Equal(20_001, index.Count);
        Assert.Equal("Szymańska", Assert.Single(index.Lookup(Name("Zaneta", "Szymanska"))).Surname);
        Assert.Equal(40, index.Lookup(Name("", "Nazwisko7")).Count);
        Assert.Single(index.Lookup(Name("Osoba7", "Nazwisko7")));
    }
}
