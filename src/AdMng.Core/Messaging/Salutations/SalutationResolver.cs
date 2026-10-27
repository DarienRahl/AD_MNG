using AdMng.Core.ActiveDirectory;
using AdMng.Core.Model;
using AdMng.Core.Schema;

namespace AdMng.Core.Messaging.Salutations;

/// <summary>Variant of <c>{$salutation}</c>.</summary>
public enum SalutationVariant
{
    /// <summary>"Szanowny Panie Profesorze" / "Dear Professor Kowalski".</summary>
    Full,

    /// <summary>"Szanowny Panie" / "Dear Mr Kowalski" – the title is ignored.</summary>
    NoTitle,

    /// <summary>"Panie Profesorze" / "Professor Kowalski" – without the adjective / "Dear".</summary>
    Vocative,

    /// <summary>"Panie" / "Mr Kowalski".</summary>
    VocativeNoTitle,
}

/// <summary>A person as the salutation fields see them: names, the recognized title and the gender.</summary>
public sealed record SalutationPerson
{
    public string? GivenName { get; init; }

    public string? Surname { get; init; }

    public string? DisplayName { get; init; }

    public TitleInfo Title { get; init; } = TitleInfo.None;

    public GenderInfo Gender { get; init; } = GenderInfo.Unknown;

    /// <summary>
    /// The display name without a title prefix or suffix when the person has neither given name nor surname ("dr Jan
    /// Kowalski" → "Jan Kowalski"), else null.
    /// </summary>
    public string? DisplayNameWithoutTitle { get; init; }

    /// <summary>"Jan Kowalski" (given name and surname), else the display name without its title, else empty.</summary>
    public string Name
    {
        get
        {
            var name = $"{GivenName?.Trim()} {Surname?.Trim()}".Trim();
            if (name.Length > 0)
            {
                return name;
            }

            return (Title.NameWithoutTitle ?? DisplayNameWithoutTitle ?? DisplayName)?.Trim() ?? string.Empty;
        }
    }

    /// <summary>The person of a directory object (title and gender attributes of <paramref name="options"/>).</summary>
    /// <param name="person">The account or the manager.</param>
    /// <param name="options">Settings merged with the template's options.</param>
    /// <param name="overrideGender">Chosen in the preview for this run, or null.</param>
    /// <param name="schema">Schema of the session: confidential attributes are never read as title or gender.</param>
    /// <param name="language">Language of the message (the Polish name rule applies to Polish messages only).</param>
    public static SalutationPerson From(
        DirectoryObject person,
        SalutationOptions options,
        Gender? overrideGender = null,
        ISchemaCatalog? schema = null,
        MessageLanguage language = MessageLanguage.Polish)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(options);
        string? Read(string? attribute) =>
            attribute is not null && !UserMessageRules.IsForbiddenAttribute(attribute, schema) ? person.GetString(attribute) : null;
        return Of(
            Read(options.UsableTitleAttribute),
            person.GetString(Attr.DisplayName),
            person.GetString(Attr.GivenName),
            person.GetString(Attr.Sn),
            Read(options.UsableGenderAttribute),
            options,
            overrideGender,
            language);
    }

    /// <summary>A person of typed values (the settings' check box and tests) for a message in <paramref name="language"/>.</summary>
    public static SalutationPerson Of(
        string? titleValue,
        string? displayName,
        string? givenName,
        string? surname,
        string? genderValue,
        SalutationOptions options,
        Gender? overrideGender = null,
        MessageLanguage language = MessageLanguage.Polish)
    {
        ArgumentNullException.ThrowIfNull(options);
        var title = TitleParser.Parse(titleValue, displayName, givenName, surname, options.Titles, options.TitleFromDisplayName);
        return new SalutationPerson
        {
            GivenName = givenName,
            Surname = surname,
            DisplayName = displayName,
            DisplayNameWithoutTitle = options.TitleFromDisplayName && string.IsNullOrWhiteSpace(givenName) && string.IsNullOrWhiteSpace(surname)
                ? TitleParser.WithoutTitles(displayName, options.Titles)
                : null,
            Title = title,
            Gender = GenderGuesser.WithTitle(
                GenderGuesser.Guess(overrideGender, genderValue, title.HonorificGender, givenName, surname, options, language == MessageLanguage.Polish),
                title),
        };
    }
}

/// <summary>A rendered salutation field with what it needed (the caller raises the matching findings).</summary>
public sealed record SalutationResult(string Text)
{
    /// <summary>The gender decided the text (or its absence made it neutral).</summary>
    public bool UsedGender { get; init; }

    /// <summary>The title was looked at (findings about the title apply).</summary>
    public bool ConsultedTitle { get; init; }

    /// <summary>A title-specific form was used ("Panie Profesorze", "Professor").</summary>
    public bool UsedTitle { get; init; }

    /// <summary>The neutral text (or the joined forms "Pan/Pani") was used.</summary>
    public bool IsNeutral { get; init; }

    /// <summary>The English salutation needed the surname and there is none.</summary>
    public bool SurnameMissing { get; init; }
}

/// <summary>
/// Composes salutations (design note section 17): Polish = adjective + vocative of the highest-ranked title having a form
/// for the gender (else "Panie" / "Pani"); English = "Dear" + the title's English form (no gender needed) or Mr / Ms +
/// surname; unknown gender → the neutral text of the settings or the template, or the default gender. Pure.
/// </summary>
public static class SalutationResolver
{
    /// <summary>The gender a field renders with and whether it is the default of the settings / the template.</summary>
    public static (Gender Gender, bool IsFallback) EffectiveGender(GenderInfo gender, SalutationOptions options)
    {
        ArgumentNullException.ThrowIfNull(gender);
        ArgumentNullException.ThrowIfNull(options);
        if (gender.Gender != Gender.Unknown || gender.Source == DerivedValueSource.Override)
        {
            return (gender.Gender, false);
        }

        return options.UnknownGender switch
        {
            UnknownGenderMode.Masculine => (Gender.Masculine, true),
            UnknownGenderMode.Feminine => (Gender.Feminine, true),
            _ => (Gender.Unknown, false),
        };
    }

    /// <summary><c>{$salutation}</c> in <paramref name="language"/>.</summary>
    public static SalutationResult Salutation(SalutationPerson person, MessageLanguage language, SalutationVariant variant, SalutationOptions options)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(options);
        // The titled variants look at the title even when none was recognized (its unrecognized words are a finding).
        var consulted = options.UseTitles && variant is SalutationVariant.Full or SalutationVariant.Vocative;
        var useTitle = consulted && person.Title.HasTitle;
        var vocative = variant is SalutationVariant.Vocative or SalutationVariant.VocativeNoTitle;
        return language == MessageLanguage.English
            ? English(person, consulted, useTitle, vocative, options)
            : Polish(person, consulted, useTitle, vocative, options);
    }

    /// <summary><c>{$honorific:case}</c>: Pan / Pana / Panu … or Pani / Panią …; English Mr / Ms; unknown → "Pan/Pani".</summary>
    public static SalutationResult Honorific(SalutationPerson person, MessageLanguage language, GrammaticalCase grammaticalCase, SalutationOptions options)
    {
        ArgumentNullException.ThrowIfNull(person);
        var (gender, _) = EffectiveGender(person.Gender, options);
        string masculine;
        string feminine;
        if (language == MessageLanguage.English)
        {
            (masculine, feminine) = (SalutationData.EnglishMasculine, SalutationData.EnglishFeminine);
        }
        else
        {
            var index = (int)grammaticalCase;
            (masculine, feminine) = (SalutationData.PolishMasculineCases[index], SalutationData.PolishFeminineCases[index]);
        }

        var text = gender switch
        {
            Gender.Masculine => masculine,
            Gender.Feminine => feminine,
            _ => masculine + "/" + feminine,
        };
        return new SalutationResult(text) { UsedGender = true, IsNeutral = gender == Gender.Unknown };
    }

    /// <summary><c>{$byGender:masc/fem[/neutral]}</c>: the part of the gender; unknown → the third part, else "masc/fem".</summary>
    public static SalutationResult ByGender(SalutationPerson person, string argument, SalutationOptions options)
    {
        ArgumentNullException.ThrowIfNull(person);
        var parts = SplitByGender(argument);
        var masculine = parts[0];
        var feminine = parts.Count > 1 ? parts[1] : parts[0];
        var (gender, _) = EffectiveGender(person.Gender, options);
        var text = gender switch
        {
            Gender.Masculine => masculine,
            Gender.Feminine => feminine,
            _ => parts.Count > 2 ? parts[2] : masculine + "/" + feminine,
        };
        return new SalutationResult(text) { UsedGender = true, IsNeutral = gender == Gender.Unknown };
    }

    /// <summary><c>{$title}</c> / <c>{$title:highest}</c> (empty when titles are off).</summary>
    public static SalutationResult Title(SalutationPerson person, bool highest, SalutationOptions options)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(options);
        var text = !options.UseTitles ? string.Empty : highest ? person.Title.Highest : person.Title.Text;
        return new SalutationResult(text) { ConsultedTitle = options.UseTitles, UsedTitle = text.Length > 0 };
    }

    /// <summary><c>{$titledName}</c>: "prof. dr hab. Jan Kowalski", "dr hab. Anna Nowak, prof. UW", "Jan Kowalski".</summary>
    public static SalutationResult TitledName(SalutationPerson person, SalutationOptions options)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(options);
        var name = person.Name;
        var title = person.Title;
        if (!options.UseTitles || !title.HasTitle)
        {
            return new SalutationResult(name) { ConsultedTitle = options.UseTitles };
        }

        var text = $"{title.PrefixText} {name}".Trim();
        if (title.Suffix.Count > 0)
        {
            text = text.Length == 0 ? title.SuffixText : text + ", " + title.SuffixText;
        }

        return new SalutationResult(text) { ConsultedTitle = true, UsedTitle = true };
    }

    /// <summary>The case of an argument of <c>{$honorific}</c> ("dopełniacz", "dopelniacz", "gen"); null for none.</summary>
    public static GrammaticalCase? ParseCase(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            return null;
        }

        var key = SalutationKey.Of(argument);
        return SalutationData.CaseNames.Where(c => SalutationKey.Of(c.Name) == key).Select(c => (GrammaticalCase?)c.Case).FirstOrDefault();
    }

    /// <summary>The variant of an argument of <c>{$salutation}</c>.</summary>
    public static SalutationVariant ParseVariant(string? argument) => SalutationKey.Of(argument) switch
    {
        "notitle" => SalutationVariant.NoTitle,
        "vocative" => SalutationVariant.Vocative,
        "vocativenotitle" => SalutationVariant.VocativeNoTitle,
        _ => SalutationVariant.Full,
    };

    /// <summary>The parts of a <c>{$byGender}</c> argument, trimmed (at least one).</summary>
    public static IReadOnlyList<string> SplitByGender(string? argument) =>
        [.. (argument ?? string.Empty).Split('/').Select(p => p.Trim())];

    /// <summary>
    /// The neutral text of the language with <c>{name}</c> filled in (no name at all: the fixed last resort); for the
    /// vocative the adjectives are removed ("Pani / Panie", "{name}").
    /// </summary>
    public static string Neutral(SalutationPerson person, MessageLanguage language, bool vocative, SalutationOptions options)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(options);
        var text = options.Neutral(language);
        if (text.Contains(SalutationData.NamePlaceholder, StringComparison.OrdinalIgnoreCase))
        {
            var name = person.Name;
            text = name.Length == 0
                ? language == MessageLanguage.English ? SalutationData.LastResortEnglish : SalutationData.LastResortPolish
                : text.Replace(SalutationData.NamePlaceholder, name, StringComparison.OrdinalIgnoreCase);
        }

        return vocative ? WithoutAdjectives(text) : text;
    }

    private static SalutationResult Polish(SalutationPerson person, bool consulted, bool useTitle, bool vocative, SalutationOptions options)
    {
        var (gender, _) = EffectiveGender(person.Gender, options);
        if (gender == Gender.Unknown)
        {
            // The default neutral text keeps a title that has both forms: "Szanowna Pani Profesor / Szanowny Panie Profesorze".
            if (useTitle && options.NeutralPolish is null && person.Title.FirstWithPolishForm is { } row
                && !string.IsNullOrWhiteSpace(row.PolishMasculine) && !string.IsNullOrWhiteSpace(row.PolishFeminine))
            {
                var (feminineForm, masculineForm) = (row.PolishFeminine.Trim(), row.PolishMasculine.Trim());
                var titled = vocative
                    ? $"{feminineForm} / {masculineForm}"
                    : $"{SalutationData.PolishFeminineAdjective} {feminineForm} / {SalutationData.PolishMasculineAdjective} {masculineForm}";
                return new SalutationResult(titled) { UsedGender = true, IsNeutral = true, ConsultedTitle = true, UsedTitle = true };
            }

            return new SalutationResult(Neutral(person, MessageLanguage.Polish, vocative, options)) { UsedGender = true, IsNeutral = true, ConsultedTitle = consulted };
        }

        var masculine = gender == Gender.Masculine;
        var form = useTitle
            ? person.Title.Candidates.Select(r => masculine ? r.PolishMasculine : r.PolishFeminine).FirstOrDefault(f => !string.IsNullOrWhiteSpace(f))?.Trim()
            : null;
        var noun = form ?? (masculine ? SalutationData.PolishMasculineVocative : SalutationData.PolishFeminineVocative);
        var text = vocative ? noun : (masculine ? SalutationData.PolishMasculineAdjective : SalutationData.PolishFeminineAdjective) + " " + noun;
        return new SalutationResult(text) { UsedGender = true, ConsultedTitle = consulted, UsedTitle = form is not null };
    }

    private static SalutationResult English(SalutationPerson person, bool consulted, bool useTitle, bool vocative, SalutationOptions options)
    {
        var surname = person.Surname?.Trim() ?? string.Empty;
        string Compose(string form) => vocative ? $"{form} {surname}" : $"{SalutationData.EnglishGreeting} {form} {surname}";
        var titleForm = useTitle
            ? person.Title.Candidates.Select(r => r.English).FirstOrDefault(f => !string.IsNullOrWhiteSpace(f))?.Trim()
            : null;
        if (titleForm is not null)
        {
            // A title form needs no gender ("Dear Professor Kowalski").
            return surname.Length == 0
                ? new SalutationResult(Neutral(person, MessageLanguage.English, vocative, options)) { ConsultedTitle = true, IsNeutral = true, SurnameMissing = true }
                : new SalutationResult(Compose(titleForm)) { ConsultedTitle = true, UsedTitle = true };
        }

        var (gender, _) = EffectiveGender(person.Gender, options);
        if (gender == Gender.Unknown)
        {
            return new SalutationResult(Neutral(person, MessageLanguage.English, vocative, options)) { UsedGender = true, ConsultedTitle = consulted, IsNeutral = true };
        }

        if (surname.Length == 0)
        {
            return new SalutationResult(Neutral(person, MessageLanguage.English, vocative, options))
            {
                UsedGender = true,
                ConsultedTitle = consulted,
                IsNeutral = true,
                SurnameMissing = true,
            };
        }

        var form = gender == Gender.Masculine ? SalutationData.EnglishMasculine : SalutationData.EnglishFeminine;
        return new SalutationResult(Compose(form)) { UsedGender = true, ConsultedTitle = consulted };
    }

    private static string WithoutAdjectives(string text) =>
        string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !SalutationData.Adjectives.Contains(w, StringComparer.OrdinalIgnoreCase)));
}
