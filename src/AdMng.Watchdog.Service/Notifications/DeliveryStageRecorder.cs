using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Watchdog.Service.Notifications;

/// <summary>
/// Collects <see cref="DeliveryStageResult"/>s of a test send (spec 15.8): the transports report each stage they pass
/// (resolve, connect, TLS, token, authenticate, sender, recipients, data / HTTP request and response) with its duration on
/// the injected <see cref="TimeProvider"/>. Details are protocol facts only ("TLS 1.3 …", "250 2.0.0", "HTTP 202") –
/// never message content, addresses or directory values.
/// </summary>
public sealed class DeliveryStageRecorder(TimeProvider timeProvider)
{
    private readonly List<DeliveryStageResult> _stages = [];
    private readonly List<string> _hints = [];
    private long _started = timeProvider.GetTimestamp();

    public IReadOnlyList<DeliveryStageResult> Stages => _stages;

    public IReadOnlyList<string> Hints => _hints;

    /// <summary>Starts timing the next stage.</summary>
    public void Begin() => _started = timeProvider.GetTimestamp();

    public void Succeeded(DeliveryStage stage, string? detail = null, int? statusCode = null, string? enhancedStatus = null) =>
        Add(stage, true, detail, statusCode, enhancedStatus);

    public void Failed(DeliveryStage stage, string? detail = null, int? statusCode = null, string? enhancedStatus = null) =>
        Add(stage, false, detail, statusCode, enhancedStatus);

    public void AddHints(IEnumerable<string> hints)
    {
        foreach (var hint in hints)
        {
            if (!_hints.Contains(hint, StringComparer.Ordinal))
            {
                _hints.Add(hint);
            }
        }
    }

    private void Add(DeliveryStage stage, bool success, string? detail, int? statusCode, string? enhancedStatus)
    {
        _stages.Add(new DeliveryStageResult
        {
            Stage = stage,
            Success = success,
            Detail = detail,
            StatusCode = statusCode,
            EnhancedStatus = enhancedStatus,
            Duration = timeProvider.GetElapsedTime(_started),
        });
        _started = timeProvider.GetTimestamp();
    }
}
