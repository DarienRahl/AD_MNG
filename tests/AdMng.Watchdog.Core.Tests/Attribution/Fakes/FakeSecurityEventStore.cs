using AdMng.Core.Ldap;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Tests.Attribution.Fakes;

/// <summary>In-memory <see cref="ISecurityEventStore"/> (duplicates by computer + record id ignored, criteria applied
/// like the SQLite store: codes, computer, time window, strong and weak keys, limit).</summary>
internal sealed class FakeSecurityEventStore : ISecurityEventStore
{
    private readonly Lock _gate = new();
    private readonly List<SecurityEvent> _events = [];
    private readonly Dictionary<string, EventLogCursor> _cursors = new(StringComparer.OrdinalIgnoreCase);

    public List<SecurityEventCriteria> Queries { get; } = [];

    public int AppendCalls { get; private set; }

    public IReadOnlyList<SecurityEvent> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    public void Add(params SecurityEvent[] events)
    {
        lock (_gate)
        {
            foreach (var e in events)
            {
                if (!_events.Any(x => string.Equals(x.Computer, e.Computer, StringComparison.OrdinalIgnoreCase) && x.RecordId == e.RecordId))
                {
                    _events.Add(e);
                }
            }
        }
    }

    public Task<EventLogCursor?> GetCursorAsync(string source, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_cursors.GetValueOrDefault(source));
        }
    }

    public void SetCursor(EventLogCursor cursor)
    {
        lock (_gate)
        {
            _cursors[cursor.Source] = cursor;
        }
    }

    public Task AppendAsync(IReadOnlyList<SecurityEvent> events, EventLogCursor cursor, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            AppendCalls++;
            Add([.. events]);
            _cursors[cursor.Source] = cursor;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SecurityEvent>> FindAsync(SecurityEventCriteria criteria, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Queries.Add(criteria);
            IReadOnlyList<SecurityEvent> found = [.. _events
                .Where(e => criteria.EventCodes.Count == 0 || criteria.EventCodes.Contains(e.EventCode))
                .Where(e => criteria.Computer is null || string.Equals(e.Computer, criteria.Computer, StringComparison.OrdinalIgnoreCase))
                .Where(e => e.TimeCreated >= criteria.From && e.TimeCreated <= criteria.To)
                .Where(e => criteria.TargetSid is null || string.Equals(e.TargetSid, criteria.TargetSid, StringComparison.OrdinalIgnoreCase))
                .Where(e => criteria.ObjectGuid is null || e.ObjectGuid == criteria.ObjectGuid)
                .Where(e => criteria.MemberSid is null || string.Equals(e.Get("MemberSid"), criteria.MemberSid, StringComparison.OrdinalIgnoreCase))
                .Where(e => criteria.TargetUserName is null || string.Equals(e.Get("TargetUserName"), criteria.TargetUserName, StringComparison.OrdinalIgnoreCase))
                .Where(e => criteria.ObjectDn is null || DistinguishedName.AreEqual(e.Get("ObjectDN"), criteria.ObjectDn))
                .OrderBy(e => e.TimeCreated)
                .Take(criteria.Limit)];
            return Task.FromResult(found);
        }
    }

    public Task<DateTimeOffset?> GetLastSeenAsync(string computer, int eventCode, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var times = _events.Where(e => e.EventCode == eventCode && string.Equals(e.Computer, computer, StringComparison.OrdinalIgnoreCase))
                .Select(e => (DateTimeOffset?)e.TimeCreated);
            return Task.FromResult(times.Max());
        }
    }
}
