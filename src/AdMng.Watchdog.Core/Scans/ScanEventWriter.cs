using AdMng.Core.ActiveDirectory;
using AdMng.Core.Model;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Scans;

/// <summary>
/// Turns scanner drafts into stored events: recorded-kind check, tags from the snapshot (privileged, serviceAccount …),
/// severity (<see cref="SeverityResolver"/>) and <see cref="IEventStore.AppendAsync"/>. Scanner events never touch the
/// snapshots.
/// </summary>
internal sealed class ScanEventWriter
{
    private readonly IDetectionStore _store;
    private readonly IEventStore _events;
    private readonly EventFactory _factory;
    private readonly EventTagger _tagger = new();

    public ScanEventWriter(IDetectionStore store, IEventStore events, TimeProvider timeProvider, SeverityResolver severity)
    {
        _store = store;
        _events = events;
        _factory = new EventFactory(timeProvider, severity);
    }

    /// <summary>Events appended since the writer was created.</summary>
    public int Appended { get; private set; }

    /// <summary>Appends the event; null when the kind is not recorded.</summary>
    public async Task<WatchEvent?> AppendAsync(ScanContext context, EventDraft draft, ObjectRef objectRef, DetectionSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(objectRef);
        if (!context.Settings.IsRecorded(draft.Kind))
        {
            return null;
        }

        SnapshotObject? subject = null;
        if (objectRef.ObjectGuid is { } guid)
        {
            (await _store.GetSnapshotsAsync([guid], cancellationToken).ConfigureAwait(false)).TryGetValue(guid, out subject);
        }

        var tags = _tagger.Tag(draft, subject is { IsDeleted: false } ? subject : null, objectRef, context.Tagging);
        var eventContext = new EventContext { Source = source, Domain = context.Facts.DomainDnsName, Severity = context.Settings.Severity };
        var watchEvent = _factory.Create(draft, objectRef, eventContext, tags, _factory.NewChangeSetId());
        var id = await _events.AppendAsync(watchEvent, cancellationToken).ConfigureAwait(false);
        Appended++;
        return watchEvent with { Id = id };
    }

    /// <summary>The event reference of a search result (identity attributes requested by the scanner).</summary>
    public static ObjectRef RefOf(DirectoryObject entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new ObjectRef
        {
            ObjectGuid = entry.ObjectGuid,
            Dn = entry.DistinguishedName,
            Name = entry.Name,
            DisplayName = entry.GetString(Attr.DisplayName),
            SamAccountName = entry.GetString(Attr.SamAccountName),
            Sid = entry.ObjectSid,
            Kind = WatchObjectKinds.FromObjectClasses(entry.GetStrings(Attr.ObjectClass)) ?? WatchObjectKind.User,
        };
    }

    /// <summary>Identity attributes every scanner requests.</summary>
    public static IReadOnlyList<string> IdentityAttributes { get; } =
        [Attr.ObjectGuid, Attr.ObjectSid, Attr.ObjectClass, Attr.Name, Attr.DisplayName, Attr.SamAccountName];
}
