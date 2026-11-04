namespace AdMng.Core.EventLogs;

/// <summary>
/// The rules that keep values and names of a Windows event query XPath from changing the query (CLAUDE.md rule 3;
/// design note e-event-logs.md 2; shared with the Watchdog's <c>SecurityEventQueryBuilder</c>). XPath 1.0 string
/// literals have no escape mechanism, so a value is one literal: single-quoted, double-quoted when it contains an
/// apostrophe (<c>o'neill</c>), and rejected when it contains both quote characters or a control character. Letters with
/// diacritics (<c>józef.żak</c>) are allowed. Names (log names, data names, suppressed attributes) are restricted to
/// <c>[0-9A-Za-z.\-_:{}$ ]</c>. The XML around the expression is escaped by the caller's <c>XmlWriter</c>.
/// </summary>
public static class XPathLiteral
{
    /// <summary>The longest value a literal may hold.</summary>
    public const int MaxValueLength = 1024;

    /// <summary>True when <paramref name="value"/> can be a data filter value: not empty, at most
    /// <see cref="MaxValueLength"/> characters, no control character, and not both quote characters (it must fit one
    /// XPath string literal).</summary>
    public static bool IsAllowedValue(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= MaxValueLength && !value.Any(char.IsControl)
        && !(value.Contains('\'') && value.Contains('"'));

    /// <summary>True when <paramref name="name"/> may be a log name, data name or suppressed attribute
    /// (<c>[0-9A-Za-z.\-_:{}$ ]</c>, not empty).</summary>
    public static bool IsAllowedName(string? name) =>
        !string.IsNullOrEmpty(name) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':' or '{' or '}' or '$' or ' ');

    /// <summary><paramref name="value"/> as one XPath string literal (single-quoted, or double-quoted when it contains an
    /// apostrophe).</summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not <see cref="IsAllowedValue">allowed</see>; the
    /// message carries no part of the value.</exception>
    public static string Quote(string value)
    {
        if (!IsAllowedValue(value))
        {
            throw new ArgumentException("A filter value is empty, too long, contains a control character or both quote characters.", nameof(value));
        }

        return value.Contains('\'') ? "\"" + value + "\"" : "'" + value + "'";
    }
}
