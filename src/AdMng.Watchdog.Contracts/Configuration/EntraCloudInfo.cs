using System.Globalization;

namespace AdMng.Watchdog.Contracts.Configuration;

/// <summary>Fixed token authority host, SMTP scope and accepted Power Automate scopes per <see cref="EntraCloud"/>
/// (no free-text authority or scope anywhere in the configuration).</summary>
public static class EntraCloudInfo
{
    private static readonly string[] PublicTeamsScopes =
        ["https://service.flow.microsoft.com//.default", "https://service.flow.microsoft.com/.default"];

    private static readonly string[] UsGovernmentTeamsScopes =
    [
        "https://gov.service.flow.microsoft.us//.default", "https://gov.service.flow.microsoft.us/.default",
        "https://high.service.flow.microsoft.us//.default", "https://high.service.flow.microsoft.us/.default",
    ];

    private static readonly string[] ChinaTeamsScopes =
        ["https://service.powerautomate.cn//.default", "https://service.powerautomate.cn/.default"];

    /// <summary>https://&lt;host&gt;/&lt;tenant GUID&gt;/v2.0.</summary>
    /// <exception cref="ArgumentException"><paramref name="tenantId"/> is not a GUID (no tenant names or paths).</exception>
    public static string Authority(EntraCloud cloud, string tenantId)      // https://<host>/<tenant GUID>/v2.0
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        if (!Guid.TryParse(tenantId, out var tenant))
        {
            throw new ArgumentException("The tenant id must be a GUID.", nameof(tenantId));
        }

        return string.Create(CultureInfo.InvariantCulture, $"https://{AuthorityHost(cloud)}/{tenant:D}/v2.0");
    }

    public static string SmtpScope(EntraCloud cloud) => cloud switch
    {
        EntraCloud.Public => "https://outlook.office365.com/.default",
        EntraCloud.UsGovernment => "https://outlook.office365.us/.default",
        EntraCloud.China => "https://partner.outlook.cn/.default",
        _ => throw new ArgumentOutOfRangeException(nameof(cloud), cloud, "Unknown Entra cloud."),
    };

    /// <summary>Accepted Teams trigger scopes; Public: "https://service.flow.microsoft.com//.default",
    /// "https://service.flow.microsoft.com/.default"; UsGovernment: the gov.service.flow / high.service.flow variants;
    /// China: "https://service.powerautomate.cn//.default" and the single-slash form.</summary>
    public static IReadOnlyList<string> TeamsScopes(EntraCloud cloud) => cloud switch
    {
        EntraCloud.Public => PublicTeamsScopes,
        EntraCloud.UsGovernment => UsGovernmentTeamsScopes,
        EntraCloud.China => ChinaTeamsScopes,
        _ => throw new ArgumentOutOfRangeException(nameof(cloud), cloud, "Unknown Entra cloud."),
    };

    private static string AuthorityHost(EntraCloud cloud) => cloud switch
    {
        EntraCloud.Public => "login.microsoftonline.com",
        EntraCloud.UsGovernment => "login.microsoftonline.us",
        EntraCloud.China => "login.partner.microsoftonline.cn",
        _ => throw new ArgumentOutOfRangeException(nameof(cloud), cloud, "Unknown Entra cloud."),
    };
}
