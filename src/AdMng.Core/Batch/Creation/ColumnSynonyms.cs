using AdMng.Core.Model;

namespace AdMng.Core.Batch.Creation;

/// <summary>One entry of the header synonym table of docs/dev/phase3-bulk-create.md 2.1.</summary>
/// <param name="Folded">The folded header (<see cref="ColumnSynonyms.Fold"/>), e.g. <c>telefonkomorkowy</c>.</param>
/// <param name="Target">The suggested target.</param>
/// <param name="OnlyFor">The object kind the entry applies to (kind-dependent entries such as <em>nazwa</em>); null = every kind.</param>
public sealed record ColumnSynonym(string Folded, ColumnSuggestion Target, ObjectKind? OnlyFor)
{
    /// <summary>The target and the kind only (the synonym itself is a header text).</summary>
    public override string ToString() => $"Synonym → {Target} ({OnlyFor?.ToString() ?? "every kind"})";
}

/// <summary>
/// Automatic column mapping by Polish and English header synonyms and by content (docs/dev/phase3-bulk-create.md 2.1,
/// owner B1). Exact matches of the folded header only (no substring matching), never a blocked attribute, never a
/// password-like column; pure and thread-safe.
/// </summary>
public static class ColumnSynonyms
{
    /// <summary>The synonym table (2.1).</summary>
    public static IReadOnlyList<ColumnSynonym> Entries =>
        throw new NotImplementedException("F3 B1: ColumnSynonyms.Entries (2.1).");

    /// <summary>
    /// Lower-case invariant, diacritics removed, every character that is not a letter or digit removed
    /// (<em>Tel. kom.</em> → <c>telkom</c>, <em>Adres e-mail</em> → <c>adresemail</c>).
    /// </summary>
    public static string Fold(string header) =>
        throw new NotImplementedException("F3 B1: ColumnSynonyms.Fold (2.1).");

    /// <summary>
    /// The target of a column: the folded header matched exactly against the synonym table, then against
    /// <paramref name="attributeNames"/> (LDAP names of the attribute catalogue), refined by <paramref name="samples"/> (the
    /// first 20 non-empty cells, e.g. a <em>Telefon</em> column of mobile numbers → <c>mobile</c>); null = no suggestion.
    /// </summary>
    public static ColumnSuggestion? Suggest(
        string header,
        ObjectKind kind,
        IReadOnlyCollection<string>? attributeNames = null,
        IReadOnlyList<string>? samples = null) =>
        throw new NotImplementedException("F3 B1: ColumnSynonyms.Suggest (2.1).");

    /// <summary>A suggestion for a column without a header from its samples (e-mail, mobile, phone), or null.</summary>
    public static ColumnSuggestion? SuggestFromContent(IReadOnlyList<string> samples, ObjectKind kind) =>
        throw new NotImplementedException("F3 B1: ColumnSynonyms.SuggestFromContent (2.1).");

    /// <summary>
    /// A password-like header (substrings <c>haslo</c>, <c>hasla</c>, <c>password</c>, <c>passwd</c>, <c>pwd</c>,
    /// <c>kennwort</c>; exact <c>pin</c>, <c>kodpin</c>, <c>pass</c>): the column is never mapped, masked and unreachable from
    /// patterns.
    /// </summary>
    public static bool IsPasswordLike(string header) =>
        throw new NotImplementedException("F3 B1: ColumnSynonyms.IsPasswordLike (2.1).");

    /// <summary>A personal identifier header (PESEL, NIP, ID card, birth date, SSN, IBAN): ignored unless mapped explicitly.</summary>
    public static bool IsPersonalIdentifier(string header) =>
        throw new NotImplementedException("F3 B1: ColumnSynonyms.IsPersonalIdentifier (2.1).");
}
