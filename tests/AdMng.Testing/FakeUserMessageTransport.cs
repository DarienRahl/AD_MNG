using System.Collections.Concurrent;
using System.Net;
using AdMng.Core.Messaging;

namespace AdMng.Testing;

/// <summary>
/// In-memory <see cref="IDirectSmtpTransport"/> for tests: records every accepted message, answers per recipient address
/// with scripted outcomes (a queue per address, then <see cref="DefaultOutcome"/>) and counts connections. Thread-safe.
/// </summary>
public sealed class FakeUserMessageTransport : IDirectSmtpTransport, IDirectSmtpTransportFactory
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<UserMessageDelivery>> _scripted = new(StringComparer.OrdinalIgnoreCase);

    public FakeUserMessageTransport(UserMessageTransportKind kind = UserMessageTransportKind.DirectSmtp, string description = "smtp.contoso.test:587")
    {
        Kind = kind;
        Description = description;
    }

    public UserMessageTransportKind Kind { get; }

    public string Description { get; set; }

    /// <summary>What <see cref="GetStatusAsync"/> answers.</summary>
    public UserMessageTransportStatus Status { get; set; } = UserMessageTransportStatus.Available;

    /// <summary>Outcome of addresses without a scripted answer.</summary>
    public UserMessageDelivery DefaultOutcome { get; set; } = UserMessageDelivery.Accepted();

    /// <summary>Thrown by <see cref="ConnectAsync"/> when set (e.g. an authentication failure).</summary>
    public UserMessageTransportException? ConnectFailure { get; set; }

    /// <summary>Messages the "server" accepted, in order.</summary>
    public ConcurrentQueue<OutgoingUserMessage> Accepted { get; } = new();

    /// <summary>Every send attempt (accepted or not), in order.</summary>
    public ConcurrentQueue<OutgoingUserMessage> Attempts { get; } = new();

    public int Connections => _connections;

    public int DisposedConnections => _disposed;

    /// <summary>Test connection result.</summary>
    public SmtpConnectionTest TestResult { get; set; } = new() { Success = true, Stage = SmtpTestStage.Done, IsEncrypted = true, StartTlsOffered = true };

    /// <summary>Options and credential of the last <see cref="Create"/> (the factory side).</summary>
    public DirectSmtpOptions? LastOptions { get; private set; }

    public NetworkCredential? LastCredential { get; private set; }

    /// <summary>Called before every send (e.g. to cancel a run in the middle).</summary>
    public Action<OutgoingUserMessage>? BeforeSend { get; set; }

    private int _connections;
    private int _disposed;

    /// <summary>Queues answers for the next sends to <paramref name="address"/> (first To address).</summary>
    public void Script(string address, params UserMessageDelivery[] deliveries)
    {
        var queue = _scripted.GetOrAdd(address, _ => new ConcurrentQueue<UserMessageDelivery>());
        foreach (var delivery in deliveries)
        {
            queue.Enqueue(delivery);
        }
    }

    public static UserMessageDelivery Transient(int status = 451) => new(UserMessageDeliveryOutcome.Transient, $"smtp.{status}", status);

    public static UserMessageDelivery Rejected(int status = 550, string enhanced = "5.1.1") =>
        new(UserMessageDeliveryOutcome.Rejected, $"smtp.{status}.{enhanced}", status);

    public static UserMessageDelivery AuthenticationFailed() => new(UserMessageDeliveryOutcome.AuthenticationFailed, "smtp.535.5.7.139", 535, "smtp.basicAuthDisabled");

    public Task<UserMessageTransportStatus> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(Status);

    public Task<IUserMessageConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ConnectFailure is { } failure)
        {
            throw failure;
        }

        Interlocked.Increment(ref _connections);
        return Task.FromResult<IUserMessageConnection>(new Connection(this));
    }

    public Task<SmtpConnectionTest> TestConnectionAsync(CancellationToken cancellationToken) => Task.FromResult(TestResult);

    public IDirectSmtpTransport Create(DirectSmtpOptions options, NetworkCredential? credential)
    {
        LastOptions = options;
        LastCredential = credential;
        Description = options.Description;
        return this;
    }

    private UserMessageDelivery Answer(OutgoingUserMessage message)
    {
        BeforeSend?.Invoke(message);
        Attempts.Enqueue(message);
        var address = message.To[0].Address;
        var delivery = _scripted.TryGetValue(address, out var queue) && queue.TryDequeue(out var scripted) ? scripted : DefaultOutcome;
        if (delivery.IsAccepted)
        {
            Accepted.Enqueue(message);
        }

        return delivery;
    }

    private sealed class Connection(FakeUserMessageTransport owner) : IUserMessageConnection
    {
        public Task<UserMessageDelivery> SendAsync(OutgoingUserMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(owner.Answer(message));
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref owner._disposed);
            return ValueTask.CompletedTask;
        }
    }
}
