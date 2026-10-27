using AdMng.Core.Messaging.Salutations;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.UserMessages;

/// <summary>The gender the operator chose for one message in the preview (U1d) – for this run only, never saved.</summary>
public enum SalutationGenderChoice
{
    /// <summary>As derived (attribute, names, defaults).</summary>
    Automatic,

    Masculine,

    Feminine,

    /// <summary>The neutral text, without a warning.</summary>
    Neutral,
}

/// <summary>
/// Localized texts of the salutations (U1d). Key families: <c>UserMessages.Salutation.Source.{DerivedValueSource}</c>,
/// <c>UserMessages.Salutation.Gender.{Gender}</c>, <c>UserMessages.Salutation.UnknownGender.{UnknownGenderMode}</c>,
/// <c>UserMessages.Salutation.Choice.{SalutationGenderChoice}</c>, <c>UserMessages.Salutation.Field.{field}</c> (the derived
/// fields of <see cref="MessageSalutations"/>) and <c>UserMessages.Issue.{code}</c> of <see cref="SalutationIssueCodes"/>.
/// </summary>
public static class SalutationText
{
    /// <summary>Mark of a guessed value in the preview.</summary>
    public const string GuessedMark = "≈";

    /// <summary>Mark of a fallback (neutral text, conflict, default gender) in the preview.</summary>
    public const string FallbackMark = "⚠";

    /// <summary>The derived fields (account and manager).</summary>
    public static IReadOnlyList<string> Fields { get; } =
    [
        MessageSalutations.TitleField, MessageSalutations.GenderField, MessageSalutations.SalutationField,
        "managerTitle", "managerGender", "managerSalutation",
    ];

    public static string SourceKey(DerivedValueSource source) => "UserMessages.Salutation.Source." + source;

    public static string GenderKey(Gender gender) => "UserMessages.Salutation.Gender." + gender;

    public static string UnknownGenderKey(UnknownGenderMode mode) => "UserMessages.Salutation.UnknownGender." + mode;

    public static string ChoiceKey(SalutationGenderChoice choice) => "UserMessages.Salutation.Choice." + choice;

    public static string FieldKey(string field) => "UserMessages.Salutation.Field." + field;

    /// <summary>Every key of the dynamic families (localization test).</summary>
    public static IEnumerable<string> AllDynamicKeys() =>
        Enum.GetValues<DerivedValueSource>().Select(SourceKey)
            .Concat(Enum.GetValues<Gender>().Select(GenderKey))
            .Concat(Enum.GetValues<UnknownGenderMode>().Select(UnknownGenderKey))
            .Concat(Enum.GetValues<SalutationGenderChoice>().Select(ChoiceKey))
            .Concat(Fields.Select(FieldKey))
            .Concat(SalutationIssueCodes.All.Select(UserMessageText.IssueKey))
            .Append(UserMessageText.FilterKey(UserMessagePreviewFilter.Guessed));

    /// <summary>The override of a choice for <c>UserMessagePlanRequest.GenderOverrides</c>; null = automatic.</summary>
    public static Gender? OverrideOf(SalutationGenderChoice choice) => choice switch
    {
        SalutationGenderChoice.Masculine => Gender.Masculine,
        SalutationGenderChoice.Feminine => Gender.Feminine,
        SalutationGenderChoice.Neutral => Gender.Unknown,
        _ => null,
    };

    /// <summary>"z atrybutu tytułu", "z imienia (reguła)" … plus "zgadnięte" for a guess.</summary>
    public static string Source(ILocalizer localizer, DerivedFieldValue value)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(value);
        var parts = new List<string> { localizer[SourceKey(value.Source)] };
        if (!string.IsNullOrWhiteSpace(value.Detail))
        {
            parts.Add(value.Detail);
        }

        if (value.IsGuess)
        {
            parts.Add(localizer[L.Salutations.Guessed]);
        }

        return string.Join(" · ", parts);
    }

    /// <summary>The value as shown: the title or "(brak)", the localized gender, the salutation.</summary>
    public static string Value(ILocalizer localizer, DerivedFieldValue value)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(value);
        if (IsGenderField(value.Field) && Enum.TryParse<Gender>(value.Value, out var gender))
        {
            return localizer[GenderKey(gender)];
        }

        return value.Value.Length == 0 ? localizer[L.Salutations.NoTitle] : value.Value;
    }

    /// <summary>The field is the gender of the account or of the manager.</summary>
    public static bool IsGenderField(string field) => field is MessageSalutations.GenderField or "managerGender";

    /// <summary>The derived field belongs to the manager ("managerTitle", "managerGender", "managerSalutation").</summary>
    public static bool IsManagerField(string field) => field.StartsWith(MessageSalutations.ManagerPrefix, StringComparison.Ordinal);

    /// <summary>The field is the salutation of the account or of the manager.</summary>
    public static bool IsSalutationField(string field) => field is MessageSalutations.SalutationField or "managerSalutation";

    /// <summary>"⚠" for a fallback, "≈" for a guess, else empty.</summary>
    public static string Mark(DerivedFieldValue? value) =>
        value is null ? string.Empty : value.IsFallback ? FallbackMark : value.IsGuess ? GuessedMark : string.Empty;
}
