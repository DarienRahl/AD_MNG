using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Tests.Rules.Fakes;

/// <summary>In-memory <see cref="IConfigStore"/> (versions, conflicts; secrets and audit are not kept).</summary>
internal sealed class FakeConfigStore(TimeProvider timeProvider) : IConfigStore
{
    public ConfigEnvelope? Stored { get; set; }

    private int _reads;

    public int Reads => Volatile.Read(ref _reads);

    public Task<ConfigEnvelope?> GetAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _reads);
        return Task.FromResult(Stored);
    }

    public Task<ConfigEnvelope> SaveAsync(WatchdogConfiguration configuration, long expectedVersion, PrincipalRef by, string summary,
        IReadOnlyList<SecretChange> secretChanges, CancellationToken cancellationToken)
    {
        var actual = Stored?.Version ?? 0;
        if (actual != expectedVersion)
        {
            throw new ConfigVersionConflictException(expectedVersion, actual);
        }

        Stored = new ConfigEnvelope { Version = actual + 1, Configuration = configuration, UpdatedAt = timeProvider.GetUtcNow(), UpdatedBy = by.Account };
        return Task.FromResult(Stored);
    }

    public Task<ConfigAuditPage> GetAuditAsync(long? before, int limit, CancellationToken cancellationToken) =>
        Task.FromResult(new ConfigAuditPage());
}
