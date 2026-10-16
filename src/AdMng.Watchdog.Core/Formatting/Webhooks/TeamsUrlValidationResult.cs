using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Watchdog.Core.Formatting.Webhooks;

/// <summary>Why a Teams workflow URL was not accepted as a standard one (<see cref="TeamsUrlValidationResult.Problem"/>).</summary>
public enum TeamsUrlProblem
{
    /// <summary>A standard Power Automate Workflows URL (<c>*.environment.api.powerplatform.com</c>).</summary>
    None,
    /// <summary>Null, empty or white space (code <c>required</c>).</summary>
    Missing,
    /// <summary>Not an absolute http(s) URL with a host, or it carries user info (code <c>teamsUrlFormat</c>, error).</summary>
    Malformed,
    /// <summary>Not https (http only for loopback with the development flag) (code <c>insecureUrl</c>).</summary>
    Insecure,
    /// <summary>A retired Office 365 connector / Logic Apps URL (code <c>retiredTeamsUrl</c>, hint <c>teams.retiredUrl</c>).</summary>
    Retired,
    /// <summary>Usable host but not the documented Power Automate format (code <c>teamsUrlFormat</c>: warning for SAS,
    /// error for Entra client credentials – the bearer token is sent only to Power Platform hosts).</summary>
    NonStandard,
}

/// <summary>Result of <see cref="TeamsUrlValidator.Validate"/>. Never contains the URL (it is a secret).</summary>
public sealed record TeamsUrlValidationResult
{
    public required TeamsUrlProblem Problem { get; init; }

    /// <summary>Validation code of section 8.3 (<c>ConfigIssue.Code</c>, GUI key <c>Watchdog.Validation.{Code}</c>):
    /// null when valid, else <c>required</c>, <c>teamsUrlFormat</c>, <c>insecureUrl</c> or <c>retiredTeamsUrl</c>.</summary>
    public string? Code { get; init; }

    /// <summary>Null when valid; <see cref="IssueSeverity.Warning"/> only for a non-standard URL in SAS mode.</summary>
    public IssueSeverity? Severity { get; init; }

    /// <summary>English fallback text (<c>ConfigIssue.Message</c>).</summary>
    public string? Message { get; init; }

    /// <summary><see cref="DeliveryHints"/> code for test sends: <see cref="DeliveryHints.RetiredTeamsUrl"/> for retired
    /// URLs, else null.</summary>
    public string? Hint { get; init; }

    /// <summary>The host is a Power Platform environment host (<c>*.environment.api.powerplatform.com</c>, port 443) –
    /// the only kind of host an Entra bearer token may be sent to.</summary>
    public bool IsPowerPlatformHost { get; init; }

    /// <summary>A standard URL (no problem at all).</summary>
    public bool IsValid => Problem == TeamsUrlProblem.None;

    /// <summary>Valid or only a warning – the configuration can be saved.</summary>
    public bool IsAccepted => Severity != IssueSeverity.Error;

    public override string ToString() => $"TeamsUrlValidationResult({Problem})";
}
