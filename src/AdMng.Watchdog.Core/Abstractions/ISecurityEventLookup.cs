using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Abstractions;

/// <summary>
/// The logon and failure records attribution looks up (stage B design 7.8): the 4624 of a logon session
/// (<c>LogonSourceResolver</c>), the failed logons of an account around a lockout (<c>LockoutSourceResolver</c>) and the
/// failures of a window (<c>PasswordSprayDetector</c>). In collector mode (<c>AttributionSourceKind.OpenWec</c>) the
/// records were forwarded by the DCs and stored by the WEF tailer (<c>StoredSecurityEventLookup</c>); the Windows
/// service keeps its live queries of the DC logs until stage B section 17 removes them. Host names are DC FQDNs as in
/// <c>System/Computer</c>, compared case-insensitively.
/// </summary>
public interface ISecurityEventLookup
{
    /// <summary>True in collector mode: a WEF collector on this host stores the DCs' records (<see cref="ISecurityEventCollector"/>).
    /// Without it (the Windows service, tests) <c>OpenWec</c> falls back to the live DC-log queries.</summary>
    bool IsAvailable { get; }

    /// <summary>The newest 4624 of <paramref name="dc"/> whose <c>TargetLogonId</c> is <paramref name="logonId"/>
    /// (normalized, <see cref="SecurityEvent.NormalizeLogonId"/>) in <c>[from, to]</c>; null when none.</summary>
    Task<SecurityEvent?> FindLogonAsync(string dc, string logonId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);

    /// <summary>Records of <paramref name="codes"/> with <c>TargetUserName</c> = <paramref name="accountName"/>
    /// (case-insensitive) in <c>[from, to]</c> logged by one of <paramref name="dcs"/> (empty = any DC), oldest first,
    /// at most <paramref name="limit"/> per DC.</summary>
    Task<IReadOnlyList<SecurityEvent>> FindFailuresForAccountAsync(string accountName, IReadOnlyList<int> codes, DateTimeOffset from, DateTimeOffset to,
        IReadOnlyList<string> dcs, int limit, CancellationToken cancellationToken);

    /// <summary>Records of <paramref name="codes"/> in <c>[from, to]</c> of <paramref name="dc"/> (null = every DC),
    /// oldest first, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<SecurityEvent>> FindFailuresAsync(IReadOnlyList<int> codes, DateTimeOffset from, DateTimeOffset to, string? dc, int limit,
        CancellationToken cancellationToken);

    /// <summary>Time of the newest record of <paramref name="eventCode"/> from <paramref name="dc"/>; null when none is
    /// stored (<c>SOURCE_NOT_COLLECTED</c>: no 4624 from that DC in the last hour).</summary>
    Task<DateTimeOffset?> LastSeenAsync(string dc, int eventCode, CancellationToken cancellationToken);
}

/// <summary>
/// The WEF collector feed of the Linux service (stage B design 7.5): available when the service tails OpenWEC files. The
/// attribution engine runs in collector mode (<c>AttributionSourceKind.OpenWec</c>) only when it is
/// (<see cref="CollectorMode"/>); the default (no collector) is never available.
/// </summary>
public interface ISecurityEventCollector
{
    /// <summary>True when the records of the DCs arrive through the collector on this host.</summary>
    bool IsAvailable { get; }
}

/// <summary>
/// Collector mode (stage B design 7.8): attribution source <c>OpenWec</c> on a host with an available WEF collector – the
/// Linux service. Without a collector (the Windows service until section 17, tests) <c>OpenWec</c> – the default of the
/// contracts – reads the DC logs over RPC like <c>DomainControllers</c>.
/// </summary>
public static class CollectorMode
{
    /// <summary>True when the records come from the WEF collector.</summary>
    public static bool IsActive(Contracts.Configuration.AttributionSettings settings, ISecurityEventCollector? collector)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Source == Contracts.Configuration.AttributionSourceKind.OpenWec && collector?.IsAvailable == true;
    }

    /// <summary>The source the engine actually uses: <c>OpenWec</c> without a collector reads the DC logs.</summary>
    public static Contracts.Configuration.AttributionSourceKind Effective(Contracts.Configuration.AttributionSettings settings, ISecurityEventCollector? collector)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Source == Contracts.Configuration.AttributionSourceKind.OpenWec && !IsActive(settings, collector)
            ? Contracts.Configuration.AttributionSourceKind.DomainControllers
            : settings.Source;
    }
}
