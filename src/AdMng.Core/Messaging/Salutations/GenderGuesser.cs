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
/// exception lists, then "ends in -a → feminine"), the surname (-ska / -ski …; against a rule result it is a conflict).
/// Pure.
/// </summary>
public static class GenderGuesser
{
    private static readonly string[] FeminineSurnameEndings = ["ska", "cka", "dzka"];
    private static readonly string[] MasculineSurnameEndings = ["ski", "cki", "dzki"];

    /// <param name="overrideGender">Chosen in the preview (<see cref="Gender.Unknown"/> = neutral on purpose); null = none.</param>
    /// <param name="attributeValue">Value of the gender attribute, or null.</param>
    /// <param name="honorific">Gender of an honorific word of the title attribute.</param>
    /// <param name="givenName">Given name.</param>
    /// <param name="surname">Surname.</param>
    /// <param name="options">Value lists and the operator's given-name list.</param>
    public static GenderInfo Guess(Gender? overrideGender, string? attributeValue, Gender honorific, string? givenName, string? surname, SalutationOptions options)
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
        var surnamePart = FirstSurnamePart(surname);
        var bySurname = FromSurname(surnamePart);
        if (byName is not { } name)
        {
            return bySurname == Gender.Unknown
                ? new GenderInfo { UnknownValue = unknownValue }
                : new GenderInfo { Gender = bySurname, Source = DerivedValueSource.Surname, IsGuess = true, Detail = surname?.Trim(), UnknownValue = unknownValue };
        }

        if (name.Source == DerivedValueSource.NameRule && bySurname != Gender.Unknown && bySurname != name.Gender)
        {
            return new GenderInfo { IsConflict = true, Detail = $"{first} {surname?.Trim()}".Trim(), UnknownValue = unknownValue };
        }

        return new GenderInfo { Gender = name.Gender, Source = name.Source, IsGuess = true, Detail = first, UnknownValue = unknownValue };
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

    /// <summary>-ska / -cka / -dzka → feminine, -ski / -cki / -dzki → masculine, else unknown.</summary>
    public static Gender FromSurname(string? surname)
    {
        var key = SalutationKey.Of(surname);
        if (FeminineSurnameEndings.Any(e => key.EndsWith(e, StringComparison.Ordinal)))
        {
            return Gender.Feminine;
        }

        return MasculineSurnameEndings.Any(e => key.EndsWith(e, StringComparison.Ordinal)) ? Gender.Masculine : Gender.Unknown;
    }

    private static string? FirstSurnamePart(string? surname)
    {
        var text = surname?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var end = text.IndexOfAny(['-', ' ']);
        return end < 0 ? text : text[..end];
    }
}
