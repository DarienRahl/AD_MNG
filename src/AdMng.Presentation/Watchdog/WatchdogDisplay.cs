using System.Globalization;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Localization;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Presentation.Watchdog;

/// <summary>
/// Localized names of the watchdog's enums and codes (section 20.4 key families "Watchdog.*"): kinds, categories,
/// severities, object kinds, attribution reasons with remedies, health codes, delivery hints, validation codes,
/// delivery stages, outbox states, configuration enums, tags. Every family is complete in both resource files
/// (enforced by tests over the enums and the code lists below); unknown codes fall back to the code or the service's
/// English text.
/// </summary>
public static class WatchdogDisplay
{
    /// <summary>Attribution reason codes (<see cref="AttributionReasons"/>), each with a ".Remedy" text.</summary>
    public static IReadOnlyList<string> ReasonCodes { get; } =
    [
        AttributionReasons.Pending, AttributionReasons.SourceDisabled, AttributionReasons.AuditPolicyMissing,
        AttributionReasons.SaclMissing, AttributionReasons.DcUnreachable, AttributionReasons.LogOverwritten,
        AttributionReasons.Ambiguous, AttributionReasons.NoMatch, AttributionReasons.System, AttributionReasons.SelfChange,
        AttributionReasons.SourceLogonNotFound, AttributionReasons.SourceNotCollected, AttributionReasons.SysvolNotAudited,
    ];

    /// <summary>Reason codes with a collector-mode text (<see cref="AttributionReasons.CollectorVariant"/>, Linux service):
    /// shown instead of the plain text when <see cref="AttributionStatusDto.Source"/> is OpenWec.</summary>
    public static IReadOnlyList<string> CollectorReasonCodes { get; } = [AttributionReasons.DcUnreachable];

    /// <summary>
    /// Health code prefixes with a text and a ".Remedy" (a code is shown with its longest known prefix). Every code the
    /// service reports resolves to one of them (a test scans the service and engine sources).
    /// </summary>
    public static IReadOnlyList<string> HealthCodes { get; } =
    [
        "directory", "directory.connection", "directory.notSynchronized", "directory.computersNotRedirected",
        "permission", "permission.uSNChanged", "permission.accountRestrictions", "permission.replicationMetadata",
        "permission.deletedObjects", "permission.domainPolicy", "permission.passwordSettings", "permission.configurationPartition",
        "permission.search", "eventlog", "eventlog.foreignRecords", "attribution", "attribution.actorConditionsUnreliable",
        "attribution.collector", "attribution.dcDirectory", "attribution.source", "storage", "storage.free", "storage.acl",
        "storage.size", "storage.maintenance", "secret", "secret.unreadable", "runtime", "runtime.outdated", "certificate",
        "certificate.expiry", "certificate.key", "rules", "rules.planner", "rules.configuredDns", "smtp", "channel",

        // stage B (HealthCheckCodes): "wec.<dc>.<check>" and "gpo.<guid>.<check>" resolve by first and last segment
        "serviceAccount", "serviceAccount.rotation", "serviceAccount.passwordAge", "serviceAccount.keytabStale",
        "serviceAccount.keytabOrder", "serviceAccount.privileged", "serviceAccount.drift", "host", "host.clockSkew",
        "host.primaryKdcNotPdc", "host.credentialKey", "host.hardening", "directory.notSealed",
        "permission.tokenGroupsUnreadable", "wec", "wec.silent", "wec.notForwarding", "wec.lag", "wec.identityMismatch",
        "wec.malformedLines", "wec.backlog", "wec.collectorFileLost", "wec.service", "gpo", "gpo.tooLarge", "gpo.parseError",
        "gpo.sysvolLag", "sysvol", "sysvol.accessDenied", "domain", "domain.machineAccountQuota", "storage.backupSkipped",
    ];

    /// <summary>Delivery test hints (<see cref="DeliveryHints"/>).</summary>
    public static IReadOnlyList<string> Hints { get; } =
    [
        DeliveryHints.BasicAuthDisabled, DeliveryHints.SmtpAuthDisabledForMailbox, DeliveryHints.DirectSendRejected,
        DeliveryHints.CertificateUntrusted, DeliveryHints.RevocationUnavailable, DeliveryHints.StartTlsMissing,
        DeliveryHints.RetiredTeamsUrl, DeliveryHints.AcceptedNotDelivered, DeliveryHints.TeamsAuthMismatch,
        DeliveryHints.WebhookGone, DeliveryHints.SecretRebindRequired, DeliveryHints.MessageTooLarge,
    ];

    /// <summary>Validation codes of section 8.3 (errors and warnings).</summary>
    public static IReadOnlyList<string> ValidationCodes { get; } =
    [
        "range", "required", "secretRebindRequired", "duplicateId", "invalidId", "unknownReference", "invalidDn", "invalidSid",
        "invalidAttribute", "schemaUnavailable", "tier0Suppression", "invalidAddress", "invalidHostName", "insecureCredentials",
        "oauthHostNotAllowed", "invalidScope", "insecureUrl", "retiredTeamsUrl", "teamsUrlFormat", "hostNotAllowed", "lockedRule",
        "acceptAnyNotAllowed", "pinnedHash", "timeZone", "language", "exclusionReason", "tooManyWakeUpContainers",
        "secretWithoutChannel", "redactedConfig", "noRecipients", "smtpDisabledButUsed", "exchangeOnlineBasic", "noTls",
        "revocationOff", "tier0Exclusion", "inactiveLow", "pollFast", "rolloutOff",

        // stage B
        "sourceUnsupportedOnHost", "logonRetentionTooShort", "retentionBelowFloor",
    ];

    /// <summary>Event tags (<see cref="EventTags"/>).</summary>
    public static IReadOnlyList<string> Tags { get; } =
    [
        EventTags.Privileged, EventTags.AdminCountOrphan, EventTags.ServiceAccount, EventTags.DefaultContainer,
        EventTags.UnexpectedContainer, EventTags.CreatedByNonAdmin, EventTags.Server, EventTags.DomainController,
        EventTags.OtherDomain, EventTags.Reconciliation, EventTags.Restored, EventTags.LongDisabled, EventTags.DisabledOu,
        EventTags.Krbtgt, EventTags.BuiltInAdministrator, EventTags.SelfChange, EventTags.PasswordReset, EventTags.SyncAccount,
        EventTags.OutOfScope, EventTags.BadSuccessor, EventTags.MachineAccountQuota, EventTags.WatchdogAccount,
        EventTags.GppPassword, EventTags.AuditSettings,
    ];

    /// <summary>Reason codes of exclusion suggestions (<see cref="ExclusionSuggestionDto.ReasonCode"/>).</summary>
    public static IReadOnlyList<string> SuggestionReasons { get; } = ["entraConnectSync", "computerCleanupAccount"];

    /// <summary>How a source (workstation / IP) was obtained (<see cref="Attribution.SourceMethod"/>).</summary>
    public static IReadOnlyList<string> SourceMethods { get; } = ["4624", "4740", "4771", "4776", "dns", "local", "adws"];

    public static string KindKey(WatchEventKind kind) => $"Watchdog.Kind.{kind}";

    public static string CategoryKey(EventCategory category) => $"Watchdog.Category.{category}";

    public static string SeverityKey(Severity severity) => $"Watchdog.Severity.{severity}";

    public static string ObjectKindKey(WatchObjectKind kind) => $"Watchdog.ObjectKind.{kind}";

    public static string ReasonKey(string code) => $"Watchdog.Reason.{code}";

    public static string RemedyKey(string code) => $"Watchdog.Reason.{code}.Remedy";

    public static string HealthKey(string code) => $"Watchdog.Health.{code}";

    public static string HealthRemedyKey(string code) => $"Watchdog.Health.{code}.Remedy";

    public static string HintKey(string hint) => $"Watchdog.Hint.{hint}";

    public static string ValidationKey(string code) => $"Watchdog.Validation.{code}";

    public static string StageKey(DeliveryStage stage) => $"Watchdog.Stage.{stage}";

    public static string OutboxKey(OutboxStatus status) => $"Watchdog.Outbox.{status}";

    public static string StateKey(WatchdogConnectionState state) => $"Watchdog.State.{state}";

    public static string DetailLevelKey(DetailLevel level) => $"Watchdog.DetailLevel.{level}";

    public static string DeliveryHoursKey(DeliveryHours hours) => $"Watchdog.DeliveryHours.{hours}";

    public static string SmtpSecurityKey(SmtpSecurity security) => $"Watchdog.SmtpSecurity.{security}";

    public static string SmtpAuthenticationKey(SmtpAuthentication authentication) => $"Watchdog.SmtpAuthentication.{authentication}";

    public static string TeamsAuthModeKey(TeamsAuthMode mode) => $"Watchdog.TeamsAuthMode.{mode}";

    public static string GroupFilterKey(GroupFilter filter) => $"Watchdog.GroupFilter.{filter}";

    public static string EntraCloudKey(EntraCloud cloud) => $"Watchdog.EntraCloud.{cloud}";

    public static string TagKey(string tag) => $"Watchdog.Tag.{tag}";

    public static string AttributionStatusKey(AttributionStatus status) => $"Watchdog.AttributionStatus.{status}";

    public static string SuggestionKey(string reasonCode) => $"Watchdog.Suggestion.{reasonCode}";

    public static string SourceOriginKey(string method) => $"Watchdog.SourceOrigin.{method}";

    public static string ConfidenceKey(AttributionConfidence confidence) => $"Watchdog.Confidence.{confidence}";

    public static string HealthStateKey(HealthState state) => $"Watchdog.HealthState.{state}";

    public static string ChannelTypeKey(DeliveryChannelType type) => $"Watchdog.ChannelType.{type}";

    public static string DeliveryKindKey(DeliveryKind kind) => $"Watchdog.DeliveryKind.{kind}";

    public static string CertificateValidationKey(CertificateValidationMode mode) => $"Watchdog.CertificateValidation.{mode}";

    public static string SubTabKey(WatchdogSubTab tab) => $"Watchdog.Tab.{tab}";

    public static string RoleKey(WatchdogRole role) => $"Watchdog.Role.{role}";

    public static string ExpiryKindKey(ExpiryKind kind) => $"Watchdog.ExpiryKind.{kind}";

    public static string AttributionSourceKey(AttributionSourceKind source) => $"Watchdog.AttributionSource.{source}";

    /// <summary>Key of a keytab tool result (<see cref="KeytabStatusResults"/>).</summary>
    public static string KeytabResultKey(string result) => $"Watchdog.KeytabResult.{result}";

    /// <summary>Key of a secrets-key variant (<see cref="HostCredentialKeys"/>).</summary>
    public static string CredentialKeyKey(string variant) => $"Watchdog.CredentialKey.{variant}";

    /// <summary>The list icon of an event's object.</summary>
    public static ObjectIcon IconFor(WatchObjectKind kind) => kind switch
    {
        WatchObjectKind.User => ObjectIcon.User,
        WatchObjectKind.Computer or WatchObjectKind.DomainController => ObjectIcon.Computer,
        WatchObjectKind.Group => ObjectIcon.Group,
        WatchObjectKind.OrganizationalUnit => ObjectIcon.OrganizationalUnit,
        WatchObjectKind.Container or WatchObjectKind.Configuration or WatchObjectKind.Schema or WatchObjectKind.GroupPolicy => ObjectIcon.Container,
        WatchObjectKind.Contact => ObjectIcon.Contact,
        WatchObjectKind.ServiceAccount => ObjectIcon.ServiceAccount,
        WatchObjectKind.Domain => ObjectIcon.Domain,
        WatchObjectKind.Trust => ObjectIcon.ForeignPrincipal,
        WatchObjectKind.Watchdog => ObjectIcon.Watchdog,
        _ => ObjectIcon.Unknown,
    };

    /// <summary>The <see cref="DeliveryChannelType"/> of a configured channel.</summary>
    public static DeliveryChannelType ToDeliveryType(ChannelType type) =>
        type == ChannelType.Teams ? DeliveryChannelType.Teams : DeliveryChannelType.Webhook;

    public static string KindName(this ILocalizer localizer, WatchEventKind kind) => Text(localizer, KindKey(kind), kind.ToString());

    public static string CategoryName(this ILocalizer localizer, EventCategory category) => Text(localizer, CategoryKey(category), category.ToString());

    public static string SeverityName(this ILocalizer localizer, Severity severity) => Text(localizer, SeverityKey(severity), severity.ToString());

    public static string WatchObjectKindName(this ILocalizer localizer, WatchObjectKind kind) => Text(localizer, ObjectKindKey(kind), kind.ToString());

    public static string TagName(this ILocalizer localizer, string tag) => Text(localizer, TagKey(tag), tag);

    /// <summary>"OpenWEC (WEF)", or the enum name.</summary>
    public static string AttributionSourceName(this ILocalizer localizer, AttributionSourceKind source) =>
        Text(localizer, AttributionSourceKey(source), source.ToString());

    /// <summary>"OpenWEC (WEF)" for the <see cref="AttributionStatusDto.Source"/> text of a status, or the text itself.</summary>
    public static string AttributionSourceName(this ILocalizer localizer, string? source) =>
        Enum.TryParse<AttributionSourceKind>(source, ignoreCase: true, out var kind) && Enum.IsDefined(kind)
            ? localizer.AttributionSourceName(kind)
            : source ?? string.Empty;

    /// <summary>The service takes the DC Security events from OpenWEC (<see cref="AttributionStatusDto.Source"/>).</summary>
    public static bool IsCollectorSource(string? source) =>
        string.Equals(source, nameof(AttributionSourceKind.OpenWec), StringComparison.OrdinalIgnoreCase);

    /// <summary>Localized keytab tool result, or the value itself (a newer tool).</summary>
    public static string KeytabResultName(this ILocalizer localizer, string result) => Text(localizer, KeytabResultKey(result), result);

    /// <summary>Localized secrets-key variant ("TPM2 + klucz hosta"), or the value itself.</summary>
    public static string CredentialKeyName(this ILocalizer localizer, string variant) => Text(localizer, CredentialKeyKey(variant), variant);

    public static string AttributionStatusName(this ILocalizer localizer, AttributionStatus status) =>
        Text(localizer, AttributionStatusKey(status), status.ToString());

    public static string ConfidenceName(this ILocalizer localizer, AttributionConfidence confidence) =>
        Text(localizer, ConfidenceKey(confidence), confidence.ToString());

    public static string HealthStateName(this ILocalizer localizer, HealthState state) => Text(localizer, HealthStateKey(state), state.ToString());

    public static string StageName(this ILocalizer localizer, DeliveryStage stage) => Text(localizer, StageKey(stage), stage.ToString());

    public static string OutboxStatusName(this ILocalizer localizer, OutboxStatus status) => Text(localizer, OutboxKey(status), status.ToString());

    public static string ChannelTypeName(this ILocalizer localizer, DeliveryChannelType type) => Text(localizer, ChannelTypeKey(type), type.ToString());

    public static string DeliveryKindName(this ILocalizer localizer, DeliveryKind kind) => Text(localizer, DeliveryKindKey(kind), kind.ToString());

    /// <summary>Localized reason ("Brak zasad inspekcji"), the code itself for unknown codes, empty for null.</summary>
    public static string ReasonText(this ILocalizer localizer, string? code) =>
        string.IsNullOrEmpty(code) ? string.Empty : Text(localizer, ReasonKey(code), code);

    /// <summary>One-line remedy of a reason code, or null.</summary>
    public static string? RemedyText(this ILocalizer localizer, string? code)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return string.IsNullOrEmpty(code) || !localizer.Contains(RemedyKey(code)) ? null : localizer[RemedyKey(code)];
    }

    /// <summary>The reason text; in collector mode (Linux service, OpenWEC) the collector variant of a code that has one
    /// (<see cref="CollectorReasonCodes"/>: "DC_UNREACHABLE" reads "collector silent").</summary>
    public static string ReasonText(this ILocalizer localizer, string? code, bool collectorMode) =>
        localizer.ReasonText(CollectorCode(localizer, code, collectorMode));

    /// <summary>The remedy; in collector mode that of the collector variant (<see cref="ReasonText(ILocalizer, string?, bool)"/>).</summary>
    public static string? RemedyText(this ILocalizer localizer, string? code, bool collectorMode) =>
        localizer.RemedyText(CollectorCode(localizer, code, collectorMode));

    /// <summary>The source origin ("z logowania 4624", "z DNS – niepotwierdzone"), or the method code.</summary>
    public static string SourceOriginText(this ILocalizer localizer, string? method) =>
        string.IsNullOrEmpty(method) ? string.Empty : Text(localizer, SourceOriginKey(method), method);

    /// <summary>Localized delivery hint (remedy text), or the hint code.</summary>
    public static string HintText(this ILocalizer localizer, string hint) => Text(localizer, HintKey(hint), hint);

    /// <summary>Localized text of a suggestion reason, or the code.</summary>
    public static string SuggestionText(this ILocalizer localizer, string reasonCode) => Text(localizer, SuggestionKey(reasonCode), reasonCode);

    /// <summary>The known health code that describes <paramref name="code"/> (longest prefix), or null.</summary>
    /// <remarks>"eventlog.dc02.contoso.test" → "eventlog"; "eventlog.dc02.contoso.test.foreignRecords" → "eventlog.foreignRecords".</remarks>
    public static string? HealthPrefix(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        var segments = code.Split('.');
        if (segments.Length > 2 && HealthCodes.Contains($"{segments[0]}.{segments[^1]}", StringComparer.Ordinal))
        {
            return $"{segments[0]}.{segments[^1]}";
        }

        for (var length = segments.Length; length > 0; length--)
        {
            var prefix = string.Join('.', segments.Take(length));
            if (HealthCodes.Contains(prefix, StringComparer.Ordinal))
            {
                return prefix;
            }
        }

        return null;
    }

    /// <summary>
    /// Localized title of a health check; {0} of the text is the part of the code after the known prefix (e.g. the DC of
    /// "eventlog.dc02.contoso.test") or, for a code resolved by its first and last segment, the segments between them
    /// (the DC of "wec.dc01.contoso.com.silent", the GPO of "gpo.&lt;guid&gt;.parseError"). Unknown codes are shown as
    /// they are.
    /// </summary>
    public static string HealthText(this ILocalizer localizer, string code)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(code);
        if (HealthPrefix(code) is not { } prefix)
        {
            return code;
        }

        var subject = code.Length > prefix.Length && code.StartsWith(prefix + ".", StringComparison.Ordinal) ? code[(prefix.Length + 1)..] : string.Empty;
        var segments = code.Split('.');
        if (segments.Length > 2 && string.Equals(prefix, $"{segments[0]}.{segments[^1]}", StringComparison.Ordinal))
        {
            subject = string.Join('.', segments[1..^1]);
        }

        return localizer.Format(HealthKey(prefix), subject);
    }

    /// <summary>One-line remedy of a health code (longest known prefix), or null.</summary>
    public static string? HealthRemedy(this ILocalizer localizer, string code)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return HealthPrefix(code ?? string.Empty) is { } prefix && localizer.Contains(HealthRemedyKey(prefix)) ? localizer[HealthRemedyKey(prefix)] : null;
    }

    /// <summary>Localized text of a validation issue, falling back to the service's English message for unknown codes.</summary>
    public static string ValidationText(this ILocalizer localizer, ConfigIssue issue)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(issue);
        return localizer.Contains(ValidationKey(issue.Code)) ? localizer[ValidationKey(issue.Code)] : issue.Message;
    }

    /// <summary>Absolute local time in the UI culture ("14.10.2026 12:30"), empty for null.</summary>
    public static string FormatTime(this ILocalizer localizer, DateTimeOffset? value, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(zone);
        return value is { } time ? TimeZoneInfo.ConvertTime(time, zone).ToString("g", localizer.Culture) : string.Empty;
    }

    /// <summary>Local time with seconds ("14.10.2026 12:30:05").</summary>
    public static string FormatTimeWithSeconds(this ILocalizer localizer, DateTimeOffset? value, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(zone);
        return value is { } time
            ? TimeZoneInfo.ConvertTime(time, zone).ToString(localizer.Culture.DateTimeFormat.ShortDatePattern + " HH:mm:ss", localizer.Culture)
            : string.Empty;
    }

    /// <summary>A duration as "1,2 s" / "350 ms".</summary>
    public static string FormatDuration(TimeSpan duration, CultureInfo culture) =>
        duration.TotalSeconds >= 1
            ? duration.TotalSeconds.ToString("0.0", culture) + " s"
            : duration.TotalMilliseconds.ToString("0", culture) + " ms";

    private static string? CollectorCode(ILocalizer localizer, string? code, bool collectorMode)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        if (!collectorMode || string.IsNullOrEmpty(code) || !CollectorReasonCodes.Contains(code, StringComparer.Ordinal))
        {
            return code;
        }

        var variant = AttributionReasons.CollectorVariant(code);
        return localizer.Contains(ReasonKey(variant)) ? variant : code;
    }

    private static string Text(ILocalizer localizer, string key, string fallback)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer.Contains(key) ? localizer[key] : fallback;
    }
}
