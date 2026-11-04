using System.Text.Json.Serialization;

namespace AdMng.Core.Messaging.Salutations;

/// <summary>Grammatical gender of a recipient as far as a salutation is concerned (U1d).</summary>
public enum Gender
{
    Unknown,
    Masculine,
    Feminine,
}

/// <summary>What a salutation renders when the gender of the recipient is unknown.</summary>
public enum UnknownGenderMode
{
    /// <summary>The neutral text of the language ("Szanowna Pani / Szanowny Panie", "Dear {name}") and a warning.</summary>
    Neutral,

    Masculine,

    Feminine,
}

/// <summary>Where a derived value of a message (title, gender, salutation) comes from – shown in the preview.</summary>
public enum DerivedValueSource
{
    /// <summary>Nothing was found.</summary>
    None,

    /// <summary>The gender attribute of the settings.</summary>
    Attribute,

    /// <summary>The title attribute (default <c>personalTitle</c>): the title, or an honorific word that sets the gender.</summary>
    TitleAttribute,

    /// <summary>A title prefix or suffix of the display name ("prof. dr hab. Jan Kowalski").</summary>
    DisplayName,

    /// <summary>The operator's given-name list of the settings.</summary>
    OperatorList,

    /// <summary>A built-in exception list of given names (Kuba, Beatrycze …) – a guess.</summary>
    NameException,

    /// <summary>The Polish rule "ends in -a → feminine" – a guess.</summary>
    NameRule,

    /// <summary>The surname ending (-ska / -ski …) – a guess.</summary>
    Surname,

    /// <summary>The operator chose the gender for this run in the preview.</summary>
    Override,

    /// <summary>A fallback: the neutral text or the default gender of the settings / the template.</summary>
    Fallback,
}

/// <summary>A row of the title table: canonical spelling, variants, rank and the forms of address.</summary>
public sealed record TitleRule
{
    /// <summary>Canonical spelling shown in <c>{$title}</c> ("prof.", "dr hab.").</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Other spellings recognized as this title ("prof. zw.", "doktor").</summary>
    public IReadOnlyList<string> Variants { get; init; } = [];

    /// <summary>0–1000: the highest-ranked title of a person decides the salutation.</summary>
    public int Rank { get; init; }

    /// <summary>Polish masculine vocative ("Panie Profesorze"); null = no title-specific form.</summary>
    public string? PolishMasculine { get; init; }

    /// <summary>Polish feminine vocative ("Pani Profesor"); null = no title-specific form.</summary>
    public string? PolishFeminine { get; init; }

    /// <summary>English form before the surname ("Professor", "Dr"); null = Mr / Ms.</summary>
    public string? English { get; init; }

    /// <summary>The same content (lists compared by value).</summary>
    public bool SameAs(TitleRule? other) =>
        other is not null
        && string.Equals(Title, other.Title, StringComparison.Ordinal)
        && (Variants ?? []).SequenceEqual(other.Variants ?? [], StringComparer.Ordinal)
        && Rank == other.Rank
        && string.Equals(PolishMasculine, other.PolishMasculine, StringComparison.Ordinal)
        && string.Equals(PolishFeminine, other.PolishFeminine, StringComparison.Ordinal)
        && string.Equals(English, other.English, StringComparison.Ordinal);
}

/// <summary>A given name the operator assigned a gender to (settings, U1d).</summary>
public sealed record GivenNameGender(string Name, Gender Gender);

/// <summary>
/// Salutation options of a template (U1d): titles on or off, what an unknown gender renders as and the neutral texts.
/// Null values take the settings.
/// </summary>
public sealed record TemplateSalutationOptions
{
    /// <summary>Maximum length of a neutral text.</summary>
    public const int MaxNeutralLength = 200;

    /// <summary>Titles in salutations, <c>{$title}</c> and <c>{$titledName}</c>; false = plain "Szanowny Panie".</summary>
    public bool UseTitles { get; init; } = true;

    /// <summary>Unknown gender renders as; null = the settings.</summary>
    public UnknownGenderMode? UnknownGender { get; init; }

    /// <summary>Polish neutral text; null = the settings.</summary>
    public string? NeutralPolish { get; init; }

    /// <summary>English neutral text; null = the settings.</summary>
    public string? NeutralEnglish { get; init; }

    /// <summary>Nothing differs from the settings.</summary>
    [JsonIgnore]
    public bool IsDefault => UseTitles && UnknownGender is null && NeutralPolish is null && NeutralEnglish is null;

    /// <summary>Repaired values of a hand-edited or imported template; null when nothing differs from the settings.</summary>
    public TemplateSalutationOptions? Normalized()
    {
        var normalized = this with
        {
            UnknownGender = UnknownGender is { } mode && Enum.IsDefined(mode) ? mode : null,
            NeutralPolish = CleanNeutral(NeutralPolish),
            NeutralEnglish = CleanNeutral(NeutralEnglish),
        };
        return normalized.IsDefault ? null : normalized;
    }

    /// <summary>A neutral text without control characters, at most <see cref="MaxNeutralLength"/> characters; null when empty.</summary>
    public static string? CleanNeutral(string? text)
    {
        var clean = UserMessageRules.HeaderSafe(text);
        if (clean.Length == 0)
        {
            return null;
        }

        return clean.Length > MaxNeutralLength ? clean[..MaxNeutralLength].TrimEnd() : clean;
    }
}

/// <summary>
/// Everything the salutation fields (<c>{$salutation}</c>, <c>{$title}</c>, <c>{$honorific}</c> …) are computed with: the
/// settings (title and gender attributes, value lists, given-name list, title table, neutral texts) merged with the options
/// of the template (<see cref="With"/>). Vocabulary typed by the operator only – never directory data.
/// </summary>
public sealed record SalutationOptions
{
    /// <summary>The default title attribute.</summary>
    public const string DefaultTitleAttribute = "personalTitle";

    /// <summary>The built-in defaults.</summary>
    public static SalutationOptions Default { get; } = new();

    /// <summary>Attribute with the academic or other title; null or empty = none. Never <c>title</c> (the job title).</summary>
    public string? TitleAttribute { get; init; } = DefaultTitleAttribute;

    /// <summary>Recognize a title prefix or suffix of the display name when the title attribute has none.</summary>
    public bool TitleFromDisplayName { get; init; } = true;

    /// <summary>Attribute with the gender (e.g. <c>extensionAttribute5</c>); null = none.</summary>
    public string? GenderAttribute { get; init; }

    /// <summary>Values of the gender attribute meaning masculine (case- and diacritics-insensitive).</summary>
    public IReadOnlyList<string> MasculineValues { get; init; } = SalutationData.DefaultMasculineValues;

    /// <summary>Values of the gender attribute meaning feminine (case- and diacritics-insensitive).</summary>
    public IReadOnlyList<string> FeminineValues { get; init; } = SalutationData.DefaultFeminineValues;

    /// <summary>Given names the operator assigned a gender to (certain).</summary>
    public IReadOnlyList<GivenNameGender> GivenNames { get; init; } = [];

    public TitleTable Titles { get; init; } = TitleTable.Default;

    /// <summary>Polish neutral text; null = <see cref="SalutationData.DefaultNeutralPolish"/>.</summary>
    public string? NeutralPolish { get; init; }

    /// <summary>English neutral text; null = <see cref="SalutationData.DefaultNeutralEnglish"/>.</summary>
    public string? NeutralEnglish { get; init; }

    public UnknownGenderMode UnknownGender { get; init; } = UnknownGenderMode.Neutral;

    /// <summary>Titles are used (the template can switch them off).</summary>
    public bool UseTitles { get; init; } = true;

    /// <summary>These options with the template's (null: unchanged).</summary>
    public SalutationOptions With(TemplateSalutationOptions? template) => template is null
        ? this
        : this with
        {
            UseTitles = template.UseTitles,
            UnknownGender = template.UnknownGender ?? UnknownGender,
            NeutralPolish = template.NeutralPolish ?? NeutralPolish,
            NeutralEnglish = template.NeutralEnglish ?? NeutralEnglish,
        };

    /// <summary>The neutral text of <paramref name="language"/> (may contain <c>{name}</c>).</summary>
    public string Neutral(MessageLanguage language) => language == MessageLanguage.English
        ? NeutralEnglish ?? SalutationData.DefaultNeutralEnglish
        : NeutralPolish ?? SalutationData.DefaultNeutralPolish;

    /// <summary>The title attribute when it can be read (a valid name, not a secret or the job title), else null.</summary>
    public string? UsableTitleAttribute => Usable(TitleAttribute);

    /// <summary>The gender attribute when it can be read, else null.</summary>
    public string? UsableGenderAttribute => Usable(GenderAttribute);

    /// <summary>
    /// A valid LDAP attribute name (letter first, then letters, digits and hyphens) that is not forbidden in messages
    /// (<see cref="UserMessageRules.IsForbiddenAttribute"/>) and not <c>title</c> (the job title).
    /// </summary>
    public static bool IsUsableAttribute(string? attribute) =>
        attribute is { Length: > 0 and <= 128 }
        && char.IsAsciiLetter(attribute[0])
        && attribute.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
        && !string.Equals(attribute, "title", StringComparison.OrdinalIgnoreCase)
        && !UserMessageRules.IsForbiddenAttribute(attribute);

    private static string? Usable(string? attribute)
    {
        var trimmed = attribute?.Trim();
        return IsUsableAttribute(trimmed) ? trimmed : null;
    }
}
