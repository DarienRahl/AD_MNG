using AdMng.Core.Messaging.Salutations;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.UserMessages;

/// <summary>One derived value of a message as the details pane shows it ("Pola wyliczone", U1d).</summary>
/// <param name="Label">"Tytuł", "Płeć", "Zwrot", "Zwrot do przełożonego" …</param>
/// <param name="Value">The title, the gender or the salutation.</param>
/// <param name="Source">Where it came from, the deciding word and "zgadnięte".</param>
/// <param name="Mark">"≈" (guess), "⚠" (fallback) or empty.</param>
public sealed record DerivedValueRow(string Label, string Value, string Source, string Mark);

/// <summary>The salutation side of a planned message (U1d): the "Zwrot" column, the derived fields and the gender override.</summary>
public sealed partial class UserMessageItemViewModel
{
    /// <summary>The gender the operator chose for this message in the preview; changing it re-plans the message.</summary>
    [ObservableProperty]
    public partial SalutationGenderChoice GenderChoice { get; set; }

    /// <summary>The template uses salutation fields for this message.</summary>
    public bool HasDerived => Planned.Derived.Count > 0;

    /// <summary>A derived value is guessed, a fallback or has a finding – the "Zgadnięte zwroty" filter.</summary>
    public bool HasGuessedSalutation => Planned.Derived.Any(d => d.IsGuess || d.IsFallback || d.Issue is not null);

    /// <summary>The salutation of the account, else the one of the manager (manager mode), or null.</summary>
    public DerivedFieldValue? Salutation =>
        Planned.Derived.FirstOrDefault(d => d.Field == MessageSalutations.SalutationField)
        ?? Planned.Derived.FirstOrDefault(d => SalutationText.IsSalutationField(d.Field));

    /// <summary>
    /// The "Zwrot" column: "≈ Szanowna Pani", "⚠ Szanowna Pani / Szanowny Panie", "Szanowny Panie Profesorze"; without a
    /// <c>{$salutation}</c> field only the mark of a guessed or fallback gender that <c>{$honorific}</c> / <c>{$byGender}</c>
    /// used ("⚠" for "Pan/Pani").
    /// </summary>
    public string SalutationColumnText => Salutation is { } salutation
        ? $"{SalutationText.Mark(salutation)} {salutation.Value}".Trim()
        : SalutationText.Mark(MarkedGender);

    /// <summary>Tooltip of the column: where the salutation (or the marked gender) came from.</summary>
    public string? SalutationToolTip => (Salutation ?? MarkedGender) is { } value ? SalutationText.Source(_localizer, value) : null;

    /// <summary>A gender a field used that is guessed or a fallback (account first), or null.</summary>
    private DerivedFieldValue? MarkedGender =>
        Planned.Derived.FirstOrDefault(d => SalutationText.IsGenderField(d.Field) && (d.IsFallback || d.IsGuess));

    /// <summary>The rows of "Pola wyliczone".</summary>
    public IReadOnlyList<DerivedValueRow> DerivedRows =>
        [.. Planned.Derived.Select(d => new DerivedValueRow(
            _localizer[SalutationText.FieldKey(d.Field)],
            SalutationText.Value(_localizer, d),
            SalutationText.Source(_localizer, d),
            SalutationText.Mark(d)))];
}
