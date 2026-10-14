using AdMng.Core.Ldap;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Testing.Stores;

// ISecurityEventStore: security_events (unique per computer + record id), eventlog_cursors.
public sealed partial class InMemoryWatchdogStore
{
    private readonly List<SecurityEvent> _securityEvents = [];
    private readonly HashSet<(string Computer, long RecordId)> _securityEventKeys = [];
    private readonly Dictionary<string, EventLogCursor> _cursors = new(StringComparer.OrdinalIgnoreCase);

    public Task<EventLogCursor?> GetCursorAsync(string source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            return Task.FromResult(_cursors.GetValueOrDefault(source));
        }
    }

    public Task AppendAsync(IReadOnlyList<SecurityEvent> events, EventLogCursor cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(cursor);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            foreach (var securityEvent in events)
            {
                if (_securityEventKeys.Add((securityEvent.Computer.ToUpperInvariant(), securityEvent.RecordId)))
                {
                    _securityEvents.Add(Copy(securityEvent));
                }
            }

            _cursors[cursor.Source] = cursor;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SecurityEvent>> FindAsync(SecurityEventCriteria criteria, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        lock (_gate)
        {
            IReadOnlyList<SecurityEvent> result =
            [
                .. _securityEvents
                    .Where(e => criteria.EventCodes.Count == 0 || criteria.EventCodes.Contains(e.EventCode))
                    .Where(e => criteria.Computer is null || string.Equals(e.Computer, criteria.Computer, StringComparison.OrdinalIgnoreCase))
                    .Where(e => e.TimeCreated >= criteria.From && e.TimeCreated <= criteria.To)
                    .Where(e => criteria.TargetSid is null || string.Equals(e.TargetSid, criteria.TargetSid, StringComparison.OrdinalIgnoreCase))
                    .Where(e => criteria.ObjectGuid is null || e.ObjectGuid == criteria.ObjectGuid)
                    .Where(e => criteria.MemberSid is null || string.Equals(e.Get("MemberSid"), criteria.MemberSid, StringComparison.OrdinalIgnoreCase))
                    .Where(e => criteria.TargetUserName is null
                        || string.Equals(e.Get("TargetUserName"), criteria.TargetUserName, StringComparison.OrdinalIgnoreCase))
                    .Where(e => criteria.ObjectDn is null || DistinguishedName.AreEqual(e.Get("ObjectDN"), criteria.ObjectDn))
                    .OrderBy(e => e.TimeCreated).ThenBy(e => e.RecordId)
                    .Take(Math.Max(0, criteria.Limit))
                    .Select(Copy),
            ];
            return Task.FromResult(result);
        }
    }

    public Task<DateTimeOffset?> GetLastSeenAsync(string computer, int eventCode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(computer);
        lock (_gate)
        {
            var last = _securityEvents
                .Where(e => e.EventCode == eventCode && string.Equals(e.Computer, computer, StringComparison.OrdinalIgnoreCase))
                .Select(e => (DateTimeOffset?)e.TimeCreated)
                .Max();
            return Task.FromResult(last);
        }
    }
}
