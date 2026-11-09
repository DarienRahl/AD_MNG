namespace AdMng.Core.Batch.Creation;

/// <summary>
/// Cell-level text rules of the wizard (docs/dev/phase3-bulk-create.md 2.1, owner B1): normalisation, Excel error values
/// and scientific notation, content tests (e-mail, phone, Polish mobile, all caps), the operator's case and phone
/// choices, splitting of multi-valued cells, group lists and full names. Pure and thread-safe.
/// </summary>
public static class CellText
{
    /// <summary>
    /// BOM and zero-width characters removed; NBSP, U+2007 and U+202F → space; runs of spaces → one; trimmed; CR, LF and
    /// CRLF inside the cell → CRLF.
    /// </summary>
    public static string Normalize(string value) =>
        throw new NotImplementedException("F3 B1: CellText.Normalize (2.1).");

    /// <summary>An Excel error value in Polish or English (<c>#N/D!</c>, <c>#ARG!</c> … <c>#N/A</c>, <c>#VALUE!</c> …).</summary>
    public static bool IsSpreadsheetError(string value) =>
        throw new NotImplementedException("F3 B1: CellText.IsSpreadsheetError (2.1).");

    /// <summary>Excel's display of a long number: <c>^[+-]?\d([.,]\d+)?E[+-]?\d+$</c>, case-insensitive (<c>4,86E+11</c>).</summary>
    public static bool IsScientificNotation(string value) =>
        throw new NotImplementedException("F3 B1: CellText.IsScientificNotation (2.1).");

    /// <summary>The cell looks like an e-mail address (header detection and content suggestions).</summary>
    public static bool LooksLikeEmail(string value) =>
        throw new NotImplementedException("F3 B1: CellText.LooksLikeEmail (2.1).");

    /// <summary>The cell looks like a phone number (7 or more digits, phone punctuation only).</summary>
    public static bool LooksLikePhone(string value) =>
        throw new NotImplementedException("F3 B1: CellText.LooksLikePhone (2.1).");

    /// <summary>
    /// A Polish mobile number: 9 digits, or <c>48</c> / <c>+48</c> / <c>0048</c> + 9 digits, whose first two digits are
    /// 45, 50, 51, 53, 57, 60, 66, 69, 72, 73, 78, 79 or 88.
    /// </summary>
    public static bool LooksLikePolishMobile(string value) =>
        throw new NotImplementedException("F3 B1: CellText.LooksLikePolishMobile (2.1).");

    /// <summary>Letters only upper case, at least two letters (<em>KOWALSKI</em>).</summary>
    public static bool IsAllCaps(string value) =>
        throw new NotImplementedException("F3 B1: CellText.IsAllCaps (2.1).");

    /// <summary>Applies the operator's case choice (<see cref="TextCase.Title"/>: <em>NOWAK-KOWALSKA</em> → <em>Nowak-Kowalska</em>).</summary>
    public static string ApplyCase(string value, TextCase textCase) =>
        throw new NotImplementedException("F3 B1: CellText.ApplyCase (2.1).");

    /// <summary>The values of a multi-valued attribute cell: split on <c>|</c> only (a line break stays inside a value).</summary>
    public static IReadOnlyList<string> SplitValues(string value) =>
        throw new NotImplementedException("F3 B1: CellText.SplitValues (2.1).");

    /// <summary>The parts of a <em>Grupy</em> cell: split on <c>;</c>, <c>|</c> and line breaks.</summary>
    public static IReadOnlyList<string> SplitList(string value) =>
        throw new NotImplementedException("F3 B1: CellText.SplitList (2.1).");

    /// <summary>The comma-separated parts of a group cell (the fallback of 2.5).</summary>
    public static IReadOnlyList<string> SplitCommas(string value) =>
        throw new NotImplementedException("F3 B1: CellText.SplitCommas (2.1).");

    /// <summary>
    /// Splits a full name after removing titles (<c>TitleParser.WithoutTitles</c>): given-name-first – the last token is the
    /// surname; surname-first – the first token is the surname; one token → surname only.
    /// </summary>
    public static (string GivenName, string Surname) SplitFullName(string value, NameOrder order) =>
        throw new NotImplementedException("F3 B1: CellText.SplitFullName (2.1).");

    /// <summary>
    /// <see cref="PhoneFormat.International"/>: <c>+48 600 100 200</c> (other countries: <c>+</c> and the digits); text with
    /// letters or fewer than 7 digits is returned unchanged. <see cref="PhoneFormat.AsIs"/> returns the value.
    /// </summary>
    public static string NormalizePhone(string value, PhoneFormat format) =>
        throw new NotImplementedException("F3 B1: CellText.NormalizePhone (2.1).");
}
