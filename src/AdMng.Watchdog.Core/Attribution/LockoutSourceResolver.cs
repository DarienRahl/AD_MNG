using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// Where the bad passwords of a lockout came from (section 11.4 #6b, AccountLockedOut – the actor stays
/// NotApplicable): the 4740 caller (client-supplied, may be empty or a front-end server) from the ingested records;
/// when it is empty or a DC and <c>LookupLockoutSources</c> is on, the PDC emulator and the DC that logged the 4740 are
/// queried (bounded like 11.4 #6 through <see cref="SourceQueryGate"/>) for 4771 Status 0x18, 4776 Status 0xC000006A
/// and 4625 with <c>TargetUserName</c> = sAMAccountName in <c>[lockoutTime − effective observation window,
/// lockoutTime]</c>; results are aggregated per address/workstation and event id, top <see cref="MaxSources"/> by count.
/// Account names with diacritics or an apostrophe are queried too (an XPath literal holds them). The resolution is
/// incomplete (<see cref="LockoutResolution.Complete"/>) while no 4740 was found or a query did not finish.
/// </summary>
/// <remarks>The "known server" caller case of the specification (tag <c>server</c> in the snapshot) is not detected –
/// the snapshot cannot be searched by computer name through the store abstraction; such callers are reported as is.</remarks>
/// <remarks>Collector mode (<see cref="CollectorMode"/>, stage B design 7.8): the failures come from the stored records of the same DCs
/// (<see cref="ISecurityEventLookup.FindFailuresForAccountAsync"/>, one bounded read per DC through the gate).</remarks>
public sealed class LockoutSourceResolver(ISecurityEventStore store, ISecurityEventSource source, SourceQueryGate gate,
    IDomainControllerCatalog domainControllers, ILockoutPolicySource lockoutPolicy, ISecurityEventLookup? lookup = null)
{
    /// <summary>Sources kept per lockout (by count).</summary>
    public const int MaxSources = 10;

    /// <summary>Failed-logon records read per DC and lockout.</summary>
    public const int MaxRecords = 1000;

    private static readonly int[] FailureCodes =
        [SecurityEventCatalog.KerberosPreAuthFailed, SecurityEventCatalog.CredentialValidation, SecurityEventCatalog.LogonFailed];

    /// <summary>The caller and sources of <paramref name="lockoutEvent"/>.</summary>
    public async Task<LockoutResolution> ResolveAsync(WatchEvent lockoutEvent, AttributionSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lockoutEvent);
        ArgumentNullException.ThrowIfNull(settings);
        var lockedAt = lockoutEvent.Lockout?.LockedAt ?? lockoutEvent.OriginatingTime ?? lockoutEvent.DetectedAt;
        var record = await Find4740Async(lockoutEvent, lockedAt, settings, cancellationToken).ConfigureAwait(false);
        var caller = record?.Get(SecurityEventParser.CallerComputerName);
        var sources = new List<LockoutSource>();
        var complete = record is not null;
        if (caller is not null)
        {
            sources.Add(new LockoutSource(null, caller, 1, SecurityEventCatalog.AccountLockedOut));
        }

        if (settings.LookupLockoutSources && lockoutEvent.Object.SamAccountName is { } sam && SecurityEventQueryBuilder.IsAllowedValue(sam)
            && await NeedsLookupAsync(caller, cancellationToken).ConfigureAwait(false))
        {
            var window = await lockoutPolicy.GetObservationWindowAsync(lockoutEvent.Object, cancellationToken).ConfigureAwait(false);
            var (failures, finished) = await QueryFailuresAsync(record, sam, lockedAt - window, lockedAt, settings, cancellationToken).ConfigureAwait(false);
            sources.AddRange(Aggregate(failures));
            complete &= finished;
        }

        var lockout = (lockoutEvent.Lockout ?? new LockoutInfo { LockedAt = lockedAt }) with
        {
            CallerComputer = caller ?? lockoutEvent.Lockout?.CallerComputer,
            Sources = [.. sources.OrderByDescending(s => s.Count).ThenBy(s => s.EventCode).Take(MaxSources)],
        };
        IReadOnlyList<EvidenceRef> evidence = record is null
            ? []
            : [new EvidenceRef { Dc = record.Computer, EventCode = record.EventCode, RecordId = record.RecordId, TimeCreated = record.TimeCreated }];
        return new LockoutResolution(lockout, evidence, complete);
    }

    private async Task<SecurityEvent?> Find4740Async(WatchEvent lockoutEvent, DateTimeOffset lockedAt, AttributionSettings settings, CancellationToken cancellationToken)
    {
        if (lockoutEvent.Object.Sid is not { Length: > 0 } sid)
        {
            return null;
        }

        var found = await store.FindAsync(new SecurityEventCriteria
        {
            EventCodes = [SecurityEventCatalog.AccountLockedOut],
            TargetSid = sid,
            From = lockedAt - settings.ReplicationLatencyBound,
            To = lockedAt + settings.FallbackWindow,
            Limit = 20,
        }, cancellationToken).ConfigureAwait(false);
        return found.OrderBy(e => (e.TimeCreated - lockedAt).Duration()).FirstOrDefault();
    }

    private async Task<bool> NeedsLookupAsync(string? caller, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(caller))
        {
            return true;
        }

        var dcs = await domainControllers.GetKnownDomainControllersAsync(cancellationToken).ConfigureAwait(false);
        return dcs.Any(dc => string.Equals(dc.Split('.')[0], caller.Trim().TrimEnd('$'), StringComparison.OrdinalIgnoreCase)
            || HostNames.AreEqual(dc, caller));
    }

    private async Task<(List<SecurityEvent> Failures, bool Finished)> QueryFailuresAsync(SecurityEvent? lockoutRecord, string sam, DateTimeOffset from, DateTimeOffset to,
        AttributionSettings settings, CancellationToken cancellationToken)
    {
        var dcs = new List<string>();
        if (await domainControllers.GetPdcHostAsync(cancellationToken).ConfigureAwait(false) is { } pdc)
        {
            dcs.Add(HostNames.Normalize(pdc));
        }

        if (lockoutRecord is not null && !dcs.Contains(HostNames.Normalize(lockoutRecord.Computer)))
        {
            dcs.Add(HostNames.Normalize(lockoutRecord.Computer));
        }

        if (settings.Source == AttributionSourceKind.OpenWec && lookup is { IsAvailable: true })
        {
            return await QueryStoredFailuresAsync(dcs, sam, from, to, settings, cancellationToken).ConfigureAwait(false);
        }

        var forwarded = settings.Source == AttributionSourceKind.ForwardedEvents;
        var hosts = forwarded ? (settings.Collector is { Length: > 0 } collector ? [collector] : []) : dcs;
        var query = new SecurityEventQuery
        {
            LogName = forwarded ? "ForwardedEvents" : "Security",
            EventCodes = FailureCodes,
            From = from,
            To = to,
            DataEquals = [("TargetUserName", sam)],
            MaxRecords = MaxRecords,
        };

        var failures = new List<SecurityEvent>();
        var finished = hosts.Count > 0;
        foreach (var host in hosts)
        {
            var result = await gate.RunAsync(host, settings.SourceQueryTimeout, token => ReadAsync(host, query, token), cancellationToken).ConfigureAwait(false);
            if (result.Completed && result.Value is { } records)
            {
                failures.AddRange(records.Where(r => !forwarded || dcs.Any(dc => HostNames.AreEqual(dc, r.Computer))));
            }
            else
            {
                finished = false;
            }
        }

        return (failures, finished);
    }

    private async Task<(List<SecurityEvent> Failures, bool Finished)> QueryStoredFailuresAsync(List<string> dcs, string sam, DateTimeOffset from,
        DateTimeOffset to, AttributionSettings settings, CancellationToken cancellationToken)
    {
        var failures = new List<SecurityEvent>();
        var finished = dcs.Count > 0;
        foreach (var dc in dcs)
        {
            var result = await gate.RunAsync(dc, settings.SourceQueryTimeout,
                token => lookup!.FindFailuresForAccountAsync(sam, FailureCodes, from, to, [dc], MaxRecords, token), cancellationToken).ConfigureAwait(false);
            if (result.Completed && result.Value is { } records)
            {
                failures.AddRange(records);
            }
            else
            {
                finished = false;
            }
        }

        return (failures, finished);
    }

    private async Task<List<SecurityEvent>> ReadAsync(string host, SecurityEventQuery query, CancellationToken cancellationToken)
    {
        var parser = new SecurityEventParser();
        var records = new List<SecurityEvent>();
        await foreach (var xml in source.ReadAsync(host, query, cancellationToken).ConfigureAwait(false))
        {
            if (parser.Parse(xml, host) is { } parsed)
            {
                records.Add(parsed.Event);
            }
        }

        return records;
    }

    private static IEnumerable<LockoutSource> Aggregate(IEnumerable<SecurityEvent> failures) =>
        failures
            .Where(IsBadPassword)
            .DistinctBy(f => (f.Computer.ToLowerInvariant(), f.RecordId))
            .Select(f => (Ip: LogonSourceResolver.NormalizeAddress(f.Get("IpAddress")), Workstation: f.Get("Workstation") ?? f.Get("WorkstationName"), f.EventCode))
            .Select(f => f with { Ip = f.Ip is "-" ? null : f.Ip })
            .Where(f => f.Ip is not null || f.Workstation is not null)
            .GroupBy(f => (f.Ip?.ToLowerInvariant(), f.Workstation?.ToUpperInvariant(), f.EventCode))
            .Select(g => new LockoutSource(g.First().Ip, g.First().Workstation, g.Count(), g.Key.Item3));

    private static bool IsBadPassword(SecurityEvent failure) => failure.EventCode switch
    {
        SecurityEventCatalog.KerberosPreAuthFailed => string.Equals(failure.Get("Status"), "0x18", StringComparison.OrdinalIgnoreCase),
        SecurityEventCatalog.CredentialValidation => string.Equals(failure.Get("Status"), "0xc000006a", StringComparison.OrdinalIgnoreCase),
        SecurityEventCatalog.LogonFailed => true,
        _ => false,
    };
}
