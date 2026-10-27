namespace AdMng.Core.Messaging.Salutations;

/// <summary>Grammatical case of the Polish short form (<c>{$honorific:dopełniacz}</c> → "Pana" / "Pani").</summary>
public enum GrammaticalCase
{
    Nominative,
    Genitive,
    Dative,
    Accusative,
    Instrumental,
    Locative,
    Vocative,
}

/// <summary>
/// Language data of the salutations (U1d), addressed by the message language – not UI strings: the default title table
/// (both languages in every row), the default values of the gender attribute, the honorific words, the Polish case forms
/// of "Pan" / "Pani", the adjectives and the neutral texts. UI labels are in the Presentation resources.
/// </summary>
public static class SalutationData
{
    /// <summary>Polish neutral text (unknown gender).</summary>
    public const string DefaultNeutralPolish = "Szanowna Pani / Szanowny Panie";

    /// <summary>English neutral text (unknown gender); <c>{name}</c> = given name and surname.</summary>
    public const string DefaultNeutralEnglish = "Dear {name}";

    /// <summary>The name placeholder of the neutral texts.</summary>
    public const string NamePlaceholder = "{name}";

    /// <summary>Polish last resort when a neutral text needs a name and the account has none.</summary>
    public const string LastResortPolish = "Szanowni Państwo";

    /// <summary>English last resort when a neutral text needs a name and the account has none.</summary>
    public const string LastResortEnglish = "Dear Sir or Madam";

    public const string PolishMasculineAdjective = "Szanowny";
    public const string PolishFeminineAdjective = "Szanowna";
    public const string PolishMasculineVocative = "Panie";
    public const string PolishFeminineVocative = "Pani";
    public const string EnglishGreeting = "Dear";
    public const string EnglishMasculine = "Mr";
    public const string EnglishFeminine = "Ms";

    /// <summary>Default values of the gender attribute meaning masculine.</summary>
    public static IReadOnlyList<string> DefaultMasculineValues { get; } = ["M", "male", "mężczyzna", "Pan", "Mr"];

    /// <summary>Default values of the gender attribute meaning feminine.</summary>
    public static IReadOnlyList<string> DefaultFeminineValues { get; } = ["K", "F", "W", "female", "kobieta", "Pani", "Ms", "Mrs"];

    /// <summary>Honorific words of the title attribute that set the gender and are not titles (compared as keys).</summary>
    public static IReadOnlyList<string> MasculineHonorifics { get; } = ["Pan", "Mr", "Mr."];

    /// <inheritdoc cref="MasculineHonorifics"/>
    public static IReadOnlyList<string> FeminineHonorifics { get; } = ["Pani", "Mrs", "Mrs.", "Ms", "Ms.", "Miss"];

    /// <summary>
    /// The default title table. A <i>doktor habilitowany</i> is customarily addressed "Panie Profesorze / Pani Profesor" in
    /// Polish correspondence; <i>magister / inżynier / licencjat</i> are shown in <c>{$title}</c> but give the plain
    /// "Szanowny Panie / Szanowna Pani". Variants are other spellings of the same title (shown in the canonical one);
    /// a different title with the same forms has its own row – <i>profesor uczelni / nadzwyczajny</i> is a position, not
    /// the title of professor, <i>lekarz dentysta / weterynarii</i> is no physician – ranked just above the row its
    /// spelling contains, so <c>{$title:highest}</c> keeps it.
    /// </summary>
    public static IReadOnlyList<TitleRule> DefaultTitles { get; } =
    [
        Row("ks. prof.", [], 120, "Księże Profesorze", null, "Father"),
        Row("ks. dr hab.", [], 118, "Księże Profesorze", null, "Father"),
        Row("ks. dr", [], 115, "Księże Doktorze", null, "Father"),
        Row("prof.", ["prof", "profesor", "professor", "prof. zw."], 100, "Panie Profesorze", "Pani Profesor", "Professor"),
        Row("prof. uczelni", ["prof. ucz."], 101, "Panie Profesorze", "Pani Profesor", "Professor"),
        Row("prof. nadzw.", ["prof. nzw.", "prof. ndzw."], 101, "Panie Profesorze", "Pani Profesor", "Professor"),
        Row("dr hab.", ["dr. hab.", "doktor habilitowany"], 90, "Panie Profesorze", "Pani Profesor", "Dr"),
        Row("ks.", ["ks", "ksiądz"], 80, "Księże", null, "Father"),
        Row("dr", ["dr.", "doktor", "doctor"], 70, "Panie Doktorze", "Pani Doktor", "Dr"),
        Row("lek.", ["lek. med.", "lekarz"], 60, "Panie Doktorze", "Pani Doktor", "Dr"),
        Row("lek. dent.", ["lek. stom."], 61, "Panie Doktorze", "Pani Doktor", "Dr"),
        Row("lek. wet.", [], 61, "Panie Doktorze", "Pani Doktor", "Dr"),
        Row("mgr", ["mgr.", "magister"], 20, null, null, null),
        Row("inż.", ["inż", "inz.", "inz", "inżynier"], 20, null, null, null),
        Row("lic.", ["lic", "licencjat"], 10, null, null, null),
    ];

    /// <summary>"Pan" in the seven cases (nominative … vocative).</summary>
    public static IReadOnlyList<string> PolishMasculineCases { get; } = ["Pan", "Pana", "Panu", "Pana", "Panem", "Panu", "Panie"];

    /// <summary>"Pani" in the seven cases (nominative … vocative).</summary>
    public static IReadOnlyList<string> PolishFeminineCases { get; } = ["Pani", "Pani", "Pani", "Panią", "Panią", "Pani", "Pani"];

    /// <summary>
    /// The arguments of <c>{$honorific:…}</c>: English abbreviations and Polish names of the cases (compared case- and
    /// diacritics-insensitively).
    /// </summary>
    public static IReadOnlyList<(string Name, GrammaticalCase Case)> CaseNames { get; } =
    [
        ("nom", GrammaticalCase.Nominative), ("gen", GrammaticalCase.Genitive), ("dat", GrammaticalCase.Dative),
        ("acc", GrammaticalCase.Accusative), ("ins", GrammaticalCase.Instrumental), ("loc", GrammaticalCase.Locative),
        ("voc", GrammaticalCase.Vocative),
        ("mianownik", GrammaticalCase.Nominative), ("dopełniacz", GrammaticalCase.Genitive), ("celownik", GrammaticalCase.Dative),
        ("biernik", GrammaticalCase.Accusative), ("narzędnik", GrammaticalCase.Instrumental), ("miejscownik", GrammaticalCase.Locative),
        ("wołacz", GrammaticalCase.Vocative),
    ];

    /// <summary>Words removed from a neutral text to get its vocative form ("Szanowna Pani / Szanowny Panie" → "Pani / Panie").</summary>
    public static IReadOnlyList<string> Adjectives { get; } = ["Szanowna", "Szanowny", "Szanowni", "Szanowne", "Dear"];

    private static TitleRule Row(string title, IReadOnlyList<string> variants, int rank, string? masculine, string? feminine, string? english) => new()
    {
        Title = title,
        Variants = variants,
        Rank = rank,
        PolishMasculine = masculine,
        PolishFeminine = feminine,
        English = english,
    };
}
