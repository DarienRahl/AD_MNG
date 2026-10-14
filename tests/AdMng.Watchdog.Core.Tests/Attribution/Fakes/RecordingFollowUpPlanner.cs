using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Tests.Attribution.Fakes;

/// <summary>Stands in for W-notify's FollowUpPlanner: records the attributed events and plans one follow-up row for each
/// resolved one.</summary>
internal sealed class RecordingFollowUpPlanner : IAttributionFollowUpPlanner
{
    private readonly Lock _gate = new();
    private readonly List<WatchEvent> _calls = [];

    public IReadOnlyList<WatchEvent> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public Task<IReadOnlyList<PendingNotification>> PlanAsync(WatchEvent attributed, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _calls.Add(attributed);
        }

        IReadOnlyList<PendingNotification> rows = attributed.Attribution.Status == AttributionStatus.Resolved
            ? [new PendingNotification
            {
                EventId = attributed.Id,
                RuleId = "account-status",
                Target = new NotificationTarget { ChannelType = DeliveryChannelType.Email, ChannelId = "email" },
                Severity = attributed.Severity,
                BucketKey = "account-status",
                DueAt = attributed.DetectedAt,
                IsFollowUp = true,
            }]
            : [];
        return Task.FromResult(rows);
    }
}
