using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// The one exclusion of the <c>watchdog-self</c> rule (stage B design 9.3): a password change of the service's own
/// account is the keytab tool's planned rotation when its new <c>msDS-KeyVersionNumber</c> equals <c>status.json.kvno</c>,
/// its <c>pwdLastSet</c> lies within ±<see cref="Tolerance"/> of <c>status.json.lastChange</c> and the result is
/// <c>ok</c> or <c>replicationPending</c> – regardless of the attributed actor (who the DC records for a kpasswd change of
/// a computer account is unverified, Q21). Any other password change of the account (an admin reset in the one-time
/// password path included) stays Critical.
/// </summary>
public static class WatchdogSelfRotation
{
    /// <summary>Allowed distance between pwdLastSet and the tool's recorded change time.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(10);

    /// <summary>True when the change (<paramref name="keyVersionNumber"/>, <paramref name="passwordLastSet"/>) is the
    /// rotation <paramref name="record"/> describes.</summary>
    public static bool IsPlanned(KeytabRotationRecord? record, int? keyVersionNumber, DateTimeOffset? passwordLastSet) =>
        record is { Kvno: { } kvno, LastChange: { } lastChange, Result: { } result }
        && (result == KeytabStatusResults.Ok || result == KeytabStatusResults.ReplicationPending)
        && keyVersionNumber == kvno
        && passwordLastSet is { } changedAt
        && (changedAt - lastChange).Duration() <= Tolerance;
}
