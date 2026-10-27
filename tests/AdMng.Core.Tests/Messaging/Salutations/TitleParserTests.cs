using AdMng.Core.Messaging.Salutations;

namespace AdMng.Core.Tests.Messaging.Salutations;

/// <summary>Titles of the title attribute and the display name (U1d, design note section 15).</summary>
public sealed class TitleParserTests
{
    private static readonly TitleTable Table = TitleTable.Default;

    [Theory]
    [InlineData("prof. dr hab.", "prof. dr hab.", "prof.")]
    [InlineData("Prof. Dr.", "prof. dr", "prof.")]
    [InlineData("dr.hab.inż.", "dr hab. inż.", "dr hab.")]
    [InlineData("DR HAB. INŻ.", "dr hab. inż.", "dr hab.")]
    [InlineData("dr n. med.", "dr n. med.", "dr")]
    [InlineData("dr hab. n. o zdr.", "dr hab. n. o zdr.", "dr hab.")]
    [InlineData("lek. med.", "lek.", "lek.")]
    [InlineData("ks. prof.", "ks. prof.", "ks. prof.")]
    [InlineData("ks. dr", "ks. dr", "ks. dr")]
    [InlineData("mgr inż.", "mgr inż.", "mgr")]
    [InlineData("mgr inz", "mgr inż.", "mgr")]
    [InlineData("doktor habilitowany", "dr hab.", "dr hab.")]
    [InlineData("prof. zw. dr hab.", "prof. dr hab.", "prof.")]
    public void Attribute_RecognizesTheChain_InCanonicalSpelling(string value, string title, string highest)
    {
        var info = TitleParser.ParseAttribute(value, Table);

        Assert.Equal(DerivedValueSource.TitleAttribute, info.Source);
        Assert.Equal(title, info.Text);
        Assert.Equal(highest, info.Highest);
        Assert.Empty(info.Unrecognized);
        Assert.Equal(Gender.Unknown, info.HonorificGender);
    }

    [Theory]
    [InlineData("Pan", Gender.Masculine)]
    [InlineData("Pani", Gender.Feminine)]
    [InlineData("Mr", Gender.Masculine)]
    [InlineData("Mrs.", Gender.Feminine)]
    [InlineData("Ms.", Gender.Feminine)]
    [InlineData("Miss", Gender.Feminine)]
    public void Attribute_HonorificWords_SetTheGender_AndAreNoTitle(string value, Gender gender)
    {
        var info = TitleParser.ParseAttribute(value, Table);

        Assert.False(info.HasTitle);
        Assert.Equal(DerivedValueSource.None, info.Source);
        Assert.Equal(gender, info.HonorificGender);
        Assert.Empty(info.Unrecognized);
    }

    [Theory]
    [InlineData("prof. uczelni", "prof. uczelni")]
    [InlineData("Prof. ucz.", "prof. uczelni")]
    [InlineData("prof. nadzw. dr hab.", "prof. nadzw.")]
    [InlineData("lek. dent.", "lek. dent.")]
    [InlineData("lek. stom.", "lek. dent.")]
    [InlineData("lek. wet.", "lek. wet.")]
    public void Attribute_DistinctTitles_KeepTheirMeaning(string value, string highest)
    {
        // A university professor is no professor (title), a dentist no physician: never printed as "prof." / "lek.".
        var info = TitleParser.ParseAttribute(value, Table);

        Assert.Equal(highest, info.Highest);
        Assert.StartsWith(highest, info.Text, StringComparison.Ordinal);
        Assert.Empty(info.Unrecognized);
    }

    [Fact]
    public void DisplayName_UniversityProfessor_IsNotPromoted()
    {
        var person = SalutationPerson.Of(null, "dr hab. Ewa Zielińska, prof. uczelni", "Ewa", "Zielińska", null, SalutationOptions.Default);

        Assert.Equal("dr hab., prof. uczelni", person.Title.Text);
        Assert.Equal("prof. uczelni", person.Title.Highest);
        Assert.Equal("dr hab. Ewa Zielińska, prof. uczelni", SalutationResolver.TitledName(person, SalutationOptions.Default).Text);
        Assert.Equal("Szanowna Pani Profesor", SalutationResolver.Salutation(person, AdMng.Core.Messaging.MessageLanguage.Polish, SalutationVariant.Full, SalutationOptions.Default).Text);

        var dentist = SalutationPerson.Of("lek. dent.", "Anna Nowak", "Anna", "Nowak", null, SalutationOptions.Default);
        Assert.Equal("lek. dent. Anna Nowak", SalutationResolver.TitledName(dentist, SalutationOptions.Default).Text);
        Assert.Equal("Szanowna Pani Doktor", SalutationResolver.Salutation(dentist, AdMng.Core.Messaging.MessageLanguage.Polish, SalutationVariant.Full, SalutationOptions.Default).Text);
    }

    [Fact]
    public void Attribute_HonorificBeforeATitle_KeepsBoth()
    {
        var info = TitleParser.ParseAttribute("Pani dr", Table);

        Assert.Equal("dr", info.Text);
        Assert.Equal(Gender.Feminine, info.HonorificGender);
    }

    [Fact]
    public void Attribute_UnknownWords_AreReported_AndTheCoveredPartStays()
    {
        Assert.Equal(["MBA"], TitleParser.ParseAttribute("MBA", Table).Unrecognized);
        Assert.False(TitleParser.ParseAttribute("MBA", Table).HasTitle);

        var mixed = TitleParser.ParseAttribute("dr MBA", Table);
        Assert.Equal("dr", mixed.Text);
        Assert.Equal(["MBA"], mixed.Unrecognized);
    }

    [Fact]
    public void Attribute_UnknownWordsOnly_StillParsesTheDisplayName()
    {
        var info = TitleParser.Parse("MBA", "dr Anna Nowak", "Anna", "Nowak", Table);

        Assert.Equal(DerivedValueSource.DisplayName, info.Source);
        Assert.Equal("dr", info.Text);
        Assert.Equal(["MBA"], info.Unrecognized);
    }

    [Fact]
    public void Attribute_WithATitle_Wins_OverTheDisplayName()
    {
        var info = TitleParser.Parse("dr", "prof. dr hab. Jan Kowalski", "Jan", "Kowalski", Table);

        Assert.Equal(DerivedValueSource.TitleAttribute, info.Source);
        Assert.Equal("dr", info.Text);
    }

    [Theory]
    [InlineData("prof. dr hab. Jan Kowalski", "Jan", "Kowalski", "prof. dr hab.", "", "Jan Kowalski")]
    [InlineData("dr Kowalski Jan", "Jan", "Kowalski", "dr", "", "Kowalski Jan")]
    [InlineData("Dr. Anna Nowak", "Anna", "Nowak", "dr", "", "Anna Nowak")]
    [InlineData("dr hab. Ewa Zielińska, prof. UW", "Ewa", "Zielińska", "dr hab.", "prof. UW", "Ewa Zielińska")]
    [InlineData("Ewa Zielińska, prof. AGH", "Ewa", "Zielińska", "", "prof. AGH", "Ewa Zielińska")]
    [InlineData("Kowalski Jan, dr hab.", "Jan", "Kowalski", "", "dr hab.", "Kowalski Jan")]
    [InlineData("lek. med. Grażyna Dąbrowska", "Grażyna", "Dąbrowska", "lek.", "", "Grażyna Dąbrowska")]
    [InlineData("prof. PWr Tomasz Wójcik", "Tomasz", "Wójcik", "prof. PWr", "", "Tomasz Wójcik")]
    public void DisplayName_PrefixAndSuffix(string displayName, string givenName, string surname, string prefix, string suffix, string rest)
    {
        var info = TitleParser.Parse(null, displayName, givenName, surname, Table);

        Assert.Equal(DerivedValueSource.DisplayName, info.Source);
        Assert.Equal(prefix, info.PrefixText);
        Assert.Equal(suffix, info.SuffixText);
        Assert.Equal(rest, info.NameWithoutTitle);
    }

    [Fact]
    public void DisplayName_SuffixTitle_ComposesTheTitleText()
    {
        var info = TitleParser.Parse(null, "dr hab. Ewa Zielińska, prof. UW", "Ewa", "Zielińska", Table);

        Assert.Equal("dr hab., prof. UW", info.Text);
        Assert.Equal("prof.", info.Highest);
        Assert.Equal(["prof.", "dr hab.", "dr"], info.Candidates.Select(c => c.Title));
    }

    [Theory]
    [InlineData("Dr Oetker Polska", "Anna", "Nowak")]
    [InlineData("Dr", "Anna", "Nowak")]
    [InlineData("dr Anna Nowak", null, null)]
    [InlineData("Kowalski, Jan", "Jan", "Kowalski")]
    [InlineData("Jan Kowalski", "Jan", "Kowalski")]
    public void DisplayName_WithoutTheNameOrATitle_IsNotCut(string displayName, string? givenName, string? surname)
    {
        var info = TitleParser.Parse(null, displayName, givenName, surname, Table);

        Assert.False(info.HasTitle);
        Assert.Equal(DerivedValueSource.None, info.Source);
    }

    [Fact]
    public void DisplayName_SurnameThatLooksLikeAUniversity_IsNotAbsorbed()
    {
        var info = TitleParser.Parse(null, "prof. NOWAK Jan", "Jan", "Nowak", Table);

        Assert.Equal("prof.", info.Text);
        Assert.Equal("NOWAK Jan", info.NameWithoutTitle);
    }

    [Fact]
    public void DisplayName_IsIgnored_WhenSwitchedOff()
    {
        var info = TitleParser.Parse(null, "prof. dr hab. Jan Kowalski", "Jan", "Kowalski", Table, fromDisplayName: false);

        Assert.False(info.HasTitle);
    }

    [Fact]
    public void Tokenize_SplitsAfterInnerDots_AndOnCommas()
    {
        Assert.Equal(["dr.", "hab.", "inż."], TitleParser.Tokenize("dr.hab.inż.").Select(t => t.Text));
        Assert.Equal(["dr", "hab", "inz"], TitleParser.Tokenize("dr.hab.inż.").Select(t => t.Key));
        Assert.Equal(["Kowalski", "Jan", "dr"], TitleParser.Tokenize("Kowalski, Jan,dr").Select(t => t.Text));
        Assert.Empty(TitleParser.Tokenize(" . , - "));
    }

    [Theory]
    [InlineData("UW", true)]
    [InlineData("UJ", true)]
    [InlineData("AGH", true)]
    [InlineData("PWr", true)]
    [InlineData("SGGW", true)]
    [InlineData("Jan", false)]
    [InlineData("U", false)]
    [InlineData("uw", false)]
    [InlineData("UNIWERSYTET", false)]
    public void University_Abbreviations(string word, bool expected) => Assert.Equal(expected, TitleParser.IsUniversity(word));

    [Fact]
    public void OperatorTable_AddsTitles_AndSelectsByRank()
    {
        var table = new TitleTable([.. SalutationData.DefaultTitles, new TitleRule
        {
            Title = "płk",
            Variants = ["pułkownik"],
            Rank = 75,
            PolishMasculine = "Panie Pułkowniku",
            PolishFeminine = "Pani Pułkownik",
            English = "Colonel",
        }]);

        var info = TitleParser.ParseAttribute("płk dr", table);

        Assert.Equal("płk dr", info.Text);
        Assert.Equal(["płk", "dr"], info.Candidates.Select(c => c.Title));
        Assert.Equal("płk", TitleParser.ParseAttribute("Pułkownik", table).Text);
    }

    [Fact]
    public void EmptyTable_RecognizesNothing()
    {
        var info = TitleParser.ParseAttribute("prof. dr hab.", new TitleTable([]));

        Assert.False(info.HasTitle);
        Assert.Equal(["prof.", "dr", "hab."], info.Unrecognized);
    }

    [Fact]
    public void FromNull_IsTheDefaultTable()
    {
        Assert.Same(TitleTable.Default, TitleTable.From(null));
        Assert.Equal(15, TitleTable.Default.Rules.Count);
        Assert.Equal(["ks. prof.", "ks. dr hab.", "ks. dr"], TitleTable.Default.Rules.Take(3).Select(r => r.Title));
    }
}
