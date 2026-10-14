namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// The domain-controller facts attribution needs (sections 10.11, 11.1, 11.4): the writable DCs of this domain
/// (default attribution sources), every DC of the forest (<see cref="EventSourceGuard"/>), invocationId → host and the
/// PDC emulator (lockout sources). The detection side's <c>DomainControllerDirectory</c> is the authoritative
/// implementation; <see cref="SessionDomainControllerCatalog"/> is the degraded fallback. Host names are DNS names,
/// compared case-insensitively without a trailing dot.
/// </summary>
public interface IDomainControllerCatalog
{
    /// <summary>False for a fallback that only knows the configured DCs and the watched DC (the engine reports health
    /// Warning <c>attribution.dcDirectory</c>).</summary>
    bool IsAuthoritative { get; }

    /// <summary>Writable DCs of this domain (RODCs and other domains' DCs excluded).</summary>
    Task<IReadOnlyList<string>> GetAttributionSourcesAsync(CancellationToken cancellationToken);

    /// <summary>Every DC of the forest.</summary>
    Task<IReadOnlyList<string>> GetKnownDomainControllersAsync(CancellationToken cancellationToken);

    /// <summary>The DC whose NTDS Settings object has <paramref name="invocationId"/>; null when unknown.</summary>
    Task<string?> ResolveHostAsync(Guid invocationId, CancellationToken cancellationToken);

    /// <summary>The PDC emulator of this domain; null when unknown.</summary>
    Task<string?> GetPdcHostAsync(CancellationToken cancellationToken);
}
