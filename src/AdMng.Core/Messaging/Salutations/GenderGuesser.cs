using System.Buffers;

namespace AdMng.Core.Messaging.Salutations;

/// <summary>The gender of a person with where it came from (U1d).</summary>
public sealed record GenderInfo
{
    /// <summary>Nothing known.</summary>
    public static GenderInfo Unknown { get; } = new();

    public Gender Gender { get; init; }

    public DerivedValueSource Source { get; init; }

    /// <summary>Guessed from the given name or the surname (not certain).</summary>
    public bool IsGuess { get; init; }

    /// <summary>The given name (rule) and the surname disagree – the gender is unknown (warning <c>GenderConflict</c>).</summary>
    public bool IsConflict { get; init; }

    /// <summary>The word that decided (given name, surname, attribute value) – shown in the preview only.</summary>
    public string? Detail { get; init; }

    /// <summary>A value of the gender attribute that is in neither list (info <c>GenderValueUnknown</c>).</summary>
    public string? UnknownValue { get; init; }
}

/// <summary>
/// Determines the gender (design note section 16): the operator's override for this run, the gender attribute (two value
/// lists) or an honorific word of the title attribute, the operator's given-name list, the first given name (built-in
/// exception lists, then "ends in -a → feminine" – in English messages for Polish names only), the surname (-ska / -ski
/// …; against a rule result – in English messages too – it is a conflict), finally a title for one gender only
/// (<see cref="WithTitle"/>). Pure.
/// </summary>
public static class GenderGuesser
{
    private static readonly string[] FeminineSurnameEndings = ["ska", "cka", "dzka"];
    private static readonly string[] MasculineSurnameEndings = ["ski", "cki", "dzki"];

    /// <summary>Letters found in Polish names only (not "ó" – Spanish, Hungarian …).</summary>
    private static readonly SearchValues<char> PolishLetters = SearchValues.Create("ąćęłńśźżĄĆĘŁŃŚŹŻ");

    /// <param name="overrideGender">Chosen in the preview (<see cref="Gender.Unknown"/> = neutral on purpose); null = none.</param>
    /// <param name="attributeValue">Value of the gender attribute, or null.</param>
    /// <param name="honorific">Gender of an honorific word of the title attribute.</param>
    /// <param name="givenName">Given name.</param>
    /// <param name="surname">Surname.</param>
    /// <param name="options">Value lists and the operator's given-name list.</param>
    /// <param name="polishNameRule">
    /// Let the Polish rule "ends in -a → feminine, else masculine" decide for every name outside the lists (Polish
    /// messages). False (English messages): recipients mostly have names it does not fit (Jennifer, Karen, Ilya), so it
    /// decides only for Polish names – with Polish letters or on <see cref="GivenNameLists.Polish"/> ("Jan Nowak" → "Dear Mr
    /// Nowak") – and gives no signal of its own for the others. It still vetoes a disagreeing surname in both languages
    /// ("Laura Nowakowski", "Paweł Gąska" → conflict), so a -ski / -ska surname never decides against an obvious given name.
    /// </param>
    public static GenderInfo Guess(Gender? overrideGender, string? attributeValue, Gender honorific, string? givenName, string? surname, SalutationOptions options, bool polishNameRule = true)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (overrideGender is { } chosen)
        {
            return new GenderInfo { Gender = chosen, Source = DerivedValueSource.Override };
        }

        string? unknownValue = null;
        if (!string.IsNullOrWhiteSpace(attributeValue))
        {
            var value = attributeValue.Trim();
            if (options.MasculineValues.Any(v => SalutationKey.Same(v, value)))
            {
                return new GenderInfo { Gender = Gender.Masculine, Source = DerivedValueSource.Attribute, Detail = value };
            }

            if (options.FeminineValues.Any(v => SalutationKey.Same(v, value)))
            {
                return new GenderInfo { Gender = Gender.Feminine, Source = DerivedValueSource.Attribute, Detail = value };
            }

            unknownValue = value;
        }

        if (honorific != Gender.Unknown)
        {
            return new GenderInfo { Gender = honorific, Source = DerivedValueSource.TitleAttribute, UnknownValue = unknownValue };
        }

        var first = FirstGivenName(givenName);
        if (first is not null && options.GivenNames.FirstOrDefault(n => n.Gender != Gender.Unknown
            && (SalutationKey.Same(n.Name, first) || SalutationKey.Same(n.Name, givenName))) is { } listed)
        {
            return new GenderInfo { Gender = listed.Gender, Source = DerivedValueSource.OperatorList, Detail = first, UnknownValue = unknownValue };
        }

        var byName = FromGivenName(first);
        var bySurname = FromSurname(surname);
        if (byName is { Source: DerivedValueSource.NameRule } rule && bySurname != Gender.Unknown && bySurname != rule.Gender)
        {
            // Also in English messages: the rule cannot decide there, but it still vetoes the surname ("Laura Nowakowski").
            return new GenderInfo { IsConflict = true, Detail = $"{first} {surname?.Trim()}".Trim(), UnknownValue = unknownValue };
        }

        if (!polishNameRule && byName?.Source == DerivedValueSource.NameRule && !LooksPolish(first!))
        {
            byName = null;
        }

        if (byName is not { } name)
        {
            return bySurname == Gender.Unknown
                ? new GenderInfo { UnknownValue = unknownValue }
                : new GenderInfo { Gender = bySurname, Source = DerivedValueSource.Surname, IsGuess = true, Detail = surname?.Trim(), UnknownValue = unknownValue };
        }

        return new GenderInfo { Gender = name.Gender, Source = name.Source, IsGuess = true, Detail = first, UnknownValue = unknownValue };
    }

    /// <summary>
    /// <paramref name="gender"/>, or – when the names gave no signal at all (no conflict, no override) – the gender the title
    /// implies (<see cref="TitleInfo.ImpliedGender"/>: "ks. dr" exists for priests only) as a guess with the title as detail.
    /// </summary>
    public static GenderInfo WithTitle(GenderInfo gender, TitleInfo title)
    {
        ArgumentNullException.ThrowIfNull(gender);
        ArgumentNullException.ThrowIfNull(title);
        return gender is { Gender: Gender.Unknown, IsConflict: false, Source: DerivedValueSource.None } && title.ImpliedGender is var implied and not Gender.Unknown
            ? gender with { Gender = implied, Source = title.Source, IsGuess = true, Detail = title.Text }
            : gender;
    }

    /// <summary>A given name the Polish rule fits in English messages too: Polish letters or a frequent Polish name.</summary>
    public static bool LooksPolish(string firstGivenName)
    {
        ArgumentNullException.ThrowIfNull(firstGivenName);
        return firstGivenName.AsSpan().ContainsAny(PolishLetters) || GivenNameLists.Polish.Contains(SalutationKey.Of(firstGivenName));
    }

    /// <summary>"Jan Maria" → "Jan", "Anna-Maria" → "Anna"; initials ("J.", "J") and empty → null.</summary>
    public static string? FirstGivenName(string? givenName)
    {
        var text = givenName?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var end = text.IndexOfAny([' ', '\t', '-', ' ']);
        var first = end < 0 ? text : text[..end];
        return first.EndsWith('.') || SalutationKey.Of(first).Length < 2 ? null : first;
    }

    /// <summary>The gender a given name suggests with the kind of signal; null = no signal (unisex, initials, empty).</summary>
    public static (Gender Gender, DerivedValueSource Source)? FromGivenName(string? firstGivenName)
    {
        if (firstGivenName is null)
        {
            return null;
        }

        var key = SalutationKey.Of(firstGivenName);
        if (key.Length < 2 || GivenNameLists.Unisex.Contains(key))
        {
            return null;
        }

        if (GivenNameLists.MasculineEndingInA.Contains(key))
        {
            return (Gender.Masculine, DerivedValueSource.NameException);
        }

        if (GivenNameLists.FeminineNotEndingInA.Contains(key))
        {
            return (Gender.Feminine, DerivedValueSource.NameException);
        }

        return (key.EndsWith('a') ? Gender.Feminine : Gender.Masculine, DerivedValueSource.NameRule);
    }

    /// <summary>
    /// -ska / -cka / -dzka → feminine, -ski / -cki / -dzki → masculine, else unknown. Every part of a double-barrelled
    /// surname counts – the adjectival part is often the second one ("Nowak-Kowalska"); parts that disagree give none.
    /// </summary>
    public static Gender FromSurname(string? surname)
    {
        var genders = (surname ?? string.Empty).Split(['-', '\u2013', ' ', '\t', '\u00A0'], StringSplitOptions.RemoveEmptyEntries)
            .Select(FromSurnamePart)
            .Where(g => g != Gender.Unknown)
            .Distinct()
            .ToList();
        return genders.Count == 1 ? genders[0] : Gender.Unknown;
    }

    private static Gender FromSurnamePart(string part)
    {
        var key = SalutationKey.Of(part);
        if (FeminineSurnameEndings.Any(e => key.EndsWith(e, StringComparison.Ordinal)))
        {
            return Gender.Feminine;
        }

        return MasculineSurnameEndings.Any(e => key.EndsWith(e, StringComparison.Ordinal)) ? Gender.Masculine : Gender.Unknown;
    }
}
