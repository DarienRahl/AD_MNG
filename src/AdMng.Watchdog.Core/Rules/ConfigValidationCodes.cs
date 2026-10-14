namespace AdMng.Watchdog.Core.Rules;

/// <summary>
/// Stable codes of <see cref="Contracts.Api.ConfigIssue.Code"/> (section 8.3). The GUI localizes
/// <c>Watchdog.Validation.{Code}</c>; the service returns them in <c>extensions.issues</c> of a 400 response.
/// </summary>
public static class ConfigValidationCodes
{
    // errors
    public const string Range = "range";
    public const string Required = "required";
    public const string SecretRebindRequired = "secretRebindRequired";
    public const string DuplicateId = "duplicateId";
    public const string InvalidId = "invalidId";
    public const string UnknownReference = "unknownReference";
    public const string InvalidDn = "invalidDn";
    public const string InvalidSid = "invalidSid";
    public const string InvalidAttribute = "invalidAttribute";
    public const string Tier0Suppression = "tier0Suppression";
    public const string InvalidAddress = "invalidAddress";
    public const string InvalidHostName = "invalidHostName";
    public const string InsecureCredentials = "insecureCredentials";
    public const string OAuthHostNotAllowed = "oauthHostNotAllowed";
    public const string InvalidScope = "invalidScope";
    public const string InsecureUrl = "insecureUrl";
    public const string RetiredTeamsUrl = "retiredTeamsUrl";
    public const string TeamsUrlFormat = "teamsUrlFormat";
    public const string HostNotAllowed = "hostNotAllowed";
    public const string LockedRule = "lockedRule";
    public const string AcceptAnyNotAllowed = "acceptAnyNotAllowed";
    public const string PinnedHash = "pinnedHash";
    public const string TimeZone = "timeZone";
    public const string Language = "language";
    public const string ExclusionReason = "exclusionReason";
    public const string TooManyWakeUpContainers = "tooManyWakeUpContainers";
    public const string SecretWithoutChannel = "secretWithoutChannel";
    public const string RedactedConfig = "redactedConfig";

    /// <summary>Set by the service (live schema check of <c>additionalWatchedAttributes</c> without a session).</summary>
    public const string SchemaUnavailable = "schemaUnavailable";

    // warnings
    public const string NoRecipients = "noRecipients";
    public const string SmtpDisabledButUsed = "smtpDisabledButUsed";
    public const string ExchangeOnlineBasic = "exchangeOnlineBasic";
    public const string NoTls = "noTls";
    public const string RevocationOff = "revocationOff";
    public const string Tier0Exclusion = "tier0Exclusion";
    public const string InactiveLow = "inactiveLow";
    public const string PollFast = "pollFast";
    public const string RolloutOff = "rolloutOff";
}
