using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>
/// Channel health after every delivery attempt (section 15.8), reported to <see cref="IHealthRegistry.ReportChannel"/>:
/// Ok after a success, Warning after ≥ 3 transient failures in a row or a message-specific (permanent) failure, Error
/// after a broken channel, failed authentication or a dead letter – Error stays until the next success or until an hour
/// passed without another error-level failure. Thread-safe; the sender processes each channel sequentially.
/// </summary>
public sealed class ChannelHealthTracker
{
    /// <summary>Transient failures in a row that turn the channel to Warning.</summary>
    public const int WarningAfterFailures = 3;

    /// <summary>How long an error-level failure keeps the channel in Error.</summary>
    public static readonly TimeSpan ErrorWindow = TimeSpan.FromHours(1);

    private readonly IHealthRegistry _registry;
    private readonly Dictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public ChannelHealthTracker(IHealthRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <summary>Records a delivered message.</summary>
    public void Success(string channelId, DeliveryChannelType channelType, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelId);
        ChannelHealthDto report;
        lock (_gate)
        {
            var state = Get(channelId, channelType);
            state.ConsecutiveFailures = 0;
            state.LastSuccessAt = now;
            state.ErrorAt = null;
            state.Health = HealthState.Ok;
            report = state.ToDto();
        }

        _registry.ReportChannel(report);
    }

    /// <summary>Records a failed attempt; returns true when the channel entered Error with it (the caller then reports
    /// <c>HealthDeliveryFailing</c> once).</summary>
    /// <param name="channelId">Channel id ("email" for SMTP).</param>
    /// <param name="channelType">Channel type.</param>
    /// <param name="level"><see cref="HealthState.Error"/> for broken channels, authentication failures and dead
    /// letters; <see cref="HealthState.Warning"/> for message-specific failures; <see cref="HealthState.Ok"/> for a
    /// transient failure that will be retried.</param>
    /// <param name="errorCode">Classified error code (no message content).</param>
    /// <param name="now">Attempt time.</param>
    public bool Failure(string channelId, DeliveryChannelType channelType, HealthState level, string? errorCode, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelId);
        ChannelHealthDto report;
        bool enteredError;
        lock (_gate)
        {
            var state = Get(channelId, channelType);
            var previous = state.Health;
            state.ConsecutiveFailures++;
            state.LastFailureAt = now;
            state.LastErrorCode = errorCode;
            if (level == HealthState.Error)
            {
                state.ErrorAt = now;
            }

            state.Health = state.ErrorAt is { } errorAt && now - errorAt < ErrorWindow
                ? HealthState.Error
                : level == HealthState.Warning || state.ConsecutiveFailures >= WarningAfterFailures ? HealthState.Warning : HealthState.Ok;
            enteredError = previous != HealthState.Error && state.Health == HealthState.Error;
            report = state.ToDto();
        }

        _registry.ReportChannel(report);
        return enteredError;
    }

    /// <summary>Updates the pending counts (outbox Pending/Retrying per channel) of the known channels and of channels
    /// with pending messages that were not attempted yet (state Unknown).</summary>
    public void UpdatePending(IReadOnlyDictionary<string, int> pending, Func<string, DeliveryChannelType?> channelTypeOf)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(channelTypeOf);
        var reports = new List<ChannelHealthDto>();
        lock (_gate)
        {
            foreach (var channelId in pending.Keys)
            {
                if (!_states.ContainsKey(channelId) && channelTypeOf(channelId) is { } type)
                {
                    Get(channelId, type);
                }
            }

            foreach (var state in _states.Values)
            {
                var count = pending.GetValueOrDefault(state.ChannelId);
                if (count != state.Pending || state.Health == HealthState.Unknown)
                {
                    state.Pending = count;
                    reports.Add(state.ToDto());
                }
            }
        }

        foreach (var report in reports)
        {
            _registry.ReportChannel(report);
        }
    }

    private State Get(string channelId, DeliveryChannelType channelType)
    {
        if (!_states.TryGetValue(channelId, out var state))
        {
            state = new State(channelId, channelType);
            _states[channelId] = state;
        }

        return state;
    }

    private sealed class State(string channelId, DeliveryChannelType channelType)
    {
        public string ChannelId { get; } = channelId;

        public DeliveryChannelType ChannelType { get; } = channelType;

        public HealthState Health { get; set; } = HealthState.Unknown;

        public int ConsecutiveFailures { get; set; }

        public DateTimeOffset? LastSuccessAt { get; set; }

        public DateTimeOffset? LastFailureAt { get; set; }

        public DateTimeOffset? ErrorAt { get; set; }

        public string? LastErrorCode { get; set; }

        public int Pending { get; set; }

        public ChannelHealthDto ToDto() => new()
        {
            ChannelId = ChannelId,
            ChannelType = ChannelType,
            State = Health,
            LastSuccessAt = LastSuccessAt,
            LastFailureAt = LastFailureAt,
            LastErrorCode = LastErrorCode,
            ConsecutiveFailures = ConsecutiveFailures,
            Pending = Pending,
        };
    }
}
