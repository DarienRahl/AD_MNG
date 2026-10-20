using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// <see cref="ISecurityEventLookup"/> over the stored records (collector mode, stage B design 7.8): the WEF tailer stores
/// the reduced 4624/4625/4771/4776 of every DC for <c>Retention.LogonRecordsHours</c>, and the lookups are indexed reads
/// of <see cref="ISecurityEventStore.FindAsync"/> (<see cref="SecurityEventCriteria.LogonId"/>, target user name,
/// computer).
/// </summary>
public sealed class StoredSecurityEventLookup(ISecurityEventStore store, ISecurityEventCollector? collector = null) : ISecurityEventLookup
{
    /// <inheritdoc/>
    public bool IsAvailable => collector?.IsAvailable == true;

    /// <summary>4624 rows read for one logon id (logon ids repeat only across reboots).</summary>
    public const int MaxLogonRows = 50;

    /// <inheritdoc/>
    public async Task<SecurityEvent?> FindLogonAsync(string dc, string logonId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dc);
        ArgumentException.ThrowIfNullOrWhiteSpace(logonId);
        if (SecurityEvent.NormalizeLogonId(logonId) is not { } normalized)
        {
            return null;
        }

        var found = await store.FindAsync(new SecurityEventCriteria
        {
            EventCodes = [SecurityEventCatalog.LogonSucceeded],
            Computer = HostNames.Normalize(dc),
            LogonId = normalized,
            From = from,
            To = to,
            Limit = MaxLogonRows,
        }, cancellationToken).ConfigureAwait(false);
        return found.Where(e => HostNames.AreEqual(e.Computer, dc)).OrderBy(e => e.TimeCreated).ThenBy(e => e.RecordId).LastOrDefault();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SecurityEvent>> FindFailuresForAccountAsync(string accountName, IReadOnlyList<int> codes, DateTimeOffset from,
        DateTimeOffset to, IReadOnlyList<string> dcs, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        ArgumentNullException.ThrowIfNull(codes);
        ArgumentNullException.ThrowIfNull(dcs);
        IReadOnlyList<string?> computers = dcs.Count == 0 ? [null] : [.. dcs.Select(HostNames.Normalize).Distinct(StringComparer.Ordinal)];
        var records = new List<SecurityEvent>();
        foreach (var computer in computers)
        {
            records.AddRange(await store.FindAsync(new SecurityEventCriteria
            {
                EventCodes = codes,
                Computer = computer,
                TargetUserName = accountName,
                From = from,
                To = to,
                Limit = limit,
            }, cancellationToken).ConfigureAwait(false));
        }

        return [.. records.OrderBy(e => e.TimeCreated).ThenBy(e => e.RecordId)];
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<SecurityEvent>> FindFailuresAsync(IReadOnlyList<int> codes, DateTimeOffset from, DateTimeOffset to, string? dc, int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(codes);
        return store.FindAsync(new SecurityEventCriteria
        {
            EventCodes = codes,
            Computer = dc is null ? null : HostNames.Normalize(dc),
            From = from,
            To = to,
            Limit = limit,
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<DateTimeOffset?> LastSeenAsync(string dc, int eventCode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dc);
        return store.GetLastSeenAsync(HostNames.Normalize(dc), eventCode, cancellationToken);
    }
}

/// <summary>The default <see cref="ISecurityEventCollector"/>: no WEF collector on this host.</summary>
public sealed class NoSecurityEventCollector : ISecurityEventCollector
{
    /// <inheritdoc/>
    public bool IsAvailable => false;
}
