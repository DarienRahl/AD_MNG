using System.Reflection;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Diagnostics;
using AdMng.Watchdog.Core.Formatting;
using AdMng.Watchdog.Core.Formatting.Webhooks;
using AdMng.Watchdog.Core.Model;
using Microsoft.Extensions.Logging;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>
/// <see cref="IWatchdogJob"/> "heartbeat" (section 13.6): for every enabled webhook channel with
/// <c>Webhook.HeartbeatInterval</c>, a <c>watchdog.heartbeat</c> payload (instance, service version, directory state,
/// last poll, pending outbox count – no directory values) is enqueued as <see cref="DeliveryKind.Health"/> every interval,
/// the first one right after start-up. A SIEM alerts when it stops arriving; Teams uses the daily digest instead.
/// </summary>
public sealed class HeartbeatJob : IWatchdogJob
{
    /// <summary>Shortest accepted interval (the validator requires ≥ 5 min; this only guards against a flood).</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);

    private static readonly WatchdogSignal[] Signals = [WatchdogSignal.ConfigChanged];

    private readonly IOutboxStore _outbox;
    private readonly IHealthRegistry _health;
    private readonly IWatchdogConfigProvider _configProvider;
    private readonly IWatchdogSignals _signals;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HeartbeatJob> _logger;
    private readonly Dictionary<string, DateTimeOffset> _lastSent = new(StringComparer.OrdinalIgnoreCase);

    public HeartbeatJob(IOutboxStore outbox, IHealthRegistry health, IWatchdogConfigProvider configProvider, IWatchdogSignals signals,
        TimeProvider timeProvider, ILogger<HeartbeatJob> logger)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(configProvider);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _outbox = outbox;
        _health = health;
        _configProvider = configProvider;
        _signals = signals;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>The service version written into heartbeats (informational version without build metadata).</summary>
    public static string ServiceVersion { get; } = ReadServiceVersion();

    /// <inheritdoc/>
    public string Name => "heartbeat";

    /// <inheritdoc/>
    public TimeSpan DefaultInterval { get; } = TimeSpan.FromMinutes(1);

    /// <inheritdoc/>
    public IReadOnlyList<WatchdogSignal> WakeSignals => Signals;

    /// <inheritdoc/>
    public async Task<JobResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var now = _timeProvider.GetUtcNow();
            var configuration = _configProvider.Current.Configuration;
            var channels = (configuration.Channels ?? [])
                .Where(c => c is not null && c.Enabled && c.Type == ChannelType.Webhook && c.Webhook?.HeartbeatInterval > TimeSpan.Zero)
                .ToList();
            foreach (var stale in _lastSent.Keys.Where(id => !channels.Any(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))).ToList())
            {
                _lastSent.Remove(stale);
            }

            var next = now + DefaultInterval;
            var enqueued = false;
            foreach (var channel in channels)
            {
                var interval = Max(channel.Webhook!.HeartbeatInterval!.Value, MinimumInterval);
                if (_lastSent.TryGetValue(channel.Id, out var last) && now - last < interval)
                {
                    next = Min(next, last + interval);
                    continue;
                }

                await EnqueueAsync(channel, configuration, now, cancellationToken).ConfigureAwait(false);
                _lastSent[channel.Id] = now;
                next = Min(next, now + interval);
                enqueued = true;
            }

            if (enqueued)
            {
                _signals.Raise(WatchdogSignal.OutboxEnqueued);
            }

            return new JobResult(next - now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SafeExceptionLog.LogError(_logger, ex, "heartbeat");
            return new JobResult(DefaultInterval, Failed: true, FailureCode: SafeExceptionLog.ErrorCode(ex));
        }
    }

    private async Task EnqueueAsync(ChannelSettings channel, WatchdogConfiguration configuration, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var pending = (await _outbox.CountPendingByChannelAsync(cancellationToken).ConfigureAwait(false)).Values.Sum();
        var directory = _health.Directory;
        var payload = new HeartbeatPayload(now, ServiceVersion, directory.State.ToString(), directory.LastPollAt, pending);
        var context = NotificationContext.Create(configuration, channel.DetailLevel);
        await _outbox.EnqueueAsync(new OutboxMessage
        {
            MessageId = Guid.CreateVersion7(now).ToString("N"),
            ChannelType = DeliveryChannelType.Webhook,
            ChannelId = channel.Id,
            TargetKey = NotificationTargetResolver.ChannelTarget(channel).Key,
            Kind = DeliveryKind.Health,
            Http = WebhookPayloadFormatter.Heartbeat(payload, context),
            CreatedAt = now,
            NextAttemptAt = now,
        }, cancellationToken).ConfigureAwait(false);
    }

    private static string ReadServiceVersion()
    {
        var version = typeof(HeartbeatJob).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(HeartbeatJob).Assembly.GetName().Version?.ToString()
            ?? "0.0.0";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? version[..plus] : version;
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
