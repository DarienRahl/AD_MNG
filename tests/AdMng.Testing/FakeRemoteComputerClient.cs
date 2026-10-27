using AdMng.Core.Computers.Remote;
using AdMng.Core.Directory;

namespace AdMng.Testing;

/// <summary>
/// In-memory <see cref="IRemoteComputerClient"/> (K1): per host a scripted computer (system, users, services) or a failure,
/// and the record of every call with the host, the session's DC and identity and the arguments (restart message verbatim).
/// </summary>
public sealed class FakeRemoteComputerClient : IRemoteComputerClient
{
    private readonly Lock _gate = new();
    private readonly List<FakeRemoteCall> _calls = [];

    /// <summary>The computers by DNS name (case-insensitive).</summary>
    public Dictionary<string, FakeRemoteComputer> Computers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Hosts whose connect fails with this kind.</summary>
    public Dictionary<string, RemoteErrorKind> ConnectFailures { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every call: connect, then the operations (oldest first).</summary>
    public IReadOnlyList<FakeRemoteCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>The next operation waits until cancelled (to test cancellation on window close).</summary>
    public bool HoldNextOperation { get; set; }

    /// <summary>Completes when an operation started to wait (<see cref="HoldNextOperation"/>).</summary>
    public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<IRemoteComputerSession> ConnectAsync(RemoteTarget target, IDirectorySession session, RemoteOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        Record(new FakeRemoteCall("Connect", target.DnsHostName, session.Info.Server, session.Info.BoundIdentity) { Options = options });
        if (ConnectFailures.TryGetValue(target.DnsHostName, out var kind))
        {
            throw new RemoteActionException(kind);
        }

        if (!Computers.TryGetValue(target.DnsHostName, out var computer))
        {
            throw new RemoteActionException(RemoteErrorKind.Unreachable);
        }

        return Task.FromResult<IRemoteComputerSession>(new Session(this, target.DnsHostName, session, computer));
    }

    private void Record(FakeRemoteCall call)
    {
        lock (_gate)
        {
            _calls.Add(call);
        }
    }

    private sealed class Session(FakeRemoteComputerClient owner, string host, IDirectorySession session, FakeRemoteComputer computer) : IRemoteComputerSession
    {
        public bool IsDisposed { get; private set; }

        public async Task<RemoteOperatingSystem> GetOperatingSystemAsync(CancellationToken cancellationToken)
        {
            await StepAsync("OperatingSystem", null, cancellationToken).ConfigureAwait(false);
            return computer.OperatingSystem;
        }

        public async Task<IReadOnlyList<LoggedOnUser>> GetLoggedOnUsersAsync(CancellationToken cancellationToken)
        {
            await StepAsync("LoggedOnUsers", null, cancellationToken).ConfigureAwait(false);
            return computer.Users;
        }

        public async Task<IReadOnlyList<RemoteService>> GetServicesAsync(CancellationToken cancellationToken)
        {
            await StepAsync("Services", null, cancellationToken).ConfigureAwait(false);
            return computer.Services;
        }

        public async Task<GroupPolicyRefreshResult> RefreshGroupPolicyAsync(GroupPolicyRefreshTarget target, CancellationToken cancellationToken)
        {
            await StepAsync("RefreshGroupPolicy", target.ToString(), cancellationToken).ConfigureAwait(false);
            return computer.PolicyResult with { UserRefreshScheduled = target == GroupPolicyRefreshTarget.ComputerAndUsers };
        }

        public Task ScheduleRestartAsync(TimeSpan delay, string message, bool force, CancellationToken cancellationToken) =>
            StepAsync("ScheduleRestart", null, cancellationToken, delay, message, force);

        public Task AbortRestartAsync(CancellationToken cancellationToken) => StepAsync("AbortRestart", null, cancellationToken);

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            owner.Record(new FakeRemoteCall("Dispose", host, session.Info.Server, session.Info.BoundIdentity));
            return ValueTask.CompletedTask;
        }

        private async Task StepAsync(string operation, string? argument, CancellationToken cancellationToken, TimeSpan? delay = null, string? message = null, bool force = false)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            owner.Record(new FakeRemoteCall(operation, host, session.Info.Server, session.Info.BoundIdentity)
            {
                Argument = argument,
                Delay = delay,
                Message = message,
                Force = force,
            });
            if (owner.HoldNextOperation)
            {
                owner.HoldNextOperation = false;
                owner.Held.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (computer.Failures.TryGetValue(operation, out var kind))
            {
                throw new RemoteActionException(kind);
            }
        }
    }
}

/// <summary>A scripted remote computer.</summary>
public sealed class FakeRemoteComputer
{
    public RemoteOperatingSystem OperatingSystem { get; set; } =
        new("Microsoft Windows 11 Enterprise", "10.0.26200", 26200, 6899, new DateTimeOffset(2026, 10, 26, 6, 15, 0, TimeSpan.Zero));

    public List<LoggedOnUser> Users { get; } = [];

    public List<RemoteService> Services { get; } = [];

    public GroupPolicyRefreshResult PolicyResult { get; set; } = new(true, new DateTimeOffset(2026, 10, 27, 9, 1, 0, TimeSpan.Zero), 0, false);

    /// <summary>Operations ("OperatingSystem", "ScheduleRestart" …) that fail with a kind.</summary>
    public Dictionary<string, RemoteErrorKind> Failures { get; } = new(StringComparer.Ordinal);
}

/// <summary>One recorded call of <see cref="FakeRemoteComputerClient"/>.</summary>
public sealed record FakeRemoteCall(string Operation, string Host, string Server, string Identity)
{
    public string? Argument { get; init; }

    public TimeSpan? Delay { get; init; }

    public string? Message { get; init; }

    public bool Force { get; init; }

    public RemoteOptions? Options { get; init; }
}
