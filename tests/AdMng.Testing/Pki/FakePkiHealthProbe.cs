using System.Collections.Concurrent;
using AdMng.Core.Pki;
using AdMng.Presentation.Pki;

namespace AdMng.Testing.Pki;

/// <summary>
/// An in-memory <see cref="IPkiHealthProbe"/> (P1, docs/dev/p-certificate-services.md 2.10): no HTTP. Answers the result
/// set for a URL (<see cref="Set"/>), else <see cref="PkiHttpOutcome.NotChecked"/>; records every call. This file lives in
/// <c>AdMng.Testing</c> but is compiled into the projects that reference <c>AdMng.Presentation</c> (the Presentation and UI
/// tests link it): <c>AdMng.Testing</c> references Core only, and the Watchdog tests reference it too.
/// </summary>
public sealed class FakePkiHealthProbe : IPkiHealthProbe
{
    private readonly ConcurrentDictionary<string, PkiHttpCheckResult> _results = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<IReadOnlyList<PkiUrl>> _calls = new();

    /// <summary>Every call with the URLs it was given.</summary>
    public IReadOnlyList<IReadOnlyList<PkiUrl>> Calls => [.. _calls];

    /// <summary>The answer for <paramref name="url"/>.</summary>
    public void Set(string url, PkiHttpOutcome outcome, int? statusCode = null, CertificateSummary? certificate = null, CrlSummary? crl = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        _results[url] = new PkiHttpCheckResult(url, PkiUrlKind.Cdp, outcome, statusCode, certificate, crl);
    }

    public Task<IReadOnlyList<PkiHttpCheckResult>> CheckAsync(IReadOnlyList<PkiUrl> urls, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(urls);
        cancellationToken.ThrowIfCancellationRequested();
        _calls.Enqueue([.. urls]);
        var results = new List<PkiHttpCheckResult>(urls.Count);
        foreach (var url in urls)
        {
            results.Add(_results.TryGetValue(url.Url, out var set)
                ? set with { Kind = url.Kind }
                : new PkiHttpCheckResult(url.Url, url.Kind, PkiHttpOutcome.NotChecked, null, null, null));
            progress?.Report(results.Count);
        }

        return Task.FromResult<IReadOnlyList<PkiHttpCheckResult>>(results);
    }
}
