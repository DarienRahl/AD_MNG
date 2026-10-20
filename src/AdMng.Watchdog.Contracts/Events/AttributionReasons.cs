namespace AdMng.Watchdog.Contracts.Events;

/// <summary>Reason codes of <see cref="Attribution.Reason"/> and <see cref="Attribution.SourceReason"/>.</summary>
public static class AttributionReasons
{
    public const string Pending = "PENDING";
    public const string SourceDisabled = "SOURCE_DISABLED";          // attribution off / non-Windows host
    public const string AuditPolicyMissing = "AUDIT_POLICY_MISSING"; // no events of that family from that DC in 7 days
    public const string SaclMissing = "SACL_MISSING";                // non-SAM attribute, never saw a 5136
    public const string DcUnreachable = "DC_UNREACHABLE";            // RPC / access denied
    public const string LogOverwritten = "LOG_OVERWRITTEN";          // oldest record newer than the change
    public const string Ambiguous = "AMBIGUOUS";
    public const string NoMatch = "NO_MATCH";                        // window searched, nothing matched
    public const string System = "SYSTEM";                           // actor is the DC itself (S-1-5-18 / DC$)
    public const string SelfChange = "SELF_CHANGE";                  // computer changed its own object
    // source-level reasons (Attribution.SourceReason)
    public const string SourceLogonNotFound = "SOURCE_LOGON_NOT_FOUND"; // 4624 not found within LogonLookback / overwritten
    public const string SourceNotCollected = "SOURCE_NOT_COLLECTED";   // ForwardedEvents mode without 4624/4771/4776 in the subscription; collector mode: no 4624 from that DC in the last hour

    /// <summary>GroupPolicyContentOutOfBand: SYSVOL files changed without a versionNumber change – SYSVOL access is not
    /// audited, the actor stays unknown (stage B design 8.7).</summary>
    public const string SysvolNotAudited = "SYSVOL_NOT_AUDITED";

    /// <summary>
    /// The localization variant of <paramref name="code"/> in collector mode (Linux service, OpenWEC – stage B design 7.8):
    /// the texts <c>Reason.{code}.Collector</c> and <c>Reason.{code}.Collector.Remedy</c> (GUI: <c>Watchdog.Reason.…</c>).
    /// The stored reason code itself never changes. Exists for <see cref="DcUnreachable"/> ("collector silent").
    /// </summary>
    public static string CollectorVariant(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return code + ".Collector";
    }
}
