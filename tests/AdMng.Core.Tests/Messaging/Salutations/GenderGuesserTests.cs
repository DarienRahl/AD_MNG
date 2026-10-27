using AdMng.Core.Messaging.Salutations;

namespace AdMng.Core.Tests.Messaging.Salutations;

/// <summary>The gender of the salutation (U1d, design note section 16).</summary>
public sealed class GenderGuesserTests
{
    private static readonly SalutationOptions Options = SalutationOptions.Default;

    private static GenderInfo Guess(string? givenName, string? surname, SalutationOptions? options = null, string? value = null, Gender honorific = Gender.Unknown, Gender? chosen = null) =>
        GenderGuesser.Guess(chosen, value, honorific, givenName, surname, options ?? Options);

    [Theory]
    [InlineData("Anna", "Nowak", Gender.Feminine, DerivedValueSource.NameRule)]
    [InlineData("Jan", "Nowak", Gender.Masculine, DerivedValueSource.NameRule)]
    [InlineData("Łucja", "Wiśniewska", Gender.Feminine, DerivedValueSource.NameRule)]
    [InlineData("ŁUCJA", "ŻÓŁĆ", Gender.Feminine, DerivedValueSource.NameRule)]
    [InlineData("Kuba", "Mazur", Gender.Masculine, DerivedValueSource.NameException)]
    [InlineData("Barnaba", "Nowak", Gender.Masculine, DerivedValueSource.NameException)]
    [InlineData("Bonawentura", "Kot", Gender.Masculine, DerivedValueSource.NameException)]
    [InlineData("Mykoła", "Szewczenko", Gender.Masculine, DerivedValueSource.NameException)]
    [InlineData("Beatrycze", "Nowak", Gender.Feminine, DerivedValueSource.NameException)]
    [InlineData("Noemi", "Nowak", Gender.Feminine, DerivedValueSource.NameException)]
    [InlineData("Emily", "Brown", Gender.Feminine, DerivedValueSource.NameException)]
    [InlineData("Jan Maria", "Rokita", Gender.Masculine, DerivedValueSource.NameRule)]
    [InlineData("Anna-Maria", "Wesołowska", Gender.Feminine, DerivedValueSource.NameRule)]
    public void GivenName_DecidesByRuleOrException(string givenName, string surname, Gender gender, DerivedValueSource source)
    {
        var info = Guess(givenName, surname);

        Assert.Equal(gender, info.Gender);
        Assert.Equal(source, info.Source);
        Assert.True(info.IsGuess);
        Assert.False(info.IsConflict);
        Assert.Equal(GenderGuesser.FirstGivenName(givenName), info.Detail);
    }

    [Theory]
    [InlineData("Jennifer", "Smith", Gender.Feminine)]
    [InlineData("Karen", "Miller", Gender.Feminine)]
    [InlineData("Sabine", "Müller", Gender.Feminine)]
    [InlineData("Yvonne", "Schmidt", Gender.Feminine)]
    [InlineData("Christine", "Dupont", Gender.Feminine)]
    [InlineData("Kerstin", "Lindqvist", Gender.Feminine)]
    [InlineData("Ilya", "Petrov", Gender.Masculine)]
    [InlineData("Misha", "Kowalczyk", Gender.Masculine)]
    [InlineData("Kostya", "Iwanow", Gender.Masculine)]
    public void CommonNonPolishNames_AreExceptionsToTheRule(string givenName, string surname, Gender gender)
    {
        var info = Guess(givenName, surname);

        Assert.Equal((gender, DerivedValueSource.NameException), (info.Gender, info.Source));
        Assert.Equal(gender, GenderGuesser.Guess(null, null, Gender.Unknown, givenName, surname, Options, polishNameRule: false).Gender);
    }

    [Theory]
    [InlineData("Aditya", "Sharma", Gender.Masculine)]
    [InlineData("Krishna", "Rao", Gender.Masculine)]
    [InlineData("Chaitanya", "Reddy", Gender.Masculine)]
    [InlineData("Surya", "Iyer", Gender.Masculine)]
    [InlineData("Attila", "Nagy", Gender.Masculine)]
    [InlineData("Savva", "Morozow", Gender.Masculine)]
    [InlineData("Ravindra", "Patil", Gender.Masculine)]
    [InlineData("Swati", "Gupta", Gender.Feminine)]
    [InlineData("Jyoti", "Singh", Gender.Feminine)]
    [InlineData("Preeti", "Joshi", Gender.Feminine)]
    [InlineData("Shruti", "Nair", Gender.Feminine)]
    [InlineData("Lakshmi", "Menon", Gender.Feminine)]
    [InlineData("Ayşe", "Yılmaz", Gender.Feminine)]
    public void ForeignStaffNames_AreExceptionsToTheRule(string givenName, string surname, Gender gender)
    {
        // A Polish message to a colleague without preferredLanguage: the "-a" rule would say the opposite.
        var info = Guess(givenName, surname);

        Assert.Equal((gender, DerivedValueSource.NameException), (info.Gender, info.Source));
    }

    [Theory]
    [InlineData("Katherine")]
    [InlineData("Kate")]
    [InlineData("Jane")]
    [InlineData("Grace")]
    [InlineData("Rose")]
    [InlineData("Stephanie")]
    [InlineData("Diane")]
    [InlineData("Leah")]
    [InlineData("Amélie")]
    [InlineData("Danielle")]
    [InlineData("Gabrielle")]
    [InlineData("Ljubow")]
    public void FeminineNamesNotEndingInA_AreNoPan(string givenName)
    {
        // A Polish message: the "-a" rule would say "Szanowny Panie".
        var info = Guess(givenName, "Nowak");

        Assert.Equal((Gender.Feminine, DerivedValueSource.NameException), (info.Gender, info.Source));
    }

    [Theory]
    [InlineData("Nowak", Gender.Unknown)]
    [InlineData("Kowalski", Gender.Masculine)]
    public void Sasa_IsUnisex(string surname, Gender gender)
    {
        // Saša (Serbian, Croatian) is mostly a man's name: no "Szanowna Pani" by the "-a" rule.
        var info = Guess("Saša", surname);

        Assert.Equal(gender, info.Gender);
        Assert.NotEqual(DerivedValueSource.NameRule, info.Source);
    }

    [Fact]
    public void EnglishMessages_DoNotApplyThePolishRule()
    {
        // Outside the lists the rule gives no signal: unknown (neutral "Dear John Smith" and a warning) …
        var john = GenderGuesser.Guess(null, null, Gender.Unknown, "John", "Smith", Options, polishNameRule: false);
        Assert.Equal((Gender.Unknown, DerivedValueSource.None), (john.Gender, john.Source));
        Assert.Equal(Gender.Unknown, GenderGuesser.Guess(null, null, Gender.Unknown, "Brenda", "Lee", Options, polishNameRule: false).Gender);

        // … the lists, the surname, the attribute and the operator's list still decide.
        Assert.Equal(DerivedValueSource.Surname, GenderGuesser.Guess(null, null, Gender.Unknown, "John", "Kowalski", Options, polishNameRule: false).Source);
        Assert.Equal(Gender.Feminine, GenderGuesser.Guess(null, "F", Gender.Unknown, "John", "Smith", Options, polishNameRule: false).Gender);
        var listed = Options with { GivenNames = [new GivenNameGender("John", Gender.Masculine)] };
        Assert.Equal(DerivedValueSource.OperatorList, GenderGuesser.Guess(null, null, Gender.Unknown, "John", "Smith", listed, polishNameRule: false).Source);

        // An agreeing rule leaves the decision to the surname.
        var agreeing = GenderGuesser.Guess(null, null, Gender.Unknown, "Brenda", "Kowalska", Options, polishNameRule: false);
        Assert.Equal((Gender.Feminine, DerivedValueSource.Surname), (agreeing.Gender, agreeing.Source));
    }

    [Theory]
    [InlineData("Jan", "Nowak", Gender.Masculine)]
    [InlineData("Anna", "Nowak", Gender.Feminine)]
    [InlineData("Krzysztof", "Smith", Gender.Masculine)]
    [InlineData("Paweł", "Brown", Gender.Masculine)]
    [InlineData("Małgorzata", "Brown", Gender.Feminine)]
    [InlineData("Anna Maria", "Kowalska", Gender.Feminine)]
    public void EnglishMessages_ApplyThePolishRule_ToPolishNames(string givenName, string surname, Gender gender)
    {
        // A Polish employee with preferredLanguage=en: "Dear Mr Nowak" (a guess), as "Szanowny Panie" in Polish.
        var info = GenderGuesser.Guess(null, null, Gender.Unknown, givenName, surname, Options, polishNameRule: false);

        Assert.Equal((gender, DerivedValueSource.NameRule, true), (info.Gender, info.Source, info.IsGuess));
    }

    [Theory]
    [InlineData("Jan", true)]
    [InlineData("JAN", true)]
    [InlineData("Łucja", true)]
    [InlineData("Zdzisław", true)]
    [InlineData("Grzegorz", true)]
    [InlineData("John", false)]
    [InlineData("Brenda", false)]
    [InlineData("Marian", false)]
    [InlineData("Ramón", false)]
    public void LooksPolish(string givenName, bool expected) => Assert.Equal(expected, GenderGuesser.LooksPolish(givenName));

    [Theory]
    [InlineData("Laura", "Nowakowski")]
    [InlineData("Maria", "Kowalski")]
    [InlineData("Paweł", "Gąska")]
    [InlineData("Andrzej", "Laska")]
    [InlineData("Jan", "Kowalska")]
    public void EnglishMessages_TheRuleStillVetoesADisagreeingSurname(string givenName, string surname)
    {
        // The rule gives no signal of its own there, but a -ski / -ska surname never decides against it ("Dear Mr
        // Nowakowski" to Laura): the conflict renders the neutral form with a warning, as in Polish messages.
        var info = GenderGuesser.Guess(null, null, Gender.Unknown, givenName, surname, Options, polishNameRule: false);

        Assert.Equal((Gender.Unknown, true), (info.Gender, info.IsConflict));
        Assert.Equal($"{givenName} {surname}", info.Detail);
    }

    [Fact]
    public void Kuba_IsAnExceptionAndWinsOverAFeminineSurname()
    {
        var info = Guess("Kuba", "Kowalska");

        Assert.Equal((Gender.Masculine, DerivedValueSource.NameException), (info.Gender, info.Source));
        Assert.False(info.IsConflict);
    }

    [Theory]
    [InlineData("Sasza", "Kamińska", Gender.Feminine)]
    [InlineData("Nikola", "Lewandowski", Gender.Masculine)]
    [InlineData("Sasza", "Zawadzka", Gender.Feminine)]
    [InlineData("Kim", "Wysocki", Gender.Masculine)]
    [InlineData("J.", "Kowalska-Nowak", Gender.Feminine)]
    [InlineData(null, "Kowalski", Gender.Masculine)]
    public void UnisexNamesAndInitials_LetTheSurnameDecide(string? givenName, string surname, Gender gender)
    {
        var info = Guess(givenName, surname);

        Assert.Equal(gender, info.Gender);
        Assert.Equal(DerivedValueSource.Surname, info.Source);
        Assert.True(info.IsGuess);
    }

    [Theory]
    [InlineData("Sasza", "Kim")]
    [InlineData("Alex", "Nowak")]
    [InlineData(null, null)]
    [InlineData("J.", "Kim")]
    public void NoSignal_IsUnknown(string? givenName, string? surname)
    {
        var info = Guess(givenName, surname);

        Assert.Equal(Gender.Unknown, info.Gender);
        Assert.Equal(DerivedValueSource.None, info.Source);
        Assert.False(info.IsConflict);
    }

    [Theory]
    [InlineData("Jan", "Kowalska")]
    [InlineData("Anna", "Kowalski")]
    public void RuleAgainstSurname_IsAConflict(string givenName, string surname)
    {
        var info = Guess(givenName, surname);

        Assert.Equal(Gender.Unknown, info.Gender);
        Assert.True(info.IsConflict);
        Assert.Equal($"{givenName} {surname}", info.Detail);
    }

    [Fact]
    public void AgreeingSurname_KeepsTheNameResult()
    {
        var info = Guess("Anna", "Kowalska");

        Assert.Equal((Gender.Feminine, DerivedValueSource.NameRule), (info.Gender, info.Source));
    }

    [Theory]
    [InlineData("K", Gender.Feminine)]
    [InlineData("kobieta", Gender.Feminine)]
    [InlineData("MĘŻCZYZNA", Gender.Masculine)]
    [InlineData("mezczyzna", Gender.Masculine)]
    [InlineData("Mr.", Gender.Masculine)]
    [InlineData("female", Gender.Feminine)]
    public void GenderAttribute_IsCertain(string value, Gender gender)
    {
        // The attribute wins over a conflicting name.
        var info = Guess("Anna", "Kowalski", value: value);

        Assert.Equal((gender, DerivedValueSource.Attribute), (info.Gender, info.Source));
        Assert.False(info.IsGuess);
    }

    [Fact]
    public void GenderAttribute_UnknownValue_IsIgnored_AndReported()
    {
        var info = Guess("Anna", "Nowak", value: "X");

        Assert.Equal((Gender.Feminine, DerivedValueSource.NameRule), (info.Gender, info.Source));
        Assert.Equal("X", info.UnknownValue);
    }

    [Fact]
    public void HonorificWordOfTheTitleAttribute_WinsOverTheSurname()
    {
        var info = Guess("Nikola", "Lewandowski", honorific: Gender.Feminine);

        Assert.Equal((Gender.Feminine, DerivedValueSource.TitleAttribute), (info.Gender, info.Source));
        Assert.False(info.IsGuess);
    }

    [Fact]
    public void OperatorList_IsCertainForThatName()
    {
        var options = Options with { GivenNames = [new GivenNameGender("Sasza", Gender.Masculine), new GivenNameGender("Józefa", Gender.Masculine)] };

        var sasza = Guess("Sasza", "Kim", options);
        Assert.Equal((Gender.Masculine, DerivedValueSource.OperatorList, false), (sasza.Gender, sasza.Source, sasza.IsGuess));
        Assert.Equal(Gender.Masculine, Guess("JOZEFA", "Nowak", options).Gender);
        Assert.Equal(Gender.Feminine, Guess("Anna", "Nowak", options).Gender);
    }

    [Fact]
    public void Override_WinsOverEverything_UnknownMeansNeutralOnPurpose()
    {
        var masculine = Guess("Anna", "Kowalska", value: "K", chosen: Gender.Masculine);
        Assert.Equal((Gender.Masculine, DerivedValueSource.Override), (masculine.Gender, masculine.Source));

        var neutral = Guess("Anna", "Kowalska", chosen: Gender.Unknown);
        Assert.Equal((Gender.Unknown, DerivedValueSource.Override), (neutral.Gender, neutral.Source));
    }

    [Theory]
    [InlineData("Jan Maria", "Jan")]
    [InlineData("Anna-Maria", "Anna")]
    [InlineData("  Łucja ", "Łucja")]
    [InlineData("J.", null)]
    [InlineData("J", null)]
    [InlineData("", null)]
    public void FirstGivenName(string givenName, string? first) => Assert.Equal(first, GenderGuesser.FirstGivenName(givenName));

    [Theory]
    [InlineData("Kamińska", Gender.Feminine)]
    [InlineData("Wysocka", Gender.Feminine)]
    [InlineData("Zawadzka", Gender.Feminine)]
    [InlineData("Kamiński", Gender.Masculine)]
    [InlineData("Wysocki", Gender.Masculine)]
    [InlineData("Zawadzki", Gender.Masculine)]
    [InlineData("Nowak", Gender.Unknown)]
    [InlineData("", Gender.Unknown)]
    [InlineData("Nowak-Kowalska", Gender.Feminine)]
    [InlineData("Kowalska-Nowak", Gender.Feminine)]
    [InlineData("Nowak-Kowalski", Gender.Masculine)]
    [InlineData("Wiśniewska-Zawadzka", Gender.Feminine)]
    [InlineData("Kowalska-Wiśniewski", Gender.Unknown)]
    public void Surnames(string surname, Gender gender) => Assert.Equal(gender, GenderGuesser.FromSurname(surname));

    [Theory]
    [InlineData("Ewa", "Nowak-Kowalski")]
    [InlineData("Jan", "Nowak-Kowalska")]
    public void DoubleBarrelledSurname_EveryPartCounts_ForAConflict(string givenName, string surname)
    {
        // The adjectival part of a married name is often the second one.
        var info = Guess(givenName, surname);

        Assert.Equal((Gender.Unknown, true), (info.Gender, info.IsConflict));
    }

    [Fact]
    public void DoubleBarrelledSurname_SecondPart_DecidesWithoutANameSignal()
    {
        var info = Guess("Sasza", "Nowak-Kowalska");

        Assert.Equal((Gender.Feminine, DerivedValueSource.Surname), (info.Gender, info.Source));
    }

    [Fact]
    public void BuiltInLists_HaveTheMinimumContent()
    {
        Assert.Contains("kuba", GivenNameLists.MasculineEndingInA);
        Assert.Contains("mykola", GivenNameLists.MasculineEndingInA);
        Assert.Contains("beatrycze", GivenNameLists.FeminineNotEndingInA);
        Assert.Contains("sasza", GivenNameLists.Unisex);
        Assert.True(GivenNameLists.MasculineEndingInA.Count >= 40);
        Assert.True(GivenNameLists.FeminineNotEndingInA.Count >= 60);
        Assert.Empty(GivenNameLists.Unisex.Intersect(GivenNameLists.MasculineEndingInA));
        Assert.Empty(GivenNameLists.Unisex.Intersect(GivenNameLists.FeminineNotEndingInA));
        Assert.Empty(GivenNameLists.Polish.Intersect(GivenNameLists.Unisex));
        Assert.True(GivenNameLists.Polish.Count >= 200);
    }
}
