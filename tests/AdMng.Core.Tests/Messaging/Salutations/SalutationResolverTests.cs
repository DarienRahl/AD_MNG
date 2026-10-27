using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Salutations;

namespace AdMng.Core.Tests.Messaging.Salutations;

/// <summary>Composition of salutations and their fallbacks (U1d, design note sections 17 and 20).</summary>
public sealed class SalutationResolverTests
{
    private static readonly SalutationOptions Options = SalutationOptions.Default;

    private static SalutationPerson Person(string? title, string? displayName, string? givenName, string? surname, SalutationOptions? options = null, string? genderValue = null, Gender? chosen = null) =>
        SalutationPerson.Of(title, displayName ?? $"{givenName} {surname}".Trim(), givenName, surname, genderValue, options ?? Options, chosen);

    private static string Polish(SalutationPerson person, SalutationVariant variant = SalutationVariant.Full, SalutationOptions? options = null) =>
        SalutationResolver.Salutation(person, MessageLanguage.Polish, variant, options ?? Options).Text;

    private static string English(SalutationPerson person, SalutationVariant variant = SalutationVariant.Full, SalutationOptions? options = null) =>
        SalutationResolver.Salutation(person, MessageLanguage.English, variant, options ?? Options).Text;

    // The grammar examples of the design note (section 20).
    [Theory]
    [InlineData("prof. dr hab.", null, "Jan", "Kowalski", "Szanowny Panie Profesorze", "Dear Professor Kowalski")]
    [InlineData(null, "dr Anna Nowak", "Anna", "Nowak", "Szanowna Pani Doktor", "Dear Dr Nowak")]
    [InlineData("mgr inż.", null, "Piotr", "Wiśniewski", "Szanowny Panie", "Dear Mr Wiśniewski")]
    [InlineData(null, "dr hab. Ewa Zielińska, prof. UW", "Ewa", "Zielińska", "Szanowna Pani Profesor", "Dear Professor Zielińska")]
    [InlineData("ks. dr", null, "Tomasz", "Wójcik", "Szanowny Księże Doktorze", "Dear Father Wójcik")]
    [InlineData(null, null, "Kuba", "Mazur", "Szanowny Panie", "Dear Mr Mazur")]
    [InlineData(null, null, "Sasza", "Kamińska", "Szanowna Pani", "Dear Ms Kamińska")]
    [InlineData(null, null, "Sasza", "Kim", "Szanowna Pani / Szanowny Panie", "Dear Sasza Kim")]
    [InlineData("Pani", null, "Nikola", "Lewandowski", "Szanowna Pani", "Dear Ms Lewandowski")]
    [InlineData("dr hab.", null, "Grażyna", "Dąbrowska", "Szanowna Pani Profesor", "Dear Dr Dąbrowska")]
    [InlineData("ks. prof.", null, "Józef", "Tischner", "Szanowny Księże Profesorze", "Dear Father Tischner")]
    [InlineData("ks.", null, "Adam", "Boniecki", "Szanowny Księże", "Dear Father Boniecki")]
    [InlineData("lek. med.", null, "Małgorzata", "Wójcik", "Szanowna Pani Doktor", "Dear Dr Wójcik")]
    public void GrammarExamples(string? title, string? displayName, string givenName, string surname, string polish, string english)
    {
        var person = Person(title, displayName, givenName, surname);

        Assert.Equal(polish, Polish(person));
        Assert.Equal(english, English(person));
    }

    [Fact]
    public void Variants_NoTitleAndVocative()
    {
        var professor = Person("prof. dr hab.", null, "Jan", "Kowalski");

        Assert.Equal("Szanowny Panie", Polish(professor, SalutationVariant.NoTitle));
        Assert.Equal("Panie Profesorze", Polish(professor, SalutationVariant.Vocative));
        Assert.Equal("Panie", Polish(professor, SalutationVariant.VocativeNoTitle));
        Assert.Equal("Dear Mr Kowalski", English(professor, SalutationVariant.NoTitle));
        Assert.Equal("Professor Kowalski", English(professor, SalutationVariant.Vocative));
        Assert.Equal("Mr Kowalski", English(professor, SalutationVariant.VocativeNoTitle));

        var neutral = Person(null, null, "Sasza", "Kim");
        Assert.Equal("Pani / Panie", Polish(neutral, SalutationVariant.Vocative));
        Assert.Equal("Sasza Kim", English(neutral, SalutationVariant.Vocative));
    }

    [Fact]
    public void TitleRowWithoutAFormForTheGender_FallsToTheNextRank()
    {
        // "ks. dr" has no feminine form: the next rank with one ("dr") is used.
        var person = Person("ks. dr", null, "Anna", "Nowak");

        Assert.Equal("Szanowna Pani Doktor", Polish(person));
        Assert.Equal("Dear Father Nowak", English(person));

        // "ks." alone has no feminine form at all: the plain form.
        Assert.Equal("Szanowna Pani", Polish(Person("ks.", null, "Anna", "Nowak")));
    }

    [Fact]
    public void English_WithATitleForm_NeedsNoGender()
    {
        var result = SalutationResolver.Salutation(Person("prof.", null, "Sasza", "Kim"), MessageLanguage.English, SalutationVariant.Full, Options);

        Assert.Equal("Dear Professor Kim", result.Text);
        Assert.False(result.UsedGender);
        Assert.True(result.UsedTitle);
        Assert.False(result.IsNeutral);

        // Polish needs the gender for the adjective: the neutral text keeps the title.
        var polish = SalutationResolver.Salutation(Person("prof.", null, "Sasza", "Kim"), MessageLanguage.Polish, SalutationVariant.Full, Options);
        Assert.Equal("Szanowna Pani Profesor / Szanowny Panie Profesorze", polish.Text);
        Assert.True(polish.UsedGender);
        Assert.True(polish.IsNeutral);
    }

    [Fact]
    public void UnknownGender_WithATitle_KeepsTheTitle_OrTakesTheGenderTheTitleImplies()
    {
        // Both forms: the default neutral text with the title (no faux pas "Szanowna Pani / Szanowny Panie" to a professor).
        var professor = Person("prof. dr hab.", null, "Sasza", "Kim");
        Assert.Equal(Gender.Unknown, professor.Gender.Gender);
        Assert.Equal("Szanowna Pani Profesor / Szanowny Panie Profesorze", Polish(professor));
        Assert.Equal("Pani Profesor / Panie Profesorze", Polish(professor, SalutationVariant.Vocative));
        Assert.Equal("Szanowna Pani / Szanowny Panie", Polish(professor, SalutationVariant.NoTitle));
        Assert.Equal("Szanowna Pani Doktor / Szanowny Panie Doktorze", Polish(Person("dr", null, "Sasza", "Kim")));

        // An operator's own neutral text stays as it is; a title without forms changes nothing.
        var custom = Options with { NeutralPolish = "Szanowni Państwo" };
        Assert.Equal("Szanowni Państwo", Polish(Person("prof.", null, "Sasza", "Kim", custom), options: custom));
        Assert.Equal("Szanowna Pani / Szanowny Panie", Polish(Person("mgr inż.", null, "Sasza", "Kim")));

        // A title for one gender only ("ks.") decides an unknown gender – a guess from the title.
        var priest = Person("ks. dr", null, "J.", "Wójcik");
        Assert.Equal((Gender.Masculine, DerivedValueSource.TitleAttribute, true, "ks. dr"), (priest.Gender.Gender, priest.Gender.Source, priest.Gender.IsGuess, priest.Gender.Detail));
        Assert.Equal("Szanowny Księże Doktorze", Polish(priest));
        Assert.Equal("Pan", SalutationResolver.Honorific(priest, MessageLanguage.Polish, GrammaticalCase.Nominative, Options).Text);

        // … but never against the names or the operator's choice.
        Assert.Equal(Gender.Feminine, Person("ks. dr", null, "Anna", "Nowak").Gender.Gender);
        Assert.Equal((Gender.Unknown, DerivedValueSource.Override), (Person("ks.", null, "J.", "Wójcik", chosen: Gender.Unknown).Gender.Gender, Person("ks.", null, "J.", "Wójcik", chosen: Gender.Unknown).Gender.Source));
    }

    [Fact]
    public void English_WithoutASurname_IsNeutral()
    {
        var result = SalutationResolver.Salutation(Person(null, "Jan", "Jan", null), MessageLanguage.English, SalutationVariant.Full, Options);

        Assert.Equal("Dear Jan", result.Text);
        Assert.True(result.SurnameMissing);
        Assert.True(result.IsNeutral);

        var titled = SalutationResolver.Salutation(Person("dr", "Anna", "Anna", null), MessageLanguage.English, SalutationVariant.Full, Options);
        Assert.Equal("Dear Anna", titled.Text);
        Assert.True(titled.SurnameMissing);
    }

    [Fact]
    public void NeutralTexts_OfTheSettings_AndTheLastResort()
    {
        var options = Options with { NeutralPolish = "Szanowni Państwo", NeutralEnglish = "Hello {name}" };
        var person = Person(null, null, "Sasza", "Kim", options);

        Assert.Equal("Szanowni Państwo", Polish(person, options: options));
        Assert.Equal("Państwo", Polish(person, SalutationVariant.Vocative, options));
        Assert.Equal("Hello Sasza Kim", English(person, options: options));

        // No name at all: the fixed last resort of the language.
        var nobody = SalutationPerson.Of(null, null, null, null, null, Options);
        Assert.Equal("Dear Sir or Madam", English(nobody));
        Assert.Equal("Szanowna Pani / Szanowny Panie", Polish(nobody));
        Assert.Equal("Szanowni Państwo", Polish(nobody, options: Options with { NeutralPolish = "Dzień dobry {name}" }));

        // The display name without its title stands in for the name.
        var displayOnly = SalutationPerson.Of(null, "Sasza Kim", null, null, null, Options);
        Assert.Equal("Dear Sasza Kim", English(displayOnly));

        // Without given name and surname a title prefix or suffix is cut from the display name for {name} – but it is no
        // title of the person (nothing confirms the rest is a name).
        var titledDisplayOnly = SalutationPerson.Of(null, "dr Jan Kowalski", null, null, null, Options);
        Assert.Equal("Dear Jan Kowalski", English(titledDisplayOnly));
        Assert.False(titledDisplayOnly.Title.HasTitle);
        Assert.Equal("Kowalski Jan", SalutationPerson.Of(null, "Kowalski Jan, dr hab.", null, null, null, Options).Name);
        Assert.Equal("dr Jan Kowalski", SalutationPerson.Of(null, "dr Jan Kowalski", null, null, null, Options with { TitleFromDisplayName = false }).Name);
        Assert.Equal("Dr", SalutationPerson.Of(null, "Dr", null, null, null, Options).Name);
    }

    [Fact]
    public void UnknownGender_DefaultOfTheSettingsOrTheTemplate()
    {
        var feminine = Options.With(new TemplateSalutationOptions { UnknownGender = UnknownGenderMode.Feminine });
        var person = Person(null, null, "Sasza", "Kim", feminine);

        Assert.Equal("Szanowna Pani", Polish(person, options: feminine));
        Assert.Equal("Dear Ms Kim", English(person, options: feminine));
        Assert.Equal((Gender.Feminine, true), SalutationResolver.EffectiveGender(person.Gender, feminine));

        var masculine = Options with { UnknownGender = UnknownGenderMode.Masculine };
        Assert.Equal("Szanowny Panie", Polish(Person(null, null, "Sasza", "Kim", masculine), options: masculine));

        // The template's "from the settings" (null) keeps the settings' default.
        Assert.Equal(UnknownGenderMode.Masculine, masculine.With(new TemplateSalutationOptions { NeutralEnglish = "Hi {name}" }).UnknownGender);
    }

    [Fact]
    public void ConflictAndOverride()
    {
        var conflict = Person(null, null, "Jan", "Kowalska");
        Assert.Equal("Szanowna Pani / Szanowny Panie", Polish(conflict));

        var chosen = Person(null, null, "Jan", "Kowalska", chosen: Gender.Feminine);
        Assert.Equal("Szanowna Pani", Polish(chosen));

        // A neutral override stays neutral even when the default would be masculine.
        var masculine = Options with { UnknownGender = UnknownGenderMode.Masculine };
        var neutral = Person(null, null, "Jan", "Kowalski", masculine, chosen: Gender.Unknown);
        Assert.Equal("Szanowna Pani / Szanowny Panie", Polish(neutral, options: masculine));
    }

    [Fact]
    public void TemplateWithoutTitles_UsesThePlainForms()
    {
        var options = Options.With(new TemplateSalutationOptions { UseTitles = false });
        var person = Person("prof. dr hab.", null, "Jan", "Kowalski", options);

        Assert.Equal("Szanowny Panie", Polish(person, options: options));
        Assert.Equal("Dear Mr Kowalski", English(person, options: options));
        Assert.Equal(string.Empty, SalutationResolver.Title(person, highest: false, options).Text);
        Assert.Equal("Jan Kowalski", SalutationResolver.TitledName(person, options).Text);
    }

    [Theory]
    [InlineData(GrammaticalCase.Nominative, "Pan", "Pani")]
    [InlineData(GrammaticalCase.Genitive, "Pana", "Pani")]
    [InlineData(GrammaticalCase.Dative, "Panu", "Pani")]
    [InlineData(GrammaticalCase.Accusative, "Pana", "Panią")]
    [InlineData(GrammaticalCase.Instrumental, "Panem", "Panią")]
    [InlineData(GrammaticalCase.Locative, "Panu", "Pani")]
    [InlineData(GrammaticalCase.Vocative, "Panie", "Pani")]
    public void Honorific_AllSevenCases(GrammaticalCase grammaticalCase, string masculine, string feminine)
    {
        Assert.Equal(masculine, SalutationResolver.Honorific(Person(null, null, "Jan", "Kowalski"), MessageLanguage.Polish, grammaticalCase, Options).Text);
        Assert.Equal(feminine, SalutationResolver.Honorific(Person(null, null, "Anna", "Nowak"), MessageLanguage.Polish, grammaticalCase, Options).Text);
        Assert.Equal($"{masculine}/{feminine}", SalutationResolver.Honorific(Person(null, null, "Sasza", "Kim"), MessageLanguage.Polish, grammaticalCase, Options).Text);
        Assert.Equal("Mr", SalutationResolver.Honorific(Person(null, null, "Jan", "Kowalski"), MessageLanguage.English, grammaticalCase, Options).Text);
        Assert.Equal("Mr/Ms", SalutationResolver.Honorific(Person(null, null, "Sasza", "Kim"), MessageLanguage.English, grammaticalCase, Options).Text);
    }

    [Theory]
    [InlineData("dopełniacz", GrammaticalCase.Genitive)]
    [InlineData("DOPELNIACZ", GrammaticalCase.Genitive)]
    [InlineData("gen", GrammaticalCase.Genitive)]
    [InlineData("narzędnik", GrammaticalCase.Instrumental)]
    [InlineData("wołacz", GrammaticalCase.Vocative)]
    [InlineData("biernik", GrammaticalCase.Accusative)]
    [InlineData("loc", GrammaticalCase.Locative)]
    public void Cases_ByEnglishAndPolishNames(string argument, GrammaticalCase expected) =>
        Assert.Equal(expected, SalutationResolver.ParseCase(argument));

    [Fact]
    public void ByGender_TwoAndThreeParts()
    {
        Assert.Equal("zmienił", SalutationResolver.ByGender(Person(null, null, "Jan", "Kowalski"), "zmienił/zmieniła", Options).Text);
        Assert.Equal("zmieniła", SalutationResolver.ByGender(Person(null, null, "Anna", "Nowak"), "zmienił/zmieniła", Options).Text);
        Assert.Equal("zmienił/zmieniła", SalutationResolver.ByGender(Person(null, null, "Sasza", "Kim"), "zmienił/zmieniła", Options).Text);
        Assert.Equal("their", SalutationResolver.ByGender(Person(null, null, "Sasza", "Kim"), "his/her/their", Options).Text);
        Assert.Equal(string.Empty, SalutationResolver.ByGender(Person(null, null, "Jan", "Kowalski"), "/a", Options).Text);
    }

    [Fact]
    public void TitleAndTitledName()
    {
        var suffix = Person(null, "dr hab. Ewa Zielińska, prof. UW", "Ewa", "Zielińska");
        Assert.Equal("dr hab., prof. UW", SalutationResolver.Title(suffix, highest: false, Options).Text);
        Assert.Equal("prof. UW", SalutationResolver.Title(suffix, highest: true, Options).Text);
        Assert.Equal("dr hab. Ewa Zielińska, prof. UW", SalutationResolver.TitledName(suffix, Options).Text);

        var professor = Person("prof. dr hab.", null, "Jan", "Kowalski");
        Assert.Equal("prof. dr hab. Jan Kowalski", SalutationResolver.TitledName(professor, Options).Text);
        Assert.Equal("Jan Kowalski", SalutationResolver.TitledName(Person(null, null, "Jan", "Kowalski"), Options).Text);
    }
}
