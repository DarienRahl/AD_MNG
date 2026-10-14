using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// Which logs to ingest (section 11.1): ForwardedEvents mode → the collector's ForwardedEvents log; otherwise
/// <c>Attribution.DomainControllers</c> – every entry must be a writable DC of this domain known to the DC directory,
/// unknown hosts are skipped with health Error <c>eventlog.&lt;host&gt;</c> "notADomainController" – or, when empty, the
/// directory's attribution sources. Also reports <c>attribution.dcDirectory</c> (Warning while only the degraded
/// fallback catalog is available).
/// </summary>
public sealed class AttributionSourceResolver(IDomainControllerCatalog domainControllers, Abstractions.IHealthRegistry health, TimeProvider timeProvider)
{
    /// <summary>Detail/argument of a configured host that is not a writable DC of this domain.</summary>
    public const string NotADomainController = "notADomainController";

    /// <summary>The logs to ingest.</summary>
    public async Task<IReadOnlyList<IngestionSource>> ResolveAsync(AttributionSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var now = timeProvider.GetUtcNow();
        health.Report(new HealthCheckDto
        {
            Code = "attribution.dcDirectory",
            State = domainControllers.IsAuthoritative ? HealthState.Ok : HealthState.Warning,
            Detail = domainControllers.IsAuthoritative ? null : "Domain controller discovery is not available – only the configured DCs and the watched DC are used.",
            CheckedAt = now,
        });

        if (settings.Source == AttributionSourceKind.ForwardedEvents)
        {
            if (string.IsNullOrWhiteSpace(settings.Collector))
            {
                health.Report(new HealthCheckDto { Code = "attribution.collector", State = HealthState.Error, Detail = "ForwardedEvents mode needs a collector.", CheckedAt = now });
                return [];
            }

            return [new IngestionSource(HostNames.Normalize(settings.Collector), "ForwardedEvents")];
        }

        var writable = (await domainControllers.GetAttributionSourcesAsync(cancellationToken).ConfigureAwait(false))
            .Select(HostNames.Normalize).Distinct(StringComparer.Ordinal).ToList();
        var configured = (settings.DomainControllers ?? []).Where(h => !string.IsNullOrWhiteSpace(h)).Select(HostNames.Normalize).Distinct(StringComparer.Ordinal).ToList();
        if (configured.Count == 0)
        {
            return [.. writable.Select(h => new IngestionSource(h, "Security"))];
        }

        var sources = new List<IngestionSource>();
        foreach (var host in configured)
        {
            if (writable.Contains(host))
            {
                sources.Add(new IngestionSource(host, "Security"));
            }
            else
            {
                health.Report(new HealthCheckDto
                {
                    Code = "eventlog." + host,
                    State = HealthState.Error,
                    Detail = NotADomainController,
                    Args = [host, NotADomainController],
                    CheckedAt = now,
                });
            }
        }

        return sources;
    }

    /// <summary>The DCs whose records correlation considers: the ingested DCs, or in ForwardedEvents mode the
    /// directory's attribution sources (the collector relays their records).</summary>
    public async Task<IReadOnlyList<string>> CorrelationHostsAsync(AttributionSettings settings, IReadOnlyList<IngestionSource> sources,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(sources);
        if (settings.Source != AttributionSourceKind.ForwardedEvents)
        {
            return [.. sources.Select(s => s.Host)];
        }

        return [.. (await domainControllers.GetAttributionSourcesAsync(cancellationToken).ConfigureAwait(false)).Select(HostNames.Normalize).Distinct(StringComparer.Ordinal)];
    }
}
