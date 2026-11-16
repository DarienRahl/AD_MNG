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

    /// <summary>The names in <paramref name="text"/>, in order, not overlapping. Empty for a text without any.</summary>
    public static IReadOnlyList<PowerShellDnSpan> Find(string? text)
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
                if (IsCut(text, end) || !DistinguishedName.TryParse(match.Value, out _))
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

        return next == ',' && end + 1 < text.Length && !char.IsWhiteSpace(text[end + 1]);
    }
}
