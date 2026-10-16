using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Core.Formatting.Webhooks;

/// <summary>
/// Checks a Microsoft Teams channel URL (Power Automate Workflows trigger, sections 8.3 and 15.7). Standard form:
/// <c>https://&lt;env&gt;.environment.api.powerplatform.com[:443]/powerautomate/automations/direct/[…/]workflows/…</c>
/// (any length – real URLs exceed 4 KB with their SAS query). Retired Office 365 connector URLs
/// (<c>*.webhook.office.com</c>, <c>outlook.office(365).com/webhook…</c>) and Logic Apps URLs (<c>*.logic.azure.com</c>)
/// are rejected with their own code (case-insensitive, also with a trailing dot). The URL itself never appears in a
/// result, message or exception.
/// </summary>
public static class TeamsUrlValidator
{
    /// <summary>Host suffix of Power Platform environments; Entra bearer tokens are sent only to such hosts.</summary>
    public const string PowerPlatformHostSuffix = ".environment.api.powerplatform.com";

    private const string WorkflowPathPrefix = "/powerautomate/automations/direct/";

    /// <summary>Validates <paramref name="url"/> for a channel with <paramref name="authMode"/>.</summary>
    /// <param name="url">The URL (e.g. <c>SecretField.NewValue</c> or the decrypted stored URL).</param>
    /// <param name="authMode">SAS: a non-standard host is a warning; EntraClientCredentials: an error.</param>
    /// <param name="allowInsecureLoopback">Development only (<c>ConfigValidationContext.IsDevelopment</c>): accept
    /// <c>http://localhost</c> / <c>127.0.0.1</c> / <c>[::1]</c> (still non-standard: a SAS warning, an OAuth error).</param>
    public static TeamsUrlValidationResult Validate(string? url, TeamsAuthMode authMode, bool allowInsecureLoopback = false)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return Error(TeamsUrlProblem.Missing, "required", "The Teams workflow URL is required.");
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.IsFile || string.IsNullOrEmpty(uri.Host))
        {
            return Error(TeamsUrlProblem.Malformed, "teamsUrlFormat", "The Teams workflow URL is not a valid absolute https URL.");
        }

        var host = uri.IdnHost.ToLowerInvariant();

        // "host." names the same DNS host as "host" – a trailing dot must not dodge the retired check. (The Power Platform
        // check below stays exact: a trailing dot there is non-standard, which fails closed for the bearer token.)
        if (IsRetired(host.TrimEnd('.'), uri.AbsolutePath))
        {
            return Error(
                TeamsUrlProblem.Retired,
                "retiredTeamsUrl",
                "Office 365 connector and Logic Apps URLs (*.webhook.office.com, *.logic.azure.com) are retired – create a Power Automate "
                + "Workflows trigger ('When a Teams webhook request is received') and use its *.environment.api.powerplatform.com URL.",
                DeliveryHints.RetiredTeamsUrl);
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            return Error(TeamsUrlProblem.Insecure, "insecureUrl", "The Teams workflow URL must use https.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return Error(TeamsUrlProblem.Malformed, "teamsUrlFormat", "The Teams workflow URL must not contain user information.");
        }

        if (uri.Scheme != Uri.UriSchemeHttps && !(allowInsecureLoopback && uri.IsLoopback))
        {
            return Error(TeamsUrlProblem.Insecure, "insecureUrl", "The Teams workflow URL must use https.");
        }

        var powerPlatformHost = uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 && IsPowerPlatformHost(host);
        if (powerPlatformHost && IsWorkflowPath(uri.AbsolutePath))
        {
            return new TeamsUrlValidationResult { Problem = TeamsUrlProblem.None, IsPowerPlatformHost = true };
        }

        return authMode == TeamsAuthMode.EntraClientCredentials
            ? new TeamsUrlValidationResult
            {
                Problem = TeamsUrlProblem.NonStandard,
                Code = "teamsUrlFormat",
                Severity = IssueSeverity.Error,
                IsPowerPlatformHost = powerPlatformHost,
                Message = "Entra client credentials require a Power Automate Workflows URL "
                          + "(https://…environment.api.powerplatform.com/powerautomate/automations/direct/…/workflows/…) – "
                          + "the bearer token is sent only to Power Platform hosts.",
            }
            : new TeamsUrlValidationResult
            {
                Problem = TeamsUrlProblem.NonStandard,
                Code = "teamsUrlFormat",
                Severity = IssueSeverity.Warning,
                IsPowerPlatformHost = powerPlatformHost,
                Message = "The URL is not a standard Power Automate Workflows URL "
                          + "(https://…environment.api.powerplatform.com/powerautomate/automations/direct/…/workflows/…).",
            };
    }

    /// <summary>True for <c>&lt;label(s)&gt;.environment.api.powerplatform.com</c> (case-insensitive; pass
    /// <see cref="Uri.IdnHost"/>). Host only – anything that is not an ASCII host name (user info, port, path, query,
    /// fragment, white space, non-ASCII, trailing dot) is false. Before attaching a bearer token (section 15.7) use
    /// <see cref="Validate"/>(url, <see cref="TeamsAuthMode.EntraClientCredentials"/>), which also requires https,
    /// port 443, no user info and the workflow path.</summary>
    public static bool IsPowerPlatformHost(string? host)
    {
        if (string.IsNullOrEmpty(host) || !host.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.'))
        {
            return false;
        }

        return host.Length > PowerPlatformHostSuffix.Length
               && host.EndsWith(PowerPlatformHostSuffix, StringComparison.OrdinalIgnoreCase)
               && host[^(PowerPlatformHostSuffix.Length + 1)] != '.';
    }

    private static bool IsRetired(string host, string path) =>
        IsHostOrSubdomain(host, "webhook.office.com")
        || IsHostOrSubdomain(host, "logic.azure.com")
        || ((host is "outlook.office.com" or "outlook.office365.com")
            && path.StartsWith("/webhook", StringComparison.OrdinalIgnoreCase));

    private static bool IsHostOrSubdomain(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    // ^/powerautomate/automations/direct/(.*/)?workflows/ (the "cu/NN/" and other intermediate segments are allowed).
    private static bool IsWorkflowPath(string path)
    {
        if (!path.StartsWith(WorkflowPathPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = path.AsSpan(WorkflowPathPrefix.Length);
        return rest.StartsWith("workflows/", StringComparison.Ordinal) || rest.IndexOf("/workflows/", StringComparison.Ordinal) >= 0;
    }

    private static TeamsUrlValidationResult Error(TeamsUrlProblem problem, string code, string message, string? hint = null) => new()
    {
        Problem = problem,
        Code = code,
        Severity = IssueSeverity.Error,
        Message = message,
        Hint = hint,
    };
}
