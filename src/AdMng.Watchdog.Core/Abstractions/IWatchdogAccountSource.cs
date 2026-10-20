using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Abstractions;

/// <summary>
/// The service's own directory account (stage B design 9.3, <c>ADMNG-WD$</c>) for the detection: who it is – tag
/// <c>watchdogAccount</c>, exemption from the computer-noise filter, owner/DACL tracked like a critical object – and the
/// keytab tool's last rotation (<c>status.json</c>), which excludes the tool's own password change from the
/// <c>watchdog-self</c> rule. The Linux service implements it next to its own-account monitor (design 4.7); the default
/// registration knows no account (Windows service, no <c>Kerberos.ServicePrincipal</c>).
/// </summary>
public interface IWatchdogAccountSource
{
    /// <summary>The own account as far as known now; null when the host has none.</summary>
    WatchdogAccountIdentity? Identity { get; }

    /// <summary>
    /// The keytab tool's last rotation record, or null when unavailable. When the record still names an older key version
    /// than <paramref name="keyVersionNumber"/> (the tool writes it seconds after the change the detection may already
    /// see), the implementation may wait a bounded time for the new record.
    /// </summary>
    Task<KeytabRotationRecord?> GetRotationAsync(int keyVersionNumber, CancellationToken cancellationToken);
}

/// <summary>The default: no own account (nothing is tagged, nothing excluded).</summary>
public sealed class NoWatchdogAccount : IWatchdogAccountSource
{
    public static NoWatchdogAccount Instance { get; } = new();

    public WatchdogAccountIdentity? Identity => null;

    public Task<KeytabRotationRecord?> GetRotationAsync(int keyVersionNumber, CancellationToken cancellationToken) =>
        Task.FromResult<KeytabRotationRecord?>(null);
}
