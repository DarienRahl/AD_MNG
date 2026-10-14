using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// Read-permission probes of the service account (section 10.12), run after the baseline and every
/// <c>PermissionSelfCheckInterval</c>: change tracking (uSNChanged readable on every user), account restrictions,
/// replication metadata, Deleted Objects, domain policy, the PSO container and the configuration partition. Results
/// go to <see cref="IHealthRegistry"/>; a transition from Ok to Warning/Error appends one
/// <see cref="WatchEventKind.HealthPermissionGap"/> (not repeated while the state stays bad).
/// </summary>
public sealed class PermissionSelfCheck
{
    public const string UsnChanged = "permission.uSNChanged";
    public const string AccountRestrictions = "permission.accountRestrictions";
    public const string ReplicationMetadata = "permission.replicationMetadata";
    public const string DeletedObjects = "permission.deletedObjects";
    public const string DomainPolicy = "permission.domainPolicy";
    public const string PasswordSettings = "permission.passwordSettings";
    public const string ConfigurationPartition = "permission.configurationPartition";

    /// <summary>Users sampled by the account-restrictions probe.</summary>
    public const int SampleUsers = 20;

    private const int PageSize = 1000;

    private readonly IHealthRegistry _health;
    private readonly IEventStore _events;
    private readonly IWatchdogSignals _signals;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PermissionSelfCheck> _logger;
    private readonly Dictionary<string, HealthState> _previous = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PermissionSelfCheck(IHealthRegistry health, IEventStore events, IWatchdogSignals signals, TimeProvider timeProvider, ILogger<PermissionSelfCheck> logger)
    {
        _health = health;
        _events = events;
        _signals = signals;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>When the probes last ran (null = never).</summary>
    public DateTimeOffset? LastRunAt { get; private set; }

    /// <summary>Runs every probe and reports the results.</summary>
    /// <exception cref="DirectoryException">A transient directory error (the run is repeated later).</exception>
    public async Task<IReadOnlyList<HealthCheckDto>> RunAsync(IDirectorySession session, DirectoryFacts facts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(facts);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            var results = new List<HealthCheckDto>
            {
                await ProbeAsync(UsnChanged, HealthState.Error, now, () => UsnChangedAsync(session, facts, cancellationToken)).ConfigureAwait(false),
                await ProbeAsync(AccountRestrictions, HealthState.Error, now, () => AccountRestrictionsAsync(session, facts, cancellationToken)).ConfigureAwait(false),
                await ProbeAsync(ReplicationMetadata, HealthState.Warning, now, () => ReplicationMetadataAsync(session, facts, cancellationToken)).ConfigureAwait(false),
                await ProbeAsync(DeletedObjects, HealthState.Warning, now, () => DeletedObjectsAsync(session, facts, cancellationToken)).ConfigureAwait(false),
                await ProbeAsync(DomainPolicy, HealthState.Warning, now, () => DomainPolicyAsync(session, facts, cancellationToken)).ConfigureAwait(false),
                await ProbeAsync(PasswordSettings, HealthState.Warning, now, () => PasswordSettingsAsync(session, facts, cancellationToken)).ConfigureAwait(false),
                await ProbeAsync(ConfigurationPartition, HealthState.Warning, now, () => ConfigurationPartitionAsync(session, facts, cancellationToken)).ConfigureAwait(false),
            };
            foreach (var result in results)
            {
                _health.Report(result);
                var previous = _previous.GetValueOrDefault(result.Code, HealthState.Ok);
                _previous[result.Code] = result.State;
                if (previous == HealthState.Ok && result.State is HealthState.Warning or HealthState.Error)
                {
                    await AppendGapAsync(result, now, cancellationToken).ConfigureAwait(false);
                }
            }

            LastRunAt = now;
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs one probe: (passed, args); a non-transient directory error counts as failed.</summary>
    private static async Task<HealthCheckDto> ProbeAsync(string code, HealthState failure, DateTimeOffset now, Func<Task<(bool Passed, string[] Args)>> probe)
    {
        bool passed;
        string[] args;
        try
        {
            (passed, args) = await probe().ConfigureAwait(false);
        }
        catch (DirectoryException ex) when (!ex.IsTransient)
        {
            (passed, args) = (false, [ex.Kind.ToString()]);
        }

        return new HealthCheckDto
        {
            Code = code,
            State = passed ? HealthState.Ok : failure,
            Detail = passed ? null : DetailOf(code),
            Args = args,
            CheckedAt = now,
        };
    }

    /// <summary>Users invisible to <c>(uSNChanged&gt;=1)</c> that still exist without a readable uSNChanged.</summary>
    private static async Task<(bool, string[])> UsnChangedAsync(IDirectorySession session, DirectoryFacts facts, CancellationToken cancellationToken)
    {
        var all = await GuidsAsync(session, facts.DomainDn, DetectionFilters.Users, cancellationToken).ConfigureAwait(false);
        var tracked = await GuidsAsync(session, facts.DomainDn, LdapFilter.And(DetectionFilters.Users, LdapFilter.GreaterOrEqual(Attr.UsnChanged, 1)), cancellationToken)
            .ConfigureAwait(false);
        var gaps = 0;
        foreach (var (guid, dn) in all.Where(a => !tracked.ContainsKey(a.Key)))
        {
            var entry = await session.GetAsync(dn, [Attr.ObjectGuid, Attr.UsnChanged], cancellationToken).ConfigureAwait(false);
            if (entry is not null && entry.ObjectGuid == guid && entry.GetInt64(Attr.UsnChanged) is null)
            {
                gaps++;
            }
        }

        return (gaps == 0, gaps == 0 ? [] : [gaps.ToString(CultureInfo.InvariantCulture)]);
    }

    private static async Task<(bool, string[])> AccountRestrictionsAsync(IDirectorySession session, DirectoryFacts facts, CancellationToken cancellationToken)
    {
        var sampled = 0;
        var missing = 0;
        await foreach (var entry in session.SearchAsync(new DirectorySearch
        {
            BaseDn = facts.DomainDn,
            Filter = DetectionFilters.Users,
            Attributes = [Attr.ObjectGuid, Attr.UserAccountControl, Attr.PwdLastSet, Attr.AccountExpires],
            PageSize = SampleUsers,
            SizeLimit = SampleUsers,
        }, cancellationToken).ConfigureAwait(false))
        {
            if (!entry.Has(Attr.UserAccountControl) || !entry.Has(Attr.PwdLastSet) || !entry.Has(Attr.AccountExpires))
            {
                missing++;
            }

            if (++sampled >= SampleUsers)
            {
                break;
            }
        }

        return (missing == 0, missing == 0 ? [] : [missing.ToString(CultureInfo.InvariantCulture)]);
    }

    private static async Task<(bool, string[])> ReplicationMetadataAsync(IDirectorySession session, DirectoryFacts facts, CancellationToken cancellationToken)
    {
        var head = await session.GetAsync(facts.DomainDn, [WatchdogAttributes.ReplAttributeMetaData, WatchdogAttributes.ReplPropertyMetaData], cancellationToken)
            .ConfigureAwait(false);
        return (head is not null && (head.Has(WatchdogAttributes.ReplAttributeMetaData) || head.Has(WatchdogAttributes.ReplPropertyMetaData)), []);
    }

    /// <summary>Base read of CN=Deleted Objects with Show Deleted; without List Contents only the DN comes back.</summary>
    private static async Task<(bool, string[])> DeletedObjectsAsync(IDirectorySession session, DirectoryFacts facts, CancellationToken cancellationToken)
    {
        var container = DistinguishedName.Parse(facts.DomainDn).Child("CN", "Deleted Objects").ToString();
        await foreach (var entry in session.SearchAsync(new DirectorySearch
        {
            BaseDn = container,
            Scope = SearchScope.Base,
            Filter = "(objectClass=*)",
            Attributes = [Attr.ObjectGuid],
            IncludeDeleted = true,
        }, cancellationToken).ConfigureAwait(false))
        {
            return (entry.ObjectGuid is not null, []);
        }

        return (false, []);
    }

    private static async Task<(bool, string[])> DomainPolicyAsync(IDirectorySession session, DirectoryFacts facts, CancellationToken cancellationToken)
    {
        var head = await session.GetAsync(facts.DomainDn, [WatchdogAttributes.LockoutDuration, WatchdogAttributes.MaxPwdAge], cancellationToken).ConfigureAwait(false);
        return (head is not null && head.Has(WatchdogAttributes.LockoutDuration) && head.Has(WatchdogAttributes.MaxPwdAge), []);
    }

    private static async Task<(bool, string[])> PasswordSettingsAsync(IDirectorySession session, DirectoryFacts facts, CancellationToken cancellationToken)
    {
        var container = DistinguishedName.Parse(facts.DomainDn).Child("CN", "System").Child("CN", "Password Settings Container").ToString();
        var entry = await session.GetAsync(container, [Attr.ObjectGuid, Attr.ObjectClass], cancellationToken).ConfigureAwait(false);
        return (entry?.ObjectGuid is not null, []);
    }

    private static async Task<(bool, string[])> ConfigurationPartitionAsync(IDirectorySession session, DirectoryFacts facts, CancellationToken cancellationToken)
    {
        var sites = DistinguishedName.Parse(facts.ConfigurationDn).Child("CN", "Sites").ToString();
        await foreach (var _ in session.SearchAsync(new DirectorySearch
        {
            BaseDn = sites,
            Filter = LdapFilter.Equal(Attr.ObjectClass, WatchdogObjectClasses.NtdsDsa),
            Attributes = [Attr.ObjectGuid],
            SizeLimit = 1,
        }, cancellationToken).ConfigureAwait(false))
        {
            return (true, []);
        }

        return (false, []);
    }

    private static async Task<Dictionary<Guid, string>> GuidsAsync(IDirectorySession session, string domainDn, string filter, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, string>();
        await foreach (var entry in session.SearchAsync(new DirectorySearch { BaseDn = domainDn, Filter = filter, Attributes = [Attr.ObjectGuid], PageSize = PageSize }, cancellationToken)
            .ConfigureAwait(false))
        {
            if (entry.ObjectGuid is { } guid)
            {
                result[guid] = entry.DistinguishedName;
            }
        }

        return result;
    }

    private async Task AppendGapAsync(HealthCheckDto check, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var changes = new List<AttributeChange>
        {
            new() { Attribute = "code", NewValues = [check.Code], NewCount = 1 },
            new() { Attribute = "state", NewValues = [check.State.ToString()], NewCount = 1 },
        };
        if (check.Args.Count > 0)
        {
            changes.Add(new AttributeChange { Attribute = "args", NewValues = check.Args, NewCount = check.Args.Count });
        }

        var watchEvent = new WatchEvent
        {
            EventId = Guid.CreateVersion7(now),
            ChangeSetId = Guid.CreateVersion7(now),
            Kind = WatchEventKind.HealthPermissionGap,
            Severity = EventKindCatalog.Get(WatchEventKind.HealthPermissionGap).DefaultSeverity,
            Object = new ObjectRef { Dn = string.Empty, Kind = WatchObjectKind.Watchdog },
            Changes = changes,
            DetectedAt = now,
            Source = DetectionSource.Health,
            Attribution = AdMng.Watchdog.Contracts.Events.Attribution.NotApplicable,
        };
        try
        {
            await _events.AppendAsync(watchEvent, cancellationToken).ConfigureAwait(false);
            _signals.Raise(WatchdogSignal.EventsStored);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SafeExceptionLog.LogError(_logger, ex, "permission gap event append");
        }
    }

    private static string DetailOf(string code) => code switch
    {
        UsnChanged => "Some users are invisible to change tracking (uSNChanged not readable).",
        AccountRestrictions => "Account restriction attributes (userAccountControl, pwdLastSet, accountExpires) are not readable.",
        ReplicationMetadata => "Replication metadata is not readable; originating times and DCs are unavailable.",
        DeletedObjects => "CN=Deleted Objects is not readable; deletions are found by the daily reconciliation only.",
        DomainPolicy => "The domain lockout and password policy is not readable.",
        PasswordSettings => "The Password Settings Container is not readable; the domain lockout duration is used for everyone.",
        ConfigurationPartition => "Domain controllers in the configuration partition are not readable.",
        _ => "Read permission missing.",
    };
}
