using AdMng.Core.Correspondence.Mailboxes;
using static AdMng.Core.Tests.Correspondence.Mailboxes.MailboxTestData;

namespace AdMng.Core.Tests.Correspondence.Mailboxes;

/// <summary>The matching rules of design note 3.7: levels, scores, status, tie-breaks and options.</summary>
public class MailboxOwnerMatcherTests
{
    private static readonly MailboxEntry Sekretariat = Mailbox();

    [Fact]
    public void Levels_EachRule()
    {
        var owner = Name("Łukasz", "Wiśniewski");

        Assert.Equal(OwnerMatchLevel.Exact, MailboxOwnerMatcher.LevelOf(owner, Person("łukasz", "WIŚNIEWSKI")));
        Assert.Equal(OwnerMatchLevel.Exact, MailboxOwnerMatcher.LevelOf(owner, Person(" Łukasz ", "Wiśniewski")));
        Assert.Equal(OwnerMatchLevel.Folded, MailboxOwnerMatcher.LevelOf(owner, Person("Lukasz", "Wisniewski")));
        Assert.Equal(OwnerMatchLevel.Swapped, MailboxOwnerMatcher.LevelOf(owner, Person("Wisniewski", "Łukasz")));
        Assert.Equal(OwnerMatchLevel.DisplayName, MailboxOwnerMatcher.LevelOf(owner, Person("Ł.", "Wiśniewski-Kowalski", displayName: "dr Łukasz Wiśniewski")));
        Assert.Equal(OwnerMatchLevel.DisplayName, MailboxOwnerMatcher.LevelOf(owner, Person("", "", displayName: "Wiśniewski Łukasz (IT)")));
        Assert.Equal(OwnerMatchLevel.Partial, MailboxOwnerMatcher.LevelOf(owner, Person("Łukasz", "Wiśniewski-Kowalski")));
        Assert.Equal(OwnerMatchLevel.Partial, MailboxOwnerMatcher.LevelOf(owner, Person("Łukasz Jan", "Wiśniewski")));
        Assert.Null(MailboxOwnerMatcher.LevelOf(owner, Person("Piotr", "Wiśniewski")));
        Assert.Null(MailboxOwnerMatcher.LevelOf(owner, Person("Łukasz", "Kowalski")));
        Assert.Null(MailboxOwnerMatcher.LevelOf(new OwnerName("", "", ""), Person("Łukasz", "Wiśniewski")));
    }

    [Fact]
    public void Levels_ExactNeedsTheSameLetters_DecomposedUnicodeIsTheSame()
    {
        var decomposed = $"Wo{(char)0x0301}jcik";

        Assert.Equal(OwnerMatchLevel.Exact, MailboxOwnerMatcher.LevelOf(Name("Tomasz", "Wójcik"), Person("Tomasz", decomposed)));
        Assert.Equal(OwnerMatchLevel.Folded, MailboxOwnerMatcher.LevelOf(Name("Tomasz", "Wojcik"), Person("Tomasz", "Wójcik")));
        Assert.Equal(OwnerMatchLevel.Folded, MailboxOwnerMatcher.LevelOf(Name("Anna", "Kowalska Nowak"), Person("Anna", "Kowalska-Nowak")));
    }

    [Fact]
    public void Levels_SwappingNeedsBothParts_AndASingleWordIsASurname()
    {
        Assert.Null(MailboxOwnerMatcher.LevelOf(Name("", "Jan"), Person("Jan", "", displayName: "Jan K.")));
        Assert.Equal(OwnerMatchLevel.DisplayName, MailboxOwnerMatcher.LevelOf(Name("", "Jan"), Person("Jan", "")));
        Assert.Equal(OwnerMatchLevel.Partial, MailboxOwnerMatcher.LevelOf(Name("", "Kowalski"), Person("Jan", "Kowalski")));
        Assert.Equal(OwnerMatchLevel.Partial, MailboxOwnerMatcher.LevelOf(Name("", "Nowak"), Person("Anna", "Kowalska-Nowak")));
        Assert.Equal(OwnerMatchLevel.DisplayName, MailboxOwnerMatcher.LevelOf(Name("", "Kowalski"), Person("", "", displayName: "Kowalski")));
        Assert.Null(MailboxOwnerMatcher.LevelOf(Name("Jan", ""), Person("Jan", "Kowalski")));
    }

    [Fact]
    public void NoName_ForNullOrEmptyNames()
    {
        var persons = new[] { Person("Jan", "Kowalski") };

        var none = Match(Sekretariat, null, persons);
        var empty = Match(Sekretariat, new OwnerName(" ", "", ""), persons);

        Assert.Equal(OwnerMatchStatus.NoName, none.Status);
        Assert.Equal(OwnerMatchStatus.NoName, empty.Status);
        Assert.Null(none.Best);
        Assert.Empty(none.Candidates);
    }

    [Fact]
    public void NotFound_WithoutCandidates()
    {
        var match = Match(Sekretariat, Name("Karolina", "Malinowska"), [Person("Jan", "Kowalski"), Person("Karolina", "Malinowski-Kowalska")]);

        Assert.Equal(OwnerMatchStatus.NotFound, match.Status);
        Assert.Null(match.Best);
        Assert.Empty(match.Candidates);
        Assert.Equal("NotFound (0)", match.ToString());
    }

    [Fact]
    public void Matched_ForASingleExactOrFoldedCandidate_ProbableForTheOtherLevels()
    {
        Assert.Equal(OwnerMatchStatus.Matched, Match(Sekretariat, Name("Jan", "Kowalski"), [Person("Jan", "Kowalski")]).Status);
        Assert.Equal(OwnerMatchStatus.Matched, Match(Sekretariat, Name("Zaneta", "Szymanska"), [Person("Żaneta", "Szymańska")]).Status);
        Assert.Equal(OwnerMatchStatus.Probable, Match(Sekretariat, Name("Szymańska", "Żaneta"), [Person("Żaneta", "Szymańska")]).Status);
        Assert.Equal(OwnerMatchStatus.Probable, Match(Sekretariat, Name("Żaneta", "Szymańska"), [Person("", "", displayName: "Szymańska Żaneta")]).Status);
        Assert.Equal(OwnerMatchStatus.Probable, Match(Sekretariat, Name("Żaneta", "Szymańska"), [Person("Żaneta", "Szymańska-Kowalska")]).Status);
        Assert.Equal(OwnerMatchStatus.Probable, Match(Sekretariat, Name("", "Szymańska"), [Person("Żaneta", "Szymańska")]).Status);
    }

    [Fact]
    public void Scores_LevelWeightPlusDelegateEnabledAndAddress()
    {
        Assert.Equal(50, MailboxOwnerMatcher.WeightOf(OwnerMatchLevel.Exact));
        Assert.Equal(45, MailboxOwnerMatcher.WeightOf(OwnerMatchLevel.Folded));
        Assert.Equal(35, MailboxOwnerMatcher.WeightOf(OwnerMatchLevel.Swapped));
        Assert.Equal(30, MailboxOwnerMatcher.WeightOf(OwnerMatchLevel.DisplayName));
        Assert.Equal(15, MailboxOwnerMatcher.WeightOf(OwnerMatchLevel.Partial));
        Assert.Throws<ArgumentOutOfRangeException>(() => MailboxOwnerMatcher.WeightOf((OwnerMatchLevel)7));

        var full = Person("Jan", "Kowalski");
        var delegateMailbox = Mailbox("Sekretariat", full.DistinguishedName);
        Assert.Equal(105, Match(delegateMailbox, Name("Jan", "Kowalski"), [full]).Best!.Score);
        Assert.Equal(65, Match(Sekretariat, Name("Jan", "Kowalski"), [full]).Best!.Score);
        Assert.Equal(60, Match(Sekretariat, Name("Jan", "Kowalski"), [Person("Jan", "Kowalski", withAddress: false)]).Best!.Score);
        Assert.Equal(55, Match(Sekretariat, Name("Jan", "Kowalski"), [Person("Jan", "Kowalski", disabled: true)]).Best!.Score);
        Assert.Equal(15, Match(Sekretariat, Name("Jan", "Kowalski"), [Person("Jan Maria", "Kowalski", disabled: true, withAddress: false)]).Best!.Score);
    }

    [Fact]
    public void TheBestLevelDecides_EvenAgainstAHigherScore()
    {
        var exactDisabled = Person("Jan", "Kowalski", disabled: true, withAddress: false, ou: "OU=Byli,OU=Firma,DC=contoso,DC=test");
        var folded = Person("Ján", "Kowalski");

        var match = Match(Sekretariat, Name("Jan", "Kowalski"), [folded, exactDisabled]);

        Assert.Equal(OwnerMatchStatus.OwnerDisabled, match.Status);
        Assert.Same(exactDisabled, match.Best!.Person);
        Assert.Equal(OwnerMatchLevel.Exact, match.Best.Level);
        Assert.Equal([folded, exactDisabled], match.Candidates.Select(c => c.Person));
        Assert.Equal([60, 50], match.Candidates.Select(c => c.Score));
    }

    [Fact]
    public void Ambiguous_ForSeveralCandidatesAtTheBestLevel()
    {
        var it = Person("Jan", "Nowak", ou: "OU=IT,OU=Pracownicy,OU=Firma,DC=contoso,DC=test");
        var sales = Person("Jan", "Nowak", ou: "OU=Sprzedaż,OU=Pracownicy,OU=Firma,DC=contoso,DC=test");
        var partial = Person("Jan", "Nowak-Zielińska");

        var match = Match(Sekretariat, Name("Jan", "Nowak"), [partial, sales, it]);

        Assert.Equal(OwnerMatchStatus.Ambiguous, match.Status);
        Assert.Null(match.Best);
        Assert.Equal(3, match.Candidates.Count);
        Assert.Equal(OwnerMatchLevel.Partial, match.Candidates[^1].Level);
    }

    [Fact]
    public void PreferEnabled_LeavesTheOnlyEnabledCandidate_OtherwiseAmbiguous()
    {
        var enabled = Person("Jan", "Nowak", ou: "OU=IT,OU=Pracownicy,OU=Firma,DC=contoso,DC=test");
        var disabled = Person("Jan", "Nowak", disabled: true, ou: "OU=Byli,OU=Firma,DC=contoso,DC=test");
        var secondDisabled = Person("Jan", "Nowak", disabled: true, ou: "OU=Archiwum,OU=Firma,DC=contoso,DC=test");

        var preferred = Match(Sekretariat, Name("Jan", "Nowak"), [disabled, enabled]);
        var notPreferred = Match(Sekretariat, Name("Jan", "Nowak"), [disabled, enabled], new OwnerMatchOptions { PreferEnabled = false });
        var allDisabled = Match(Sekretariat, Name("Jan", "Nowak"), [disabled, secondDisabled]);
        var twoEnabled = Match(Sekretariat, Name("Jan", "Nowak"), [enabled, Person("Jan", "Nowak", ou: "OU=Sprzedaż,OU=Firma,DC=contoso,DC=test"), disabled]);

        Assert.Equal(OwnerMatchStatus.Matched, preferred.Status);
        Assert.Same(enabled, preferred.Best!.Person);
        Assert.Equal(OwnerMatchStatus.Ambiguous, notPreferred.Status);
        Assert.Null(notPreferred.Best);
        Assert.Equal(OwnerMatchStatus.Ambiguous, allDisabled.Status);
        Assert.Equal(OwnerMatchStatus.Ambiguous, twoEnabled.Status);
    }

    [Fact]
    public void PreferEnabled_WorksOnlyWithinTheBestLevel()
    {
        // One exact candidate, disabled: a folded enabled one does not replace it.
        var match = Match(Sekretariat, Name("Paweł", "Lewandowski"),
            [Person("Paweł", "Lewandowski", disabled: true), Person("Pawel", "Lewandowski", ou: "OU=IT,OU=Firma,DC=contoso,DC=test")]);

        Assert.Equal(OwnerMatchStatus.OwnerDisabled, match.Status);
        Assert.True(match.Best!.Person.IsDisabled);
    }

    [Fact]
    public void OwnerWithoutAddress_AndOwnerDisabledWins()
    {
        var withoutAddress = Match(Sekretariat, Name("Jan", "Kowalski"), [Person("Jan", "Kowalski", withAddress: false)]);
        var both = Match(Sekretariat, Name("Jan", "Kowalski"), [Person("Jan", "Kowalski", disabled: true, withAddress: false)]);
        var whitespaceAddress = Match(Sekretariat, Name("Jan", "Kowalski"), [Person("Jan", "Kowalski") with { Address = " " }]);

        Assert.Equal(OwnerMatchStatus.OwnerWithoutAddress, withoutAddress.Status);
        Assert.NotNull(withoutAddress.Best);
        Assert.Equal(OwnerMatchStatus.OwnerDisabled, both.Status);
        Assert.Equal(OwnerMatchStatus.OwnerWithoutAddress, whitespaceAddress.Status);
    }

    [Fact]
    public void Delegate_ConfirmsOneCandidate_AtAnyLevel()
    {
        var it = Person("Jan", "Nowak", ou: "OU=IT,OU=Firma,DC=contoso,DC=test");
        var sales = Person("Jan", "Nowak", ou: "OU=Sprzedaż,OU=Firma,DC=contoso,DC=test");
        var compound = Person("Jan", "Nowak-Zielińska");

        var amongEqual = Match(Mailbox("Laboratorium", sales.DistinguishedName.ToUpperInvariant()), Name("Jan", "Nowak"), [it, sales]);
        var partialDelegate = Match(Mailbox("Laboratorium", compound.DistinguishedName), Name("Jan", "Nowak"), [it, compound]);

        Assert.Equal(OwnerMatchStatus.MatchedByDelegate, amongEqual.Status);
        Assert.Same(sales, amongEqual.Best!.Person);
        Assert.True(amongEqual.Best.IsDelegate);
        Assert.Same(sales, amongEqual.Candidates[0].Person);
        Assert.Equal(OwnerMatchStatus.MatchedByDelegate, partialDelegate.Status);
        Assert.Same(compound, partialDelegate.Best!.Person);
        Assert.Equal(OwnerMatchLevel.Partial, partialDelegate.Best.Level);
        Assert.Equal(70, partialDelegate.Best.Score);
        Assert.Equal(65, partialDelegate.Candidates[1].Score);
    }

    [Fact]
    public void Delegate_TwoDelegatesFallBackToTheLevels_ADelegateWithoutTheNameIsIgnored()
    {
        var it = Person("Jan", "Nowak", ou: "OU=IT,OU=Firma,DC=contoso,DC=test");
        var sales = Person("Jan", "Nowak", ou: "OU=Sprzedaż,OU=Firma,DC=contoso,DC=test");
        var assistant = Person("Grażyna", "Dąbrowska");

        var twoDelegates = Match(Mailbox("Laboratorium", it.DistinguishedName, sales.DistinguishedName), Name("Jan", "Nowak"), [it, sales, assistant]);
        var notACandidate = Match(Mailbox("Sekretariat", assistant.DistinguishedName), Name("Jan", "Nowak"), [it, assistant]);
        var disabledDelegate = Match(Mailbox("Laboratorium", sales.DistinguishedName), Name("Jan", "Nowak"), [it, sales with { IsDisabled = true }]);

        Assert.Equal(OwnerMatchStatus.Ambiguous, twoDelegates.Status);
        Assert.All(twoDelegates.Candidates, c => Assert.True(c.IsDelegate));
        Assert.Equal(OwnerMatchStatus.Matched, notACandidate.Status);
        Assert.False(notACandidate.Best!.IsDelegate);
        Assert.Equal(OwnerMatchStatus.OwnerDisabled, disabledDelegate.Status);
        Assert.True(disabledDelegate.Best!.IsDelegate);
    }

    [Fact]
    public void TheMailboxItself_IsNeverACandidate()
    {
        var mailbox = Mailbox("Jan Kowalski");
        var self = Person("Jan", "Kowalski", ou: MailboxOu) with { DistinguishedName = mailbox.DistinguishedName.ToLowerInvariant().Replace(",", ", ", StringComparison.Ordinal) };

        var match = Match(mailbox, Name("Jan", "Kowalski"), [self], new OwnerMatchOptions { ExcludeMailboxAccounts = false });

        Assert.Equal(OwnerMatchStatus.NotFound, match.Status);
    }

    [Fact]
    public void ExcludeMailboxAccounts_DropsFunctionalAccountsOnly()
    {
        var shared = Person("Jan", "Kowalski", type: MailboxType.Shared, ou: MailboxOu);
        var remoteRoom = Person("Jan", "Kowalski", type: MailboxType.RemoteRoom, ou: "OU=Sale,OU=Firma,DC=contoso,DC=test");
        var personal = Person("Jan", "Kowalski", type: MailboxType.User);

        var excluded = Match(Sekretariat, Name("Jan", "Kowalski"), [shared, remoteRoom, personal]);
        var included = Match(Sekretariat, Name("Jan", "Kowalski"), [shared, remoteRoom, personal], new OwnerMatchOptions { ExcludeMailboxAccounts = false });
        var onlyShared = Match(Sekretariat, Name("Jan", "Kowalski"), [shared]);

        Assert.Equal(OwnerMatchStatus.Matched, excluded.Status);
        Assert.Same(personal, excluded.Best!.Person);
        Assert.Single(excluded.Candidates);
        Assert.Equal(OwnerMatchStatus.Ambiguous, included.Status);
        Assert.Equal(3, included.Candidates.Count);
        Assert.Equal(OwnerMatchStatus.NotFound, onlyShared.Status);
    }

    [Fact]
    public void Candidates_ByScore_ThenFoldedDisplayName_ThenDn()
    {
        var zofia = Person("Jan", "Kowalski", displayName: "Żaneta? Nie – Jan Kowalski (Zofia)", ou: "OU=A,OU=Firma,DC=contoso,DC=test");
        var lukasz = Person("Jan", "Kowalski", displayName: "Łukasz", ou: "OU=B,OU=Firma,DC=contoso,DC=test");
        var lukaszAgain = Person("Jan", "Kowalski", displayName: "Lukasz", ou: "OU=C,OU=Firma,DC=contoso,DC=test");
        var aDisabled = Person("Jan", "Kowalski", displayName: "Adam", disabled: true, ou: "OU=D,OU=Firma,DC=contoso,DC=test");

        var match = Match(Sekretariat, Name("Jan", "Kowalski"), [zofia, aDisabled, lukaszAgain, lukasz]);

        // 65, 65, 65 by "lukasz" (B before C by DN) and "zaneta…", then the disabled one (55).
        Assert.Equal([lukasz, lukaszAgain, zofia, aDisabled], match.Candidates.Select(c => c.Person));
        Assert.Equal(OwnerMatchStatus.Ambiguous, match.Status);
    }

    [Fact]
    public void Candidates_AreCapped_TheChosenOneIsKept()
    {
        var chosen = Person("Jan", "Kowalski", disabled: true, withAddress: false, cn: "Jan Kowalski (stary)");
        var folded = Enumerable.Range(0, 25).Select(i => Person("Ján", "Kowalski", cn: $"Ján Kowalski {i:00}")).ToList();

        var match = Match(Sekretariat, Name("Jan", "Kowalski"), [.. folded, chosen]);
        var many = Match(Sekretariat, Name("Ján", "Kowalski"), folded);

        Assert.Equal(OwnerMatchStatus.OwnerDisabled, match.Status);
        Assert.Equal(MailboxOwnerMatcher.MaxCandidates, match.Candidates.Count);
        Assert.Contains(match.Best!, match.Candidates);
        Assert.Same(chosen, match.Candidates[^1].Person);
        Assert.Equal(OwnerMatchStatus.Ambiguous, many.Status);
        Assert.Equal(MailboxOwnerMatcher.MaxCandidates, many.Candidates.Count);
        Assert.Equal(folded.Take(20), many.Candidates.Select(c => c.Person));
    }

    [Fact]
    public void TitlesInDisplayNames_CompoundSurnames_AndSingleWords()
    {
        var professor = Person("", "", displayName: "prof. dr hab. Stanisław Kowalczyk", cn: "Rektor");
        var compound = Person("Anna", "Kowalska-Nowak");

        Assert.Equal(OwnerMatchStatus.Probable, Match(Sekretariat, Name("Stanisław", "Kowalczyk"), [professor]).Status);
        Assert.Equal(OwnerMatchLevel.DisplayName, Match(Sekretariat, Name("Stanislaw", "Kowalczyk"), [professor]).Best!.Level);
        Assert.Equal(OwnerMatchStatus.Probable, Match(Sekretariat, Name("Anna", "Nowak"), [compound]).Status);
        Assert.Equal(OwnerMatchStatus.Matched, Match(Sekretariat, Name("Anna", "Kowalska-Nowak"), [compound, Person("Anna", "Nowak")]).Status);
        Assert.Equal(OwnerMatchStatus.Ambiguous, Match(Sekretariat, Name("Anna", "Kowalska-Nowak"), [Person("Anna", "Kowalska"), Person("Anna", "Nowak")]).Status);
        Assert.Equal(OwnerMatchStatus.Ambiguous, Match(Sekretariat, Name("", "Nowak"), [compound, Person("Jan", "Nowak")]).Status);
        Assert.Equal(OwnerMatchStatus.NotFound, Match(Sekretariat, Name("", "techniczna"), [compound]).Status);
    }

    [Fact]
    public void Arguments_AreChecked_AndToStringsHideNamesAndAddresses()
    {
        var person = Person("Łukasz", "Wiśniewski");
        var index = new PersonIndex([person]);
        var match = MailboxOwnerMatcher.Match(Sekretariat, Name("Łukasz", "Wiśniewski"), index, new OwnerMatchOptions());

        Assert.Throws<ArgumentNullException>(() => MailboxOwnerMatcher.Match(null!, null, index, new OwnerMatchOptions()));
        Assert.Throws<ArgumentNullException>(() => MailboxOwnerMatcher.Match(Sekretariat, null, null!, new OwnerMatchOptions()));
        Assert.Throws<ArgumentNullException>(() => MailboxOwnerMatcher.Match(Sekretariat, null, index, null!));
        Assert.Throws<ArgumentNullException>(() => MailboxOwnerMatcher.LevelOf(null!, person));
        Assert.Throws<ArgumentNullException>(() => MailboxOwnerMatcher.LevelOf(Name("Jan", "Kowalski"), null!));
        Assert.Equal("Matched (1)", match.ToString());
        foreach (var text in new[] { match.ToString(), match.Best!.ToString(), person.ToString(), Sekretariat.ToString(), new OwnerMatchOptions().ToString() })
        {
            Assert.DoesNotContain("Wiśniewski", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("@", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Sekretariat", text, StringComparison.Ordinal);
        }
    }
}
