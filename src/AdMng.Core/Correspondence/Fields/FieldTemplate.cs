namespace AdMng.Core.Correspondence.Fields;

/// <summary>One <c>{{Pole}}</c> or <c>{{Pole|tekst gdy puste}}</c> of a template.</summary>
/// <param name="Field">The field name as written (trimmed).</param>
/// <param name="Fallback">The text rendered when the value is empty or unknown, or null.</param>
/// <param name="Start">Index of the opening <c>{{</c>.</param>
/// <param name="Length">Length of the token including the braces.</param>
public sealed record FieldToken(string Field, string? Fallback, int Start, int Length);

/// <summary>How merged values are encoded.</summary>
public enum FieldEncoding
{
    /// <summary>As is.</summary>
    Text,

    /// <summary>HTML-encoded, line breaks become <c>&lt;br&gt;</c>.</summary>
    Html,

    /// <summary>A header (subject): control characters become spaces.</summary>
    Header,
}

/// <summary>
/// The field syntax of Korespondencja (MailerWPiA's <c>{{Pole}}</c>, decision D1, design note 3.2): any column name,
/// spaces and diacritics allowed, plus <c>{{Pole|tekst gdy puste}}</c>; no nesting; "{{" without "}}" is literal.
/// </summary>
public static class FieldTemplate
{
    private const string Track = "U5 T1 core";

    /// <summary>The longest field name of a token.</summary>
    public const int MaxFieldNameLength = 80;

    /// <summary>The tokens of <paramref name="template"/>: <c>{{ Pole }}</c> or <c>{{ Pole | tekst }}</c>, in order.</summary>
    public static IReadOnlyList<FieldToken> Parse(string? template) => throw new NotImplementedException(Track);

    /// <summary>The fields used, distinct by <see cref="FieldKey"/>, in the first spelling.</summary>
    public static IReadOnlyList<string> UsedFields(string? template) => throw new NotImplementedException(Track);

    /// <summary>
    /// Joins tokens split by tags or <c>&amp;nbsp;</c> (MailerWPiA's RepairTokens); never inside attribute values.
    /// </summary>
    public static string RepairHtml(string? html) => throw new NotImplementedException(Track);

    /// <summary>
    /// Replaces every token with its value. <paramref name="lookup"/> returns null for an unknown field, "" for a known
    /// empty one; unknown and empty field names are added to <paramref name="unknown"/> / <paramref name="empty"/>.
    /// </summary>
    public static string Merge(string? template, Func<string, string?> lookup, FieldEncoding encoding,
        ICollection<string>? unknown = null, ICollection<string>? empty = null) => throw new NotImplementedException(Track);
}
