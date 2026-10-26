using System.Globalization;
using System.Text.RegularExpressions;
using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Service.Secrets;

/// <summary>
/// Hints of stored URL secrets (spec 17.2 step 3, 18): <c>scheme://host[:port]/…</c> – never a path segment, query,
/// fragment or user info. Many webhook services put the credential in the first path segment (webhook.site, ntfy.sh),
/// and the hint is stored in the configuration document and returned to every Admin. The host stays: the validator
/// (8.3) reads the declared destination of a stored URL from it (<c>SecretBindings.DeclaredUrlHost</c>). The function is
/// idempotent, so it also repairs hints stored before this rule (e.g. <c>https://webhook.site/&lt;uuid&gt;</c>).
/// </summary>
public static partial class SecretHints
{
    private const string Ellipsis = "…";

    /// <summary>The safe hint of a URL or of an existing hint; null when <paramref name="urlOrHint"/> is neither.</summary>
    public static string? Url(string? urlOrHint)
    {
        var text = urlOrHint?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (SafeHint().IsMatch(text))
        {
            return text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        var port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
        var rest = uri.AbsolutePath.Trim('/').Length > 0 || uri.Query.Length > 1 || uri.Fragment.Length > 1 ? "/" + Ellipsis : string.Empty;
        return $"{uri.Scheme}://{uri.IdnHost}{port}{rest}";
    }

    /// <summary><paramref name="configuration"/> with every channel URL hint made safe; the same instance when nothing
    /// changed (the configuration store repairs documents written before the rule).</summary>
    public static WatchdogConfiguration Sanitize(WatchdogConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var channels = configuration.Channels ?? [];
        if (channels.All(c => c?.Url?.Hint is not { } hint || string.Equals(Url(hint), hint, StringComparison.Ordinal)))
        {
            return configuration;
        }

        return configuration with
        {
            Channels = [.. channels.Select(c => c is { Url.Hint: { } hint } ? c with { Url = c.Url with { Hint = Url(hint) } } : c!)],
        };
    }

    /// <summary>A hint in the safe form: scheme, host (and port), nothing or "/…".</summary>
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]*://[^/?#@\s…]+(?:/…)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeHint();
}
