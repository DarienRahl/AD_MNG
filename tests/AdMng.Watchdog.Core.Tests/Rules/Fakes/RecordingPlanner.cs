using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Rules;

namespace AdMng.Watchdog.Core.Tests.Rules.Fakes;

/// <summary>Stands in for the NotificationPlanner (W-notify): one e-mail pending row per immediate match.</summary>
internal sealed class RecordingPlanner : IRuleMatchPlanner
{
    public List<(RuleEvaluationResult Evaluation, WatchdogConfiguration Configuration)> Calls { get; } = [];

    public ValueTask<IReadOnlyList<PendingNotification>> PlanAsync(RuleEvaluationResult evaluation, WatchdogConfiguration configuration,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        Calls.Add((evaluation, configuration));
        IReadOnlyList<PendingNotification> rows = [.. evaluation.Matches
            .Where(m => m.Rule.Delivery.Immediate)
            .Select(m => new PendingNotification
            {
                EventId = evaluation.Event.Id,
                RuleId = m.Rule.Id,
                Target = new NotificationTarget { ChannelType = DeliveryChannelType.Email, ChannelId = "email" },
                Severity = m.NotificationSeverity,
                BucketKey = m.Rule.Id,
                DueAt = now,
                CreatedAt = now,
            })];
        return ValueTask.FromResult(rows);
    }
}
