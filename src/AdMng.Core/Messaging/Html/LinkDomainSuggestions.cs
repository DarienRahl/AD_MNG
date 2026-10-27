namespace AdMng.Core.Messaging.Html;

/// <summary>
/// The domains offered by "Zezwól na domenę …" for a link outside the allowed domains (design note section 6): the host
/// and its parent domains, most specific first – never a single label, and for a two-letter country TLD with a second level
/// of at most three letters (<c>com.pl</c>, <c>gov.pl</c>, <c>edu.pl</c>, <c>co.uk</c>) nothing shorter than three labels,
/// so an operator cannot allow a whole public suffix by one click.
/// </summary>
public static class LinkDomainSuggestions
{
    /// <summary>The domains to offer for <paramref name="host"/> ("haslo.it.contoso.com.pl" → that host,
    /// "it.contoso.com.pl", "contoso.com.pl").</summary>
    public static IReadOnlyList<string> For(string? host)
    {
        var normalized = UserMessageRules.NormalizeDomain(host);
        if (normalized.Length == 0)
        {
            return [];
        }

        var labels = normalized.Split('.');
        if (labels.Length < 2 || labels.Any(l => l.Length == 0))
        {
            return [];
        }

        var minimum = 2;
        var tld = labels[^1];
        if (tld.Length == 2 && labels.Length >= 2 && labels[^2].Length <= 3)
        {
            minimum = 3;
        }

        var result = new List<string>();
        for (var count = labels.Length; count >= minimum; count--)
        {
            result.Add(string.Join('.', labels[^count..]));
        }

        return result;
    }
}
