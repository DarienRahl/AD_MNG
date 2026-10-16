using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Detection;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Watchdog.Core.Scans;

/// <summary>
/// Bursts over the stored events (sections 10.9, 10.15), every minute: <b>MassLockout</b> – distinct accounts with
/// AccountLockedOut in the window ≥ max(Threshold, ceil(percent × enabled users / 100)); <b>MassDeletion</b> –
/// ObjectDeleted of users, groups, OUs and service accounts against <c>MassDeletion</c> (Critical), of computers and
/// contacts against <c>MassComputerDeletion</c> (High), per resolved actor first, otherwise overall. Cooldown = window,
/// restart-safe (an existing burst event of the same kind/class in the window suppresses another). A computer burst
/// attributed to one actor offers that actor as a <c>computerCleanupAccount</c> exclusion suggestion for <c>bursts</c>.
/// </summary>
internal sealed class BurstDetector
{
    /// <summary>The rule the computer clean-up suggestion is for.</summary>
    public const string SuggestionRuleId = "bursts";

    /// <summary>At most this many object references in BurstInfo.</summary>
    public const int MaxObjects = 50;

    private static readonly TimeSpan EnabledUsersLifetime = TimeSpan.FromHours(24);
    private static readonly HashSet<WatchObjectKind> ComputerKinds = [WatchObjectKind.Computer, WatchObjectKind.Contact];
    private static readonly HashSet<WatchObjectKind> AccountKinds =
        [WatchObjectKind.User, WatchObjectKind.Group, WatchObjectKind.OrganizationalUnit, WatchObjectKind.ServiceAccount];

    private readonly IEventStore _events;
    private readonly ScanEventWriter _writer;
    private readonly ExclusionSuggestionRegistry _suggestions;
    private (DateTimeOffset At, int Count)? _enabledUsers;

    public BurstDetector(IEventStore events, ScanEventWriter writer, ExclusionSuggestionRegistry suggestions)
    {
        _events = events;
        _writer = writer;
        _suggestions = suggestions;
    }

    public async Task RunAsync(ScanContext context, CancellationToken cancellationToken)
    {
        var scans = context.Configuration.Scans;
        var longest = new[] { scans.MassLockout.Window, scans.MassDeletion.Window, scans.MassComputerDeletion.Window }.Max();
        var recent = new List<WatchEvent>();
        await foreach (var watchEvent in _events.GetRangeAsync(context.Now - longest, context.Now, cancellationToken).ConfigureAwait(false))
        {
            if (watchEvent.Kind is WatchEventKind.AccountLockedOut or WatchEventKind.ObjectDeleted or WatchEventKind.MassLockout or WatchEventKind.MassDeletion)
            {
                recent.Add(watchEvent);
            }
        }

        await MassLockoutAsync(context, scans.MassLockout, recent, cancellationToken).ConfigureAwait(false);
        await MassDeletionAsync(context, scans.MassDeletion, AccountKinds, WatchObjectKind.User, recent, cancellationToken).ConfigureAwait(false);
        await MassDeletionAsync(context, scans.MassComputerDeletion, ComputerKinds, WatchObjectKind.Computer, recent, cancellationToken).ConfigureAwait(false);
    }

    private async Task MassLockoutAsync(ScanContext context, BurstSettings settings, List<WatchEvent> recent, CancellationToken cancellationToken)
    {
        var since = context.Now - settings.Window;
        if (settings.Threshold <= 0 || recent.Any(e => e.Kind == WatchEventKind.MassLockout && e.DetectedAt >= since))
        {
            return;
        }

        var locked = Distinct(recent.Where(e => e.Kind == WatchEventKind.AccountLockedOut && e.DetectedAt >= since));
        if (locked.Count < settings.Threshold)
        {
            return;
        }

        var enabledUsers = await EnabledUsersAsync(context, cancellationToken).ConfigureAwait(false);
        var threshold = Math.Max(settings.Threshold, (int)Math.Ceiling(settings.PercentOfEnabledUsers * enabledUsers / 100));
        if (locked.Count >= threshold)
        {
            await EmitAsync(context, WatchEventKind.MassLockout, WatchObjectKind.Domain, settings, locked, null, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MassDeletionAsync(ScanContext context, BurstSettings settings, HashSet<WatchObjectKind> kinds, WatchObjectKind burstKind,
        List<WatchEvent> recent, CancellationToken cancellationToken)
    {
        var since = context.Now - settings.Window;
        if (settings.Threshold <= 0)
        {
            return;
        }

        var deletions = recent.Where(e => e.Kind == WatchEventKind.ObjectDeleted && e.DetectedAt >= since && kinds.Contains(e.Object.Kind)).ToList();
        var byActor = deletions
            .Where(e => e.Attribution is { Status: AttributionStatus.Resolved, Actor.Sid: not null })
            .GroupBy(e => e.Attribution.Actor!.Sid!, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Actor: g.First().Attribution, Objects: Distinct(g)))
            .Where(g => g.Objects.Count >= settings.Threshold)
            .OrderByDescending(g => g.Objects.Count)
            .FirstOrDefault();
        var overall = Distinct(deletions);
        if (byActor.Actor is null && overall.Count < settings.Threshold)
        {
            return;
        }

        if (byActor.Actor is { } actor && burstKind == WatchObjectKind.Computer)
        {
            Suggest(actor);
        }

        var cooling = recent.Any(e => e.Kind == WatchEventKind.MassDeletion && e.Object.Kind == burstKind && e.DetectedAt >= since);
        if (!cooling)
        {
            var objects = byActor.Actor is null ? overall : byActor.Objects;
            await EmitAsync(context, WatchEventKind.MassDeletion, burstKind, settings, objects, byActor.Actor, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Suggest(EventAttribution actor) => _suggestions.Add(new ExclusionSuggestionDto
    {
        RuleId = SuggestionRuleId,
        ActorSid = actor.Actor!.Sid!,
        ActorAccount = actor.Actor.Account,
        ReasonCode = ExclusionSuggestionRegistry.ComputerCleanupAccount,
    });

    private async Task EmitAsync(ScanContext context, WatchEventKind kind, WatchObjectKind objectKind, BurstSettings settings,
        IReadOnlyList<ObjectRef> objects, EventAttribution? actor, CancellationToken cancellationToken)
    {
        var domain = new ObjectRef { Dn = context.Facts.DomainDn, Name = context.Facts.DomainDnsName, Kind = objectKind };
        var draft = new EventDraft
        {
            Kind = kind,
            Burst = new BurstInfo { Count = objects.Count, Window = settings.Window, Objects = [.. objects.Take(MaxObjects)] },
            Attribution = actor,
        };
        await _writer.AppendAsync(context, draft, domain, DetectionSource.BurstDetector, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Enabled users, counted live at most once a day.</summary>
    private async Task<int> EnabledUsersAsync(ScanContext context, CancellationToken cancellationToken)
    {
        if (_enabledUsers is { } cached && context.Now - cached.At < EnabledUsersLifetime)
        {
            return cached.Count;
        }

        var count = await ScanQueries.CountAsync(context, AdMng.Core.Ldap.LdapFilter.And(DetectionFilters.Users, DetectionFilters.Enabled), cancellationToken).ConfigureAwait(false);
        _enabledUsers = (context.Now, count);
        return count;
    }

    /// <summary>Distinct objects (by GUID, else DN), latest event first.</summary>
    private static List<ObjectRef> Distinct(IEnumerable<WatchEvent> events) =>
        events.OrderByDescending(e => e.DetectedAt)
            .Select(e => e.Object)
            .DistinctBy(o => o.ObjectGuid?.ToString("N") ?? o.Dn.ToUpperInvariant())
            .ToList();
}
