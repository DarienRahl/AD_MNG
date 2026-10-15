using System.Collections.Concurrent;
using System.Globalization;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// SubjectLogonId → 4624 → source address / workstation (section 11.4 #6), when <c>LookupLogonSource</c> and the
/// logon id is not LocalSystem's 0x3e7: one newest-first query for <c>TargetLogonId</c> on the DC that logged the
/// evidence (ForwardedEvents mode: the collector's ForwardedEvents log), from <c>evidence time − LogonLookback</c> to
/// the evidence time, through <see cref="SourceQueryGate"/> (one query per DC, timeout); positive and negative results
/// are cached for up to 24 h per (DC, logon id) – nothing is ingested. Logon ids are unique per boot only, so a cached
/// session is reused only for evidence between its 4624 and <c>LogonLookback</c> later and only while no 1100 (service
/// shutdown, a reboot) of that DC was seen after the logon; a negative result only for evidence inside the window already
/// searched. LogonType 3 with an address → the address is the
/// primary source, the workstation is WorkstationName (client-supplied) or reverse DNS (SourceMethod "dns"); a missing
/// or loopback address → SourceMethod "local" (on the DC: ADWS, PowerShell, console); LogonType 10 → the RDP client
/// (possibly a jump host); LogonType 8 → simple bind. SourceConfidence never exceeds the actor's confidence and is
/// capped at Low for DNS and NTLM workstation names.
/// </summary>
public sealed class LogonSourceResolver(ISecurityEventSource source, SourceQueryGate gate, IReverseDnsResolver dns, TimeProvider timeProvider,
    DcLogHealthTracker? tracker = null)
{
    /// <summary>Lifetime of cached lookups (positive and negative).</summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);

    /// <summary>How long the "4624 is collected" probe of a WEF collector is trusted.</summary>
    public static readonly TimeSpan CollectionProbeDuration = TimeSpan.FromHours(1);

    private static readonly string[] LoopbackAddresses = ["::1", "127.0.0.1"];

    private readonly ConcurrentDictionary<(string Host, string Computer, string LogonId), CachedLookup> _cache = new();
    private readonly ConcurrentDictionary<string, (bool Collected, DateTimeOffset At)> _collected = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The source of the session that wrote <paramref name="evidence"/>; null when not looked up (disabled,
    /// no or a LocalSystem logon id, no reachable log).</summary>
    public async Task<LogonSource?> ResolveAsync(SecurityEvent evidence, AttributionConfidence actorConfidence, AttributionSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(settings);
        var logonId = evidence.SubjectLogonId;
        if (!settings.LookupLogonSource || logonId is null or "0x3e7" or "0x0" || !SecurityEventQueryBuilder.IsAllowedValue(logonId))
        {
            return null;
        }

        var forwarded = settings.Source == AttributionSourceKind.ForwardedEvents;
        var host = forwarded ? settings.Collector : evidence.Source;
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var key = (HostNames.Normalize(host), HostNames.Normalize(evidence.Computer), logonId);
        var now = timeProvider.GetUtcNow();
        if (_cache.TryGetValue(key, out var cached) && now - cached.At < CacheDuration && Covers(cached, evidence, settings))
        {
            return Cap(cached.Source, actorConfidence);
        }

        if (forwarded && !await IsCollectedAsync(host, settings, cancellationToken).ConfigureAwait(false))
        {
            return LogonSource.NotFound(AttributionReasons.SourceNotCollected);
        }

        var query = new SecurityEventQuery
        {
            LogName = forwarded ? "ForwardedEvents" : "Security",
            EventCodes = [SecurityEventCatalog.LogonSucceeded],
            From = evidence.TimeCreated - settings.LogonLookback,
            To = evidence.TimeCreated,
            DataEquals = [("TargetLogonId", logonId)],
            NewestFirst = true,
            MaxRecords = forwarded ? 10 : 1,
        };
        var result = await gate.RunAsync(host, settings.SourceQueryTimeout,
            token => FirstLogonAsync(host, query, evidence.Computer, token), cancellationToken).ConfigureAwait(false);
        if (!result.Completed)
        {
            return LogonSource.NotFound(AttributionReasons.DcUnreachable);   // timeout / access – not cached, retried later
        }

        var found = result.Value is { } logon
            ? await InterpretAsync(logon, settings, cancellationToken).ConfigureAwait(false)
            : LogonSource.NotFound(AttributionReasons.SourceLogonNotFound);
        _cache[key] = new CachedLookup(found, now, result.Value?.TimeCreated, query.From!.Value, query.To!.Value);
        return Cap(found, actorConfidence);
    }

    /// <summary>Whether a cached lookup answers <paramref name="evidence"/>: a found session only for evidence between its
    /// logon and <c>LogonLookback</c> later with no shutdown of the DC after the logon; a negative result only for
    /// evidence inside the searched window.</summary>
    private bool Covers(CachedLookup cached, SecurityEvent evidence, AttributionSettings settings)
    {
        var at = evidence.TimeCreated;
        if (cached.LogonTime is not { } logonTime)
        {
            return at >= cached.SearchedFrom && at <= cached.SearchedTo;
        }

        return at >= logonTime && at <= logonTime + settings.LogonLookback
            && !(tracker?.LastShutdown(HostNames.Normalize(evidence.Computer)) > logonTime);
    }

    /// <summary>One cached 4624 lookup: the source, when it was looked up, the logon time of a found session, and the
    /// searched window.</summary>
    private sealed record CachedLookup(LogonSource Source, DateTimeOffset At, DateTimeOffset? LogonTime, DateTimeOffset SearchedFrom, DateTimeOffset SearchedTo);

    private async Task<SecurityEvent?> FirstLogonAsync(string host, SecurityEventQuery query, string computer, CancellationToken cancellationToken)
    {
        var parser = new SecurityEventParser();
        await foreach (var xml in source.ReadAsync(host, query, cancellationToken).ConfigureAwait(false))
        {
            if (parser.Parse(xml, host) is { Event: var logon } && HostNames.AreEqual(logon.Computer, computer))
            {
                return logon;
            }
        }

        return null;
    }

    private async Task<bool> IsCollectedAsync(string collector, AttributionSettings settings, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (_collected.TryGetValue(collector, out var probe) && now - probe.At < CollectionProbeDuration)
        {
            return probe.Collected;
        }

        var query = new SecurityEventQuery
        {
            LogName = "ForwardedEvents",
            EventCodes = [SecurityEventCatalog.LogonSucceeded],
            From = now - settings.LogonLookback,
            NewestFirst = true,
            MaxRecords = 1,
        };
        var result = await gate.RunAsync(collector, settings.SourceQueryTimeout, async token =>
        {
            await foreach (var _ in source.ReadAsync(collector, query, token).ConfigureAwait(false))
            {
                return true;
            }

            return false;
        }, cancellationToken).ConfigureAwait(false);

        // An unanswered probe proves nothing: assume collected (the lookup then reports SOURCE_LOGON_NOT_FOUND or
        // DC_UNREACHABLE).
        var collected = !result.Completed || result.Value;
        _collected[collector] = (collected, now);
        return collected;
    }

    private async Task<LogonSource> InterpretAsync(SecurityEvent logon, AttributionSettings settings, CancellationToken cancellationToken)
    {
        var logonType = int.TryParse(logon.Get("LogonType"), NumberStyles.None, CultureInfo.InvariantCulture, out var type) ? type : (int?)null;
        var ip = NormalizeAddress(logon.Get("IpAddress"));
        if (ip is null || LoopbackAddresses.Contains(ip))
        {
            return new LogonSource { SourceMethod = "local", LogonType = logonType, Confidence = AttributionConfidence.High };
        }

        var workstation = logon.Get("WorkstationName");
        var ntlm = string.Equals(logon.Get("AuthenticationPackageName"), "NTLM", StringComparison.OrdinalIgnoreCase);
        var method = "4624";
        var confidence = workstation is not null && ntlm ? AttributionConfidence.Low : AttributionConfidence.High;
        if (workstation is null && settings.ResolveReverseDns && logonType == 3)
        {
            workstation = await dns.ResolveAsync(ip, cancellationToken).ConfigureAwait(false);
            if (workstation is not null)
            {
                method = "dns";
                confidence = AttributionConfidence.Low;
            }
        }

        return new LogonSource { IpAddress = ip, Workstation = workstation, SourceMethod = method, LogonType = logonType, Confidence = confidence };
    }

    /// <summary>Strips the IPv4-mapped prefix (<c>::ffff:10.0.0.5</c>).</summary>
    public static string? NormalizeAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var trimmed = address.Trim();
        return trimmed.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase) && trimmed.Contains('.', StringComparison.Ordinal)
            ? trimmed[7..]
            : trimmed;
    }

    private static LogonSource Cap(LogonSource found, AttributionConfidence actorConfidence) =>
        found.Reason is not null || found.Confidence <= actorConfidence ? found : found with { Confidence = actorConfidence };
}
