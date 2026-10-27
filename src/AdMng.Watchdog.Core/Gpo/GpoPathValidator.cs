namespace AdMng.Watchdog.Core.Gpo;

/// <summary>Outcome of <see cref="GpoPathValidator.Validate"/>.</summary>
public enum GpoPathCheck
{
    /// <summary><c>gPCFileSysPath</c> is the expected folder of the domain's SYSVOL.</summary>
    Valid = 0,
    /// <summary>Missing or empty: no content is read; no redirection either (a GPC being created).</summary>
    Missing = 1,
    /// <summary>Another host, share, folder or GUID – the "GPO path hijack" → <c>GroupPolicyPathRedirected</c> (Critical).</summary>
    Redirected = 2,
}

/// <summary>
/// Stage B design 8.2: <c>gPCFileSysPath</c> MUST equal (case-insensitive) <c>\\&lt;domain DNS&gt;\SysVol\&lt;domain DNS&gt;\Policies\{&lt;GUID&gt;}</c>
/// with the GUID of the GPC's <c>cn</c>. Anything else is a redirection, and the content is not read from that path: the
/// service always reads <c>\\&lt;pinned DC&gt;\SYSVOL\&lt;domain DNS&gt;\Policies\{GUID}</c> (<see cref="SysvolGpoFolder"/>).
/// The comparison is exact apart from case – no trailing separator, no <c>.</c>/<c>..</c> segments, no forward slashes,
/// no <c>\\?\</c> or <c>\\.\</c> prefixes, no trailing dot of the domain name, no white space.
/// </summary>
public static class GpoPathValidator
{
    /// <summary>The expected <c>gPCFileSysPath</c> (GUID upper case as GPMC writes it).</summary>
    public static string ExpectedPath(string domainDns, Guid gpoGuid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domainDns);
        var domain = domainDns.Trim().TrimEnd('.');
        return @"\\" + domain + @"\SysVol\" + domain + @"\Policies\" + gpoGuid.ToString("B", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant();
    }

    /// <summary>
    /// Checks <paramref name="gpcFileSysPath"/> against the domain and the GPC's <c>cn</c> (<c>{GUID}</c>). A <c>cn</c>
    /// that is not a GUID in braces makes every path a redirection (the GPC itself is not a valid GPO).
    /// </summary>
    public static GpoPathCheck Validate(string? gpcFileSysPath, string domainDns, string gpcCn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domainDns);
        if (string.IsNullOrEmpty(gpcFileSysPath))
        {
            return GpoPathCheck.Missing;
        }

        if (!TryParseGpcCn(gpcCn, out var guid))
        {
            return GpoPathCheck.Redirected;
        }

        return string.Equals(gpcFileSysPath, ExpectedPath(domainDns, guid), StringComparison.OrdinalIgnoreCase)
            ? GpoPathCheck.Valid
            : GpoPathCheck.Redirected;
    }

    /// <summary>The GUID of a GPC <c>cn</c>: exactly <c>{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}</c>.</summary>
    public static bool TryParseGpcCn(string? cn, out Guid guid) => AdMng.Core.GroupPolicy.GroupPolicyLinks.TryParseGpoId(cn, out guid);
}

/// <summary>
/// Paths of the listing as reported by the server are untrusted (a Samba DC or a hostile listing could report
/// <c>../</c>, absolute paths or control characters): only normalized relative paths below the GPO folder are used.
/// </summary>
public static class GpoRelativePath
{
    /// <summary>Longest accepted relative path (Windows MAX_PATH-style bound, the UNC prefix excluded).</summary>
    public const int MaxLength = 1024;

    /// <summary>
    /// Normalizes <paramref name="path"/> ('\' → '/', no leading/trailing separator) and refuses: empty paths, rooted
    /// paths (<c>/x</c>, <c>\x</c>, <c>C:</c>, UNC), <c>.</c> and <c>..</c> segments, empty segments, control characters,
    /// <c>:</c> (alternate data streams, drive letters), the wildcard and redirection characters <c>* ? " &lt; &gt; |</c>,
    /// segments ending in a space or dot, lone surrogates, and paths longer than <see cref="MaxLength"/>.
    /// </summary>
    public static bool TryNormalize(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrEmpty(path) || path.Length > MaxLength || path[0] is '/' or '\\')
        {
            return false;
        }

        var segments = path.Replace('\\', '/').TrimEnd('/').Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or ".." || segment[^1] is ' ' or '.'
                || segment.Any(c => char.IsControl(c) || c is ':' or '*' or '?' or '"' or '<' or '>' or '|')
                || !IsWellFormed(segment))
            {
                return false;
            }
        }

        normalized = string.Join('/', segments);
        return normalized.Length > 0;
    }

    private static bool IsWellFormed(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Number of folders above the entry (<c>GPT.INI</c> = 0, <c>Machine/Registry.pol</c> = 1).</summary>
    public static int Depth(string normalizedPath) => normalizedPath.Count(c => c == '/');
}
