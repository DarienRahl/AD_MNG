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

    [Fact]
    public void EnglishMessages_DoNotApplyThePolishRule()
    {
        // Outside the lists the rule gives no signal: unknown (neutral "Dear John Smith" and a warning) …
        var john = GenderGuesser.Guess(null, null, Gender.Unknown, "John", "Smith", Options, polishNameRule: false);
        Assert.Equal((Gender.Unknown, DerivedValueSource.None), (john.Gender, john.Source));
        Assert.Equal(Gender.Unknown, GenderGuesser.Guess(null, null, Gender.Unknown, "Brenda", "Lee", Options, polishNameRule: false).Gender);

        // … the lists, the surname, the attribute and the operator's list still decide.
        Assert.Equal(DerivedValueSource.Surname, GenderGuesser.Guess(null, null, Gender.Unknown, "Jan", "Kowalski", Options, polishNameRule: false).Source);
        Assert.Equal(Gender.Feminine, GenderGuesser.Guess(null, "F", Gender.Unknown, "John", "Smith", Options, polishNameRule: false).Gender);
        var listed = Options with { GivenNames = [new GivenNameGender("John", Gender.Masculine)] };
        Assert.Equal(DerivedValueSource.OperatorList, GenderGuesser.Guess(null, null, Gender.Unknown, "John", "Smith", listed, polishNameRule: false).Source);

        // No conflict between a rule that does not apply and the surname.
        Assert.Equal((Gender.Feminine, false), (GenderGuesser.Guess(null, null, Gender.Unknown, "Jan", "Kowalska", Options, polishNameRule: false) is var j ? (j.Gender, j.IsConflict) : default));
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
    public void Surnames(string surname, Gender gender) => Assert.Equal(gender, GenderGuesser.FromSurname(surname));

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
    }
}
