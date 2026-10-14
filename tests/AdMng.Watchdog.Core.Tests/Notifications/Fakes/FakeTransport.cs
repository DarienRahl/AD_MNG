using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Tests.Notifications.Fakes;

/// <summary>An <see cref="INotificationTransport"/> that answers with queued results (then <see cref="Default"/>) and
/// records every message it was given.</summary>
internal sealed class FakeTransport(DeliveryChannelType channelType) : INotificationTransport
{
    private readonly Lock _gate = new();
    private readonly Queue<Func<OutboxMessage, CancellationToken, Task<DeliveryResult>>> _answers = new();
    private readonly List<OutboxMessage> _sent = [];

    public DeliveryChannelType ChannelType => channelType;

    /// <summary>The answer once the queue is empty (Delivered).</summary>
    public Func<OutboxMessage, CancellationToken, Task<DeliveryResult>> Default { get; set; } = (_, _) => Task.FromResult(DeliveryResult.Delivered);

    public IReadOnlyList<OutboxMessage> Sent
    {
        get
        {
            lock (_gate)
            {
                return [.. _sent];
            }
        }
    }

    public void Answer(DeliveryResult result) => Answer((_, _) => Task.FromResult(result));

    public void Answer(Func<OutboxMessage, CancellationToken, Task<DeliveryResult>> answer)
    {
        lock (_gate)
        {
            _answers.Enqueue(answer);
        }
    }

    public Task<DeliveryResult> SendAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        Func<OutboxMessage, CancellationToken, Task<DeliveryResult>> answer;
        lock (_gate)
        {
            _sent.Add(message);
            answer = _answers.Count > 0 ? _answers.Dequeue() : Default;
        }

        return answer(message, cancellationToken);
    }
}
