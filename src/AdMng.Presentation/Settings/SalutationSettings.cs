using System.Text.Json.Serialization;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Salutations;

namespace AdMng.Presentation.Settings;

/// <summary>
/// Salutations of messages to users (U1d, settings tab "Wiadomości e-mail" → "Zwroty grzecznościowe"): the title and
/// gender attributes, the values of the gender attribute, the operator's given-name list, the title table, the neutral
/// texts and what an unknown gender renders as. Vocabulary typed by the operator only – nothing read from the directory
/// is ever copied here.
/// </summary>
public sealed class SalutationSettings
{
    public const int MaxGivenNames = 500;
    public const int MaxValues = 50;
    public const int MaxVariants = 20;

    /// <summary>The title attribute (default <c>personalTitle</c>); empty = none.</summary>
    public string? TitleAttribute { get; set; } = SalutationOptions.DefaultTitleAttribute;

    /// <summary>Recognize a title in the display name when the title attribute has none.</summary>
    public bool TitleFromDisplayName { get; set; } = true;

    /// <summary>The gender attribute (e.g. <c>extensionAttribute5</c>); null = none.</summary>
    public string? GenderAttribute { get; set; }

    public List<string> MasculineValues { get; set; } = [.. SalutationData.DefaultMasculineValues];

    public List<string> FeminineValues { get; set; } = [.. SalutationData.DefaultFeminineValues];

    /// <summary>Given names the operator assigned a gender to.</summary>
    public List<GivenNameGender> GivenNames { get; set; } = [];

    /// <summary>The title table; null = the built-in defaults (so later improvements reach operators who never edited it).</summary>
    public List<TitleRule>? Titles { get; set; }

    /// <summary>Polish neutral text; null = "Szanowna Pani / Szanowny Panie".</summary>
    public string? NeutralPolish { get; set; }

    /// <summary>English neutral text; null = "Dear {name}".</summary>
    public string? NeutralEnglish { get; set; }

    public UnknownGenderMode UnknownGender { get; set; } = UnknownGenderMode.Neutral;

    /// <summary>The rows in effect (the defaults when <see cref="Titles"/> is null).</summary>
    [JsonIgnore]
    public IReadOnlyList<TitleRule> EffectiveTitles => Titles ?? SalutationData.DefaultTitles;

    /// <summary>Repairs values of a hand-edited or older settings file.</summary>
    public void Normalize()
    {
        TitleAttribute = Attribute(TitleAttribute) ?? string.Empty;
        GenderAttribute = Attribute(GenderAttribute);
        MasculineValues = Values(MasculineValues);
        FeminineValues = Values(FeminineValues);
        GivenNames = [.. (GivenNames ?? [])
            .Where(n => n is not null && n.Gender is Gender.Masculine or Gender.Feminine)
            .Select(n => n with { Name = Text(n.Name) ?? string.Empty })
            .Where(n => n.Name.Any(char.IsLetter))
            .DistinctBy(n => SalutationKey.Of(n.Name), StringComparer.Ordinal)
            .Take(MaxGivenNames)];
        Titles = NormalizeTitles(Titles);
        NeutralPolish = TemplateSalutationOptions.CleanNeutral(NeutralPolish);
        NeutralEnglish = TemplateSalutationOptions.CleanNeutral(NeutralEnglish);
        UnknownGender = Enum.IsDefined(UnknownGender) ? UnknownGender : UnknownGenderMode.Neutral;
    }

    /// <summary>The Core options of the planner (the template's options are merged when a message is planned).</summary>
    public SalutationOptions ToOptions() => new()
    {
        TitleAttribute = string.IsNullOrWhiteSpace(TitleAttribute) ? null : TitleAttribute.Trim(),
        TitleFromDisplayName = TitleFromDisplayName,
        GenderAttribute = string.IsNullOrWhiteSpace(GenderAttribute) ? null : GenderAttribute.Trim(),
        MasculineValues = MasculineValues ?? [],
        FeminineValues = FeminineValues ?? [],
        GivenNames = GivenNames ?? [],
        Titles = TitleTable.From(Titles),
        NeutralPolish = NeutralPolish,
        NeutralEnglish = NeutralEnglish,
        UnknownGender = UnknownGender,
    };

    /// <summary>
    /// Repaired title rows: trimmed, header-safe, at most <see cref="TitleTable.MaxRows"/> rows of spellings and forms of at
    /// most <see cref="TitleTable.MaxTextLength"/> characters, ranks clamped; null for null or rows equal to the defaults.
    /// </summary>
    public static List<TitleRule>? NormalizeTitles(IEnumerable<TitleRule>? rows)
    {
        if (rows is null)
        {
            return null;
        }

        var normalized = rows
            .Where(r => r is not null)
            .Select(r => r with
            {
                Title = Text(r.Title) ?? string.Empty,
                Variants = [.. (r.Variants ?? []).Select(Text).OfType<string>().Distinct(StringComparer.Ordinal).Take(MaxVariants)],
                Rank = Math.Clamp(r.Rank, 0, TitleTable.MaxRank),
                PolishMasculine = Text(r.PolishMasculine),
                PolishFeminine = Text(r.PolishFeminine),
                English = Text(r.English),
            })
            .Where(r => r.Title.Length > 0)
            .Select(r => r with { Variants = [.. r.Variants.Where(v => !string.Equals(v, r.Title, StringComparison.Ordinal))] })
            .DistinctBy(r => SalutationKey.Of(r.Title), StringComparer.Ordinal)
            .Take(TitleTable.MaxRows)
            .ToList();
        return IsDefault(normalized) ? null : normalized;
    }

    /// <summary>The rows are the built-in defaults.</summary>
    public static bool IsDefault(IReadOnlyList<TitleRule> rows) =>
        rows.Count == SalutationData.DefaultTitles.Count && rows.Zip(SalutationData.DefaultTitles).All(p => p.First.SameAs(p.Second));

    /// <summary>Trimmed, header-safe text of at most <see cref="TitleTable.MaxTextLength"/> characters; null when empty.</summary>
    public static string? Text(string? value)
    {
        var clean = UserMessageRules.HeaderSafe(value);
        if (clean.Length > TitleTable.MaxTextLength)
        {
            clean = clean[..TitleTable.MaxTextLength].TrimEnd();
        }

        return clean.Length == 0 ? null : clean;
    }

    private static string? Attribute(string? value)
    {
        var trimmed = value?.Trim();
        return SalutationOptions.IsUsableAttribute(trimmed) ? trimmed : null;
    }

    private static List<string> Values(IEnumerable<string>? values) =>
        [.. (values ?? []).Select(Text).OfType<string>().DistinctBy(SalutationKey.Of, StringComparer.Ordinal).Take(MaxValues)];
}
