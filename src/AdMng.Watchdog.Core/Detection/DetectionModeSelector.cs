using AdMng.Core.Ldap;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// Decides the mode of a cycle (section 10.3, first match wins): Baseline without a completed baseline; skip while the
/// DC is not synchronized; Failback to a known DC identity whose own watermark is still valid; Resync for a never-seen
/// DC, an invocationId change (restore) or a USN rollback; the daily Reconciliation after
/// <c>FullReconciliationTime</c> (local time); NoChange when H = W; otherwise Incremental.
/// </summary>
public sealed class DetectionModeSelector
{
    public const string ReasonBaseline = "baseline";
    public const string ReasonNotSynchronized = "notSynchronized";
    public const string ReasonFailback = "failback";
    public const string ReasonDcChanged = "dcChanged";
    public const string ReasonInvocationIdChanged = "invocationIdChanged";
    public const string ReasonUsnRollback = "usnRollback";
    public const string ReasonReconciliation = "reconciliation";

    /// <param name="state">Stored DC state (null before the first baseline).</param>
    /// <param name="known">Stored watermark of the current DC identity when it differs from the stored DC.</param>
    /// <param name="dc">What the DC reports now.</param>
    /// <param name="now">Current time.</param>
    /// <param name="timeZone">Configured time zone (reconciliation time is local).</param>
    /// <param name="reconciliationTime">Directory.FullReconciliationTime.</param>
    public ModeDecision Select(DcState? state, DcWatermark? known, DcSnapshot dc, DateTimeOffset now, TimeZoneInfo timeZone, TimeOnly reconciliationTime)
    {
        ArgumentNullException.ThrowIfNull(dc);
        ArgumentNullException.ThrowIfNull(timeZone);
        if (state?.BaselineCompletedAt is null)
        {
            return new ModeDecision(DetectionMode.Baseline, false, ReasonBaseline, 0);
        }

        if (!dc.IsSynchronized)
        {
            return new ModeDecision(DetectionMode.NoChange, true, ReasonNotSynchronized, state.HighestUsn);
        }

        if (!DistinguishedName.AreEqual(state.DsServiceName, dc.DsServiceName))
        {
            return known is not null && known.InvocationId == dc.InvocationId && dc.HighestUsn >= known.HighestUsn
                ? new ModeDecision(DetectionMode.Failback, false, ReasonFailback, known.HighestUsn)
                : new ModeDecision(DetectionMode.Resync, false, ReasonDcChanged, state.HighestUsn);
        }

        if (state.InvocationId != dc.InvocationId)
        {
            return new ModeDecision(DetectionMode.Resync, false, ReasonInvocationIdChanged, state.HighestUsn);
        }

        if (dc.HighestUsn < state.HighestUsn)
        {
            return new ModeDecision(DetectionMode.Resync, false, ReasonUsnRollback, state.HighestUsn);
        }

        if (IsReconciliationDue(state.LastFullSyncAt, now, timeZone, reconciliationTime))
        {
            return new ModeDecision(DetectionMode.Reconciliation, false, ReasonReconciliation, state.HighestUsn);
        }

        return dc.HighestUsn == state.HighestUsn
            ? new ModeDecision(DetectionMode.NoChange, false, null, state.HighestUsn)
            : new ModeDecision(DetectionMode.Incremental, false, null, state.HighestUsn);
    }

    /// <summary>Local time is past today's reconciliation time and the last full sync is older than that moment.</summary>
    public static bool IsReconciliationDue(DateTimeOffset? lastFullSyncAt, DateTimeOffset now, TimeZoneInfo timeZone, TimeOnly reconciliationTime)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var local = TimeZoneInfo.ConvertTime(now, timeZone);
        var todayAt = local.Date + reconciliationTime.ToTimeSpan();
        if (timeZone.IsInvalidTime(todayAt))
        {
            todayAt = todayAt.AddHours(1);
        }

        var moment = new DateTimeOffset(todayAt, timeZone.GetUtcOffset(todayAt));
        return now >= moment && (lastFullSyncAt is null || lastFullSyncAt < moment);
    }
}
