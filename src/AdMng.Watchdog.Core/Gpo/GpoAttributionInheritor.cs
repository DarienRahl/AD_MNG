using System.Globalization;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Detection;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Watchdog.Core.Gpo;

/// <summary>
/// Attribution of the GPO content kinds (stage B design 8.7). GroupPolicySettingsChanged inherits the attribution of the
/// GroupPolicyChanged event of the same <c>versionNumber</c> transition (GPMC writes the files over SMB, then bumps
/// <c>versionNumber</c> over LDAP as the editing user → 5136 on the GPC), GroupPolicyPathRedirected that of the
/// GroupPolicyChanged event carrying the <c>gPCFileSysPath</c> change – same mechanics as
/// <see cref="AdMng.Watchdog.Core.Attribution.PrivilegeAttributionInheritor"/>: first the event's change set (the content event carries the
/// GroupPolicyChanged event's <c>ChangeSetId</c>), then a GroupPolicyChanged of the same GPO within
/// <see cref="CauseWindow"/> whose <c>versionNumber</c> became the version the content was read for – the newest one;
/// nothing else (better unresolved than the wrong actor). GroupPolicyContentOutOfBand has no LDAP change and SYSVOL access
/// is not audited: Unknown with <c>SYSVOL_NOT_AUDITED</c>.
/// </summary>
public sealed class GpoAttributionInheritor(IEventStore events)
{
    /// <summary>How long before the content event its cause may have been detected: the SYSVOL lag tolerance (2 h, retried
    /// every 2 min) plus the queue (one GPO at a time).</summary>
    public static readonly TimeSpan CauseWindow = TimeSpan.FromHours(3);

    /// <summary>Clock tolerance after the content event.</summary>
    private static readonly TimeSpan After = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The GroupPolicyChanged (or, for a restored GPO, ObjectRestored) event whose attribution <paramref name="contentEvent"/>
    /// inherits, or null. <paramref name="gpcVersion"/> is the GPC <c>versionNumber</c> the content was read for (required for
    /// the search outside the change set).
    /// </summary>
    public async Task<WatchEvent?> FindCauseAsync(WatchEvent contentEvent, int? gpcVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contentEvent);
        if (contentEvent.Kind is not (WatchEventKind.GroupPolicySettingsChanged or WatchEventKind.GroupPolicyPathRedirected))
        {
            return null;
        }

        var redirected = contentEvent.Kind == WatchEventKind.GroupPolicyPathRedirected;
        if (contentEvent.ChangeSetId != Guid.Empty)
        {
            var changeSet = await events.GetChangeSetAsync(contentEvent.ChangeSetId, cancellationToken).ConfigureAwait(false);
            var attribute = redirected ? WatchdogAttributes.GpcFileSysPath : WatchdogAttributes.VersionNumber;
            var cause = changeSet
                .Where(e => e.EventId != contentEvent.EventId && SameObject(e, contentEvent)
                            && e.Kind is WatchEventKind.GroupPolicyChanged or WatchEventKind.ObjectRestored)
                .OrderBy(e => e.Kind != WatchEventKind.GroupPolicyChanged ? 2 : HasChange(e, attribute) ? 0 : 1)
                .FirstOrDefault();
            if (cause is not null)
            {
                return cause;
            }
        }

        if (contentEvent.Object.ObjectGuid is not { } gpoGuid || (!redirected && gpcVersion is null))
        {
            return null;
        }

        var page = await events.QueryAsync(new EventQuery
        {
            ObjectGuid = gpoGuid,
            Kinds = [WatchEventKind.GroupPolicyChanged],
            From = contentEvent.DetectedAt - CauseWindow,
            To = contentEvent.DetectedAt + After,
            Limit = 100,
        }, cancellationToken).ConfigureAwait(false);
        return page.Items
            .Where(e => e.EventId != contentEvent.EventId)
            .Where(e => redirected ? HasChange(e, WatchdogAttributes.GpcFileSysPath) : BecameVersion(e, gpcVersion!.Value))
            .OrderByDescending(e => e.DetectedAt)
            .ThenByDescending(e => e.Id)
            .FirstOrDefault();
    }

    /// <summary>The content event's attribution copied from its cause.</summary>
    public static EventAttribution Inherit(EventAttribution cause, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(cause);
        return cause with { ResolvedAt = cause.ResolvedAt ?? now };
    }

    /// <summary>GroupPolicyContentOutOfBand: Unknown, <c>SYSVOL_NOT_AUDITED</c>.</summary>
    public static EventAttribution OutOfBand(DateTimeOffset now) => new()
    {
        Status = AttributionStatus.Unknown,
        Confidence = AttributionConfidence.None,
        Reason = AttributionReasons.SysvolNotAudited,
        ResolvedAt = now,
    };

    private static bool SameObject(WatchEvent candidate, WatchEvent contentEvent) =>
        contentEvent.Object.ObjectGuid is not { } guid || candidate.Object.ObjectGuid is null || candidate.Object.ObjectGuid == guid;

    private static bool HasChange(WatchEvent e, string attribute) =>
        e.Changes.Any(c => c.Attribute.Equals(attribute, StringComparison.OrdinalIgnoreCase));

    private static bool BecameVersion(WatchEvent e, int version) =>
        e.Changes.Any(c => c.Attribute.Equals(WatchdogAttributes.VersionNumber, StringComparison.OrdinalIgnoreCase)
                           && c.NewValues.Any(v => long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
                                                   && unchecked((int)n) == version));
}

/// <summary>What to do when <c>GPT.INI</c> is behind the GPC (stage B design 8.7, SYSVOL lag).</summary>
public enum SysvolLagDecision
{
    /// <summary>GPT.INI is current: read now.</summary>
    Read = 0,
    /// <summary>GPT.INI is behind: retry after <see cref="SysvolLagPolicy.RetryInterval"/>.</summary>
    Retry = 1,
    /// <summary>Still behind after <c>GpoContent.SysvolLagTolerance</c>: read anyway, health Warning
    /// <c>gpo.&lt;guid&gt;.sysvolLag</c>.</summary>
    ReadWithLagWarning = 2,
}

/// <summary>SYSVOL replication lag (stage B design 8.7): the read waits for <c>GPT.INI</c> to reach the GPC version.</summary>
public static class SysvolLagPolicy
{
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// <paramref name="gptVersion"/> null (GPT.INI missing or unreadable) counts as behind. Behind = either half (computer,
    /// user) of GPT.INI lower than the GPC's (<see cref="GpoVersion.IsBehind"/>) – a GPT.INI ahead of the GPC (SYSVOL
    /// replicated before AD) is read.
    /// </summary>
    public static SysvolLagDecision Decide(int? gptVersion, int gpcVersion, DateTimeOffset firstAttemptAt, DateTimeOffset now, TimeSpan tolerance)
    {
        if (gptVersion is { } gpt && !new GpoVersion(gpt).IsBehind(new GpoVersion(gpcVersion)))
        {
            return SysvolLagDecision.Read;
        }

        return now - firstAttemptAt >= tolerance ? SysvolLagDecision.ReadWithLagWarning : SysvolLagDecision.Retry;
    }

    /// <summary>The health code of a lagging GPO (<c>gpo.&lt;guid&gt;.sysvolLag</c>).</summary>
    public static string HealthCode(Guid gpoGuid) => HealthCheckCodes.Gpo(gpoGuid, HealthCheckCodes.GpoSysvolLag);
}
