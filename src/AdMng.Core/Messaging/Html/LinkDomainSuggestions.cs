namespace AdMng.Core.Messaging.Html;

/// <summary>
/// The domains offered by "Zezwól na domenę …" for a link outside the allowed domains (design note section 6): the host
/// and its parent domains, most specific first – never a single label, and for a two-letter country TLD with a second level
/// of at most three letters (<c>com.pl</c>, <c>gov.pl</c>, <c>edu.pl</c>, <c>co.uk</c>) nothing shorter than three labels,
/// so an operator cannot allow a whole public suffix by one click. A host of a service shared by many tenants or users
/// (<see cref="SharedDomains"/>: <c>forms.office.com</c>, <c>contoso.sharepoint.com</c>, <c>*.blob.core.windows.net</c>,
/// <c>*.github.io</c> …) is offered as the host only – allowing <c>office.com</c> or <c>sharepoint.com</c> would allow
/// every tenant's forms and sites.
/// </summary>
public static class LinkDomainSuggestions
{
    /// <summary>
    /// Domains under which hosts belong to many tenants or users (curated from the private section of the Public Suffix
    /// List and the forms, file-sharing, hosting and link-shortening services messages typically link to). Only the full
    /// host of a link below one of them is offered.
    /// </summary>
    public static IReadOnlySet<string> SharedDomains { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        // Microsoft 365 and Azure
        "microsoft.com", "office.com", "office365.com", "office.net", "live.com", "outlook.com", "sharepoint.com", "onmicrosoft.com",
        "1drv.ms", "cloud.microsoft", "windows.net", "azurewebsites.net", "cloudapp.net", "azureedge.net",
        "azurefd.net", "azurestaticapps.net", "azure-api.net", "trafficmanager.net", "powerappsportals.com", "microsoftcrmportals.com",
        "dynamics.com",
        // Google
        "google.com", "googleusercontent.com", "googleapis.com", "appspot.com", "web.app", "firebaseapp.com", "forms.gle", "blogspot.com",
        // Code and static hosting
        "github.com", "github.io", "githubusercontent.com", "gitlab.io", "amazonaws.com", "cloudfront.net", "herokuapp.com", "netlify.app",
        "vercel.app", "pages.dev", "workers.dev", "wordpress.com", "wixsite.com", "squarespace.com", "weebly.com", "notion.site",
        // Files, forms, service desks, meetings
        "dropbox.com", "box.com", "wetransfer.com", "typeform.com", "surveymonkey.com", "atlassian.net", "zendesk.com", "force.com",
        "my.site.com", "service-now.com", "zoom.us", "webex.com",
        // Link shorteners (any target)
        "bit.ly", "tinyurl.com", "t.co", "goo.gl", "ow.ly", "linktr.ee",
    };

    /// <summary>The domains to offer for <paramref name="host"/> ("haslo.it.contoso.com.pl" → that host,
    /// "it.contoso.com.pl", "contoso.com.pl"; "forms.office.com" → that host only).</summary>
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

        if (SharedDomains.Any(d => normalized == d || normalized.EndsWith("." + d, StringComparison.Ordinal)))
        {
            return [normalized];
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
