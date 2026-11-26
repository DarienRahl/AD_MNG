using AdMng.Core.Batch.Creation;
using AdMng.Core.Model;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.BulkCreate;

/// <summary>
/// The dynamic resource keys of the bulk creation wizard that more than one F3 agent shows (docs/dev/phase3-bulk-create.md
/// 2.9; complete in the contract): <c>Issue.&lt;BulkCreateIssueCodes&gt;</c>, <c>BulkCreate.Title.&lt;ObjectKind&gt;</c> and
/// <c>BulkCreate.Kind.&lt;ObjectKind&gt;</c> (the four kinds of <see cref="Kinds"/>), <c>BulkCreate.Target.&lt;ColumnTargetKind&gt;</c>,
/// <c>BulkCreate.HeaderMode.&lt;HeaderMode&gt;</c>, <c>BulkCreate.NameOrder.&lt;NameOrder&gt;</c>, <c>BulkCreate.Case.&lt;TextCase&gt;</c>,
/// <c>BulkCreate.SuggestionNote.&lt;SuggestionNote&gt;</c> (without <see cref="SuggestionNote.None"/>),
/// <c>BulkCreate.Warning.&lt;TableWarningCode&gt;</c> (<c>{0}</c> the row number, <c>{1}</c> the column – header or Excel letter –,
/// <c>{2}</c> the count), <c>BulkCreate.PatternError.&lt;PatternErrorCode&gt;</c> (<c>{0}</c> the position, <c>{1}</c> the token;
/// the did-you-mean text is <see cref="L.BulkCreate.PatternSuggestion"/> with <c>{0}</c> the suggestion),
/// <c>BulkCreate.Function.&lt;name&gt;</c> (<see cref="Functions"/>, the descriptions <em>Wstaw pole</em> shows),
/// <c>BulkCreate.CounterMode.&lt;CounterMode&gt;</c>, <c>BulkCreate.Collision.&lt;CollisionStrategy&gt;</c>,
/// <c>BulkCreate.InitialMode.&lt;InitialPasswordMode&gt;</c>, <c>BulkCreate.Phones.&lt;PhoneFormat&gt;</c>,
/// <c>BulkCheck.Status.&lt;BulkRowStatus&gt;</c>, <c>BulkCheck.Source.&lt;BulkValueSource&gt;</c>,
/// <c>BulkCheck.Scope.&lt;UniquenessScope&gt;</c> (<see cref="UniquenessScope.Forest"/> takes <c>{0}</c>: the DC) and
/// <c>BulkCheck.Filter.&lt;BulkCheckFilter&gt;</c>. All of them live in the contract block of both resx files; the owners may
/// reword their families, never remove a key.
/// </summary>
public static class BulkCreateTextKeys
{
    /// <summary>The kinds the wizard creates, in the order of its kind combo.</summary>
    public static IReadOnlyList<ObjectKind> Kinds { get; } = [ObjectKind.User, ObjectKind.Contact, ObjectKind.Group, ObjectKind.Computer];

    /// <summary>The template functions <em>Wstaw pole</em> explains (2.3, 2.9), in the order it lists them.</summary>
    public static IReadOnlyList<string> Functions { get; } =
        ["lower", "upper", "title", "capitalize", "trim", "nodiacritics", "nospaces", "alnum", "sam", "first", "last", "replace", "default"];

    /// <summary>The text of an issue code of <see cref="BulkCreateIssueCodes"/> (rendered by <see cref="DisplayNames.IssueText"/>).</summary>
    public static string IssueKey(string code) => DisplayNames.IssueKey(code);

    /// <summary><em>Zakładanie wielu kont / kontaktów / grup / komputerów</em>.</summary>
    public static string TitleKey(ObjectKind kind) => "BulkCreate.Title." + Known(kind);

    /// <summary><em>Użytkownicy / Kontakty / Grupy / Komputery</em> (the kind combo).</summary>
    public static string KindKey(ObjectKind kind) => "BulkCreate.Kind." + Known(kind);

    public static string TargetKey(ColumnTargetKind target) => "BulkCreate.Target." + target;

    public static string HeaderModeKey(HeaderMode mode) => "BulkCreate.HeaderMode." + mode;

    public static string NameOrderKey(NameOrder order) => "BulkCreate.NameOrder." + order;

    public static string CaseKey(TextCase textCase) => "BulkCreate.Case." + textCase;

    /// <summary>The note of a column suggestion; <paramref name="note"/> must not be <see cref="SuggestionNote.None"/>.</summary>
    public static string SuggestionNoteKey(SuggestionNote note) => note == SuggestionNote.None
        ? throw new ArgumentOutOfRangeException(nameof(note), "SuggestionNote.None has no text.")
        : "BulkCreate.SuggestionNote." + note;

    /// <summary>A reading finding: <c>{0}</c> the row number, <c>{1}</c> the column, <c>{2}</c> the count.</summary>
    public static string WarningKey(TableWarningCode code) => "BulkCreate.Warning." + code;

    /// <summary>A pattern problem: <c>{0}</c> the position, <c>{1}</c> the token's source text.</summary>
    public static string PatternErrorKey(PatternErrorCode code) => "BulkCreate.PatternError." + code;

    /// <summary>The description of a template function of <see cref="Functions"/>.</summary>
    public static string FunctionKey(string function) => Functions.Contains(function, StringComparer.Ordinal)
        ? "BulkCreate.Function." + function
        : throw new ArgumentOutOfRangeException(nameof(function), "Not a function of the pattern language.");

    public static string CounterModeKey(CounterMode mode) => "BulkCreate.CounterMode." + mode;

    public static string CollisionKey(CollisionStrategy strategy) => "BulkCreate.Collision." + strategy;

    public static string InitialModeKey(InitialPasswordMode mode) => "BulkCreate.InitialMode." + mode;

    public static string PhonesKey(PhoneFormat format) => "BulkCreate.Phones." + format;

    public static string StatusKey(BulkRowStatus status) => "BulkCheck.Status." + status;

    public static string SourceKey(BulkValueSource source) => "BulkCheck.Source." + source;

    /// <summary>The uniqueness scope note; <see cref="UniquenessScope.Forest"/> takes <c>{0}</c> = the DC.</summary>
    public static string ScopeKey(UniquenessScope scope) => "BulkCheck.Scope." + scope;

    public static string FilterKey(BulkCheckFilter filter) => "BulkCheck.Filter." + filter;

    /// <summary>Every key of the families (both languages must have them).</summary>
    public static IEnumerable<string> AllKeys()
    {
        foreach (var code in BulkCreateIssueCodes.All)
        {
            yield return IssueKey(code);
        }

        foreach (var kind in Kinds)
        {
            yield return TitleKey(kind);
            yield return KindKey(kind);
        }

        foreach (var value in Enum.GetValues<ColumnTargetKind>())
        {
            yield return TargetKey(value);
        }

        foreach (var value in Enum.GetValues<HeaderMode>())
        {
            yield return HeaderModeKey(value);
        }

        foreach (var value in Enum.GetValues<NameOrder>())
        {
            yield return NameOrderKey(value);
        }

        foreach (var value in Enum.GetValues<TextCase>())
        {
            yield return CaseKey(value);
        }

        foreach (var value in Enum.GetValues<SuggestionNote>().Where(n => n != SuggestionNote.None))
        {
            yield return SuggestionNoteKey(value);
        }

        foreach (var value in Enum.GetValues<TableWarningCode>())
        {
            yield return WarningKey(value);
        }

        foreach (var value in Enum.GetValues<PatternErrorCode>())
        {
            yield return PatternErrorKey(value);
        }

        foreach (var function in Functions)
        {
            yield return FunctionKey(function);
        }

        foreach (var value in Enum.GetValues<CounterMode>())
        {
            yield return CounterModeKey(value);
        }

        foreach (var value in Enum.GetValues<CollisionStrategy>())
        {
            yield return CollisionKey(value);
        }

        foreach (var value in Enum.GetValues<InitialPasswordMode>())
        {
            yield return InitialModeKey(value);
        }

        foreach (var value in Enum.GetValues<PhoneFormat>())
        {
            yield return PhonesKey(value);
        }

        foreach (var value in Enum.GetValues<BulkRowStatus>())
        {
            yield return StatusKey(value);
        }

        foreach (var value in Enum.GetValues<BulkValueSource>())
        {
            yield return SourceKey(value);
        }

        foreach (var value in Enum.GetValues<UniquenessScope>())
        {
            yield return ScopeKey(value);
        }

        foreach (var value in Enum.GetValues<BulkCheckFilter>())
        {
            yield return FilterKey(value);
        }
    }

    private static ObjectKind Known(ObjectKind kind) => Kinds.Contains(kind)
        ? kind
        : throw new ArgumentOutOfRangeException(nameof(kind), kind, "The bulk creation wizard creates users, contacts, groups and computers.");
}
