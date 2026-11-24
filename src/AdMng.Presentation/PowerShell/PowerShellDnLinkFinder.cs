using System.Text.RegularExpressions;
using AdMng.Core.Ldap;

namespace AdMng.Presentation.PowerShell;

/// <summary>A distinguished name found in a text: where it starts, how long it is and the name itself.</summary>
public readonly record struct PowerShellDnSpan(int Start, int Length, string DistinguishedName);

/// <summary>
/// Finds the distinguished names in the text of a result, so that the window can make each one a link (<i>Open in the
/// program</i>; track R, decision D6). A name is recognised by its shape - components of the usual types ending in the
/// <c>DC=</c> components of a domain - and then checked by <see cref="DistinguishedName.TryParse(string?, out DistinguishedName)"/>;
/// a name the table of the engine had to cut (<c>…</c>), or that goes on in a way the finder does not understand, is
/// not a link: the text before an ellipsis is often another valid name, and a link to the wrong object is worse than none.
/// </summary>
public static partial class PowerShellDnLinkFinder
{
    // A name starts where a word does: not after a letter, a digit, a backslash or a comma (xCN=…, a\CN=…, and never the tail of a
    // longer name) - after an equals sign it is fine, that is how a hashtable prints one (@{DistinguishedName=CN=…}). The leading
    // components end in a comma and hold anything but an unescaped separator of a list (; { } |) or an equals sign (so that a name
    // does not run on into the next one: "…DC=b and CN=C,DC=…"); the trailing ones are the domain components - a domain label has no
    // space or comma, which is what ends the name. A value with an unescaped '=' is no name to this finder (rare, and not a link).
    [GeneratedRegex(
        @"(?<![\p{L}\p{N}_\\,])(?<dn>(?:(?:CN|OU|DC|O|L|ST|C|UID|STREET)=(?:\\.|[^,\\\r\n<>"";{}|=])+,)*DC=[A-Za-z0-9][A-Za-z0-9-]*(?:,DC=[A-Za-z0-9][A-Za-z0-9-]*)*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex Candidate();

    /// <summary>The longest text the finder looks at: a line of a table or a list is far shorter, a wall of text is no place for links.</summary>
    public const int MaxTextLength = 8000;

    /// <summary>
    /// The names in <paramref name="text"/> (one line), in order, not overlapping. Empty for a text without any. With the
    /// <paramref name="width"/> of the engine's formatting, a name is no link where <c>Format-List</c> (or <c>Format-Table -Wrap</c>)
    /// may have wrapped it (audit W-06): it ends with a line as long as the formatting allows (<paramref name="width"/> - 1), or a
    /// line starts with it after blanks only - the continuation of a value; a comma at the very end of the line cuts a name whatever
    /// the width.
    /// </summary>
    public static IReadOnlyList<PowerShellDnSpan> Find(string? text, int width = 0)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxTextLength || !text.Contains('=', StringComparison.Ordinal))
        {
            return [];
        }

        List<PowerShellDnSpan>? found = null;
        try
        {
            foreach (Match match in Candidate().Matches(text))
            {
                var end = match.Index + match.Length;
                if (IsCut(text, end) || IsWrapped(text, match.Index, end, width) || !DistinguishedName.TryParse(match.Value, out _))
                {
                    continue;
                }

                (found ??= []).Add(new PowerShellDnSpan(match.Index, match.Length, match.Value));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // A text that is slow to read is no text of ours: what was found stays, the rest is plain text.
        }

        return found ?? [];
    }

    /// <summary>
    /// The name is not whole: the engine's ellipsis follows it (a cut table cell), or a comma that goes straight on into more text
    /// (the part of a name we do not understand; a comma and a space separate two names of a list).
    /// </summary>
    private static bool IsCut(string text, int end)
    {
        if (end >= text.Length)
        {
            return false;
        }

        var next = text[end];
        if (next == '…')
        {
            return true;
        }

        // A comma that goes on into more text, or one that ends the line (the formatting wrapped the name there, audit W-06).
        return next == ',' && (end + 1 >= text.Length || !char.IsWhiteSpace(text[end + 1]) || text.AsSpan(end + 1).IsWhiteSpace());
    }

    /// <summary>
    /// The name may be one the formatting wrapped (audit W-06): it ends a line as long as the formatting writes one (the width less
    /// one), or it starts an indented line - the continuation of a wrapped value (<c>DC=test</c>, <c>OU=Firma,DC=contoso,DC=test</c>).
    /// </summary>
    private static bool IsWrapped(string text, int start, int end, int width)
    {
        if (start > 0 && text.AsSpan(0, start).IsWhiteSpace())
        {
            return true;
        }

        return width > 1 && end == text.Length && text.Length >= width - 1;
    }
}
