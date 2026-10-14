using AdMng.Watchdog.Core.Attribution;

namespace AdMng.Watchdog.Core.Tests.Attribution.Fakes;

/// <summary>An authoritative DC directory fixed by the test.</summary>
internal sealed class FakeDomainControllers(params string[] writable) : IDomainControllerCatalog
{
    public List<string> Writable { get; } = [.. writable];

    public List<string> OtherDomainControllers { get; } = [];

    public string? Pdc { get; set; } = writable.FirstOrDefault();

    public Dictionary<Guid, string> InvocationIds { get; } = [];

    public bool IsAuthoritative { get; set; } = true;

    /// <summary>Thrown by <see cref="GetAttributionSourcesAsync"/> when set (engine failure path).</summary>
    public Exception? Failure { get; set; }

    public Task<IReadOnlyList<string>> GetAttributionSourcesAsync(CancellationToken cancellationToken) =>
        Failure is { } failure ? Task.FromException<IReadOnlyList<string>>(failure) : Task.FromResult<IReadOnlyList<string>>([.. Writable]);

    public Task<IReadOnlyList<string>> GetKnownDomainControllersAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([.. Writable, .. OtherDomainControllers]);

    public Task<string?> ResolveHostAsync(Guid invocationId, CancellationToken cancellationToken) =>
        Task.FromResult(InvocationIds.GetValueOrDefault(invocationId));

    public Task<string?> GetPdcHostAsync(CancellationToken cancellationToken) => Task.FromResult(Pdc);
}
