using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Attribution;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// <see cref="EventDraft"/> → <see cref="WatchEvent"/>: identity (Guid v7), change set, object, domain, detection
/// source and times, originating time/DC from replication metadata (section 10.10), the default attribution
/// (<see cref="DefaultAttribution"/>) and the severity (<see cref="SeverityResolver"/>).
/// </summary>
internal sealed class EventFactory
{
    private readonly TimeProvider _timeProvider;
    private readonly SeverityResolver _severity;

    public EventFactory(TimeProvider timeProvider, SeverityResolver severity)
    {
        _timeProvider = timeProvider;
        _severity = severity;
    }

    /// <summary>A new change set id (Guid v7).</summary>
    public Guid NewChangeSetId() => Guid.CreateVersion7(_timeProvider.GetUtcNow());

    /// <summary>
    /// The default attribution of a kind: Pending for every kind the attribution engine processes – the attributable
    /// kinds of the security-event catalog (<see cref="SecurityEventCatalog.IsAttributable"/>, section 11.2) and
    /// AccountLockedOut, whose bad-password sources it looks up before marking it NotApplicable (11.4 #6b) –
    /// NotApplicable otherwise (scanner results, bursts, health, appeared/disappeared, lockout expiry).
    /// </summary>
    public static EventAttribution DefaultAttribution(WatchEventKind kind) =>
        SecurityEventCatalog.IsAttributable(kind) || kind == WatchEventKind.AccountLockedOut ? EventAttribution.Pending : EventAttribution.NotApplicable;

    /// <summary>Creates the event.</summary>
    public WatchEvent Create(EventDraft draft, ObjectRef objectRef, EventContext context, IReadOnlyList<string> tags, Guid changeSetId)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(objectRef);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tags);
        var now = _timeProvider.GetUtcNow();
        var origin = draft.MetadataAttributes.Count > 0 ? context.Metadata.NewestAbove(draft.MetadataAttributes, context.Watermark ?? long.MinValue) : null;
        var watchEvent = new WatchEvent
        {
            EventId = Guid.CreateVersion7(now),
            ChangeSetId = draft.ChangeSetId ?? changeSetId,
            Kind = draft.Kind,
            Object = draft.Object ?? objectRef,
            PreviousDn = draft.PreviousDn,
            Changes = draft.Changes,
            Member = draft.Member,
            Via = draft.Via,
            Expiry = draft.Expiry,
            Lockout = draft.Lockout,
            Burst = draft.Burst,
            Domain = context.Domain,
            DetectedAt = now,
            OriginatingTime = origin?.OriginatingTime,
            OriginatingDc = origin is null ? null : context.ResolveDcHost?.Invoke(origin.OriginatingInvocationId, origin.OriginatingDsaDn),
            LocalChangeTime = context.LocalChangeTime,
            Source = context.Source,
            Attribution = draft.Attribution ?? DefaultAttribution(draft.Kind),
            Tags = tags,
        };
        return watchEvent with { Severity = _severity.Resolve(watchEvent, context.Severity, draft.BaseSeverity) };
    }
}
