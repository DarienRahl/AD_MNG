using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>What one ingestion pass needs besides the source (<see cref="SecurityLogIngestor"/>).</summary>
public sealed record IngestionContext
{
    public required AttributionSettings Settings { get; init; }

    public required SecurityEventReduction Reduction { get; init; }

    /// <summary>Whether a System/Computer value is a known DC (<see cref="EventSourceGuard"/>).</summary>
    public required Func<string, bool> IsKnownDomainController { get; init; }

    /// <summary>DNS name of the domain (WatchEvent.Domain of created events).</summary>
    public string? Domain { get; init; }

    /// <summary><c>Directory.DisabledKinds</c> – event-log-only kinds listed there are not created.</summary>
    public IReadOnlyCollection<WatchEventKind> DisabledKinds { get; init; } = [];
}
