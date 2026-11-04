namespace AdMng.Core.Messaging.Salutations;

/// <summary>
/// Codes of the salutation findings of a message (U1d; localization keys <c>UserMessages.Issue.{code}</c>, argument {0} =
/// the name or value concerned). Raised only when the template uses a field that needed the information.
/// </summary>
public static class SalutationIssueCodes
{
    /// <summary>Info: the gender was guessed from the given name, the surname or the title.</summary>
    public const string GenderGuessed = "GenderGuessed";

    /// <summary>Info: the title was recognized in the display name.</summary>
    public const string TitleFromDisplayName = "TitleFromDisplayName";

    /// <summary>Warning: the gender is unknown – the neutral text was used.</summary>
    public const string GenderUnknown = "GenderUnknown";

    /// <summary>Warning: the given name and the surname disagree – the neutral text (or the default gender) was used.</summary>
    public const string GenderConflict = "GenderConflict";

    /// <summary>Warning: the gender is unknown – the default gender of the settings / the template was used.</summary>
    public const string GenderDefaulted = "GenderDefaulted";

    /// <summary>Warning: words of the title attribute are not in the title table.</summary>
    public const string TitleUnrecognized = "TitleUnrecognized";

    /// <summary>Warning: an English salutation needs the surname and the account has none – the neutral text was used.</summary>
    public const string SurnameMissing = "SurnameMissing";

    /// <summary>Info: the gender attribute holds a value of neither list.</summary>
    public const string GenderValueUnknown = "GenderValueUnknown";

    /// <summary>Every code (localization test).</summary>
    public static IReadOnlyList<string> All { get; } =
        [GenderGuessed, TitleFromDisplayName, GenderUnknown, GenderConflict, GenderDefaulted, TitleUnrecognized, SurnameMissing, GenderValueUnknown];

    /// <summary>The severity of a code.</summary>
    public static MessageIssueSeverity Severity(string code) => code switch
    {
        GenderUnknown or GenderConflict or GenderDefaulted or TitleUnrecognized or SurnameMissing => MessageIssueSeverity.Warning,
        _ => MessageIssueSeverity.Info,
    };
}
