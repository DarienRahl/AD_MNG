using System.Collections.Concurrent;
using AdMng.Core.Pki.Tls;

namespace AdMng.Testing.Pki;

/// <summary>
/// An in-memory <see cref="IDomainControllerTlsProbe"/> (P1, docs/dev/p-certificate-services.md 2.10): no TCP, no TLS.
/// Answers the result set for a host and port (<see cref="Set"/>), else <see cref="DefaultOutcome"/> with no chain; records
/// every call and the highest number of concurrent probes (the snapshot service probes at most 4 at a time).
/// </summary>
public sealed class FakeDomainControllerTlsProbe : IDomainControllerTlsProbe
{
    private readonly ConcurrentDictionary<(string Host, int Port), TlsProbeResult> _results = new();
    private readonly ConcurrentQueue<(string Host, int Port, TimeSpan Timeout)> _calls = new();
    private int _inFlight;
    private int _maxConcurrency;

    /// <summary>The outcome of a host and port nothing was set for (default <see cref="TlsProbeOutcome.PortClosed"/>).</summary>
    public TlsProbeOutcome DefaultOutcome { get; set; } = TlsProbeOutcome.PortClosed;

    /// <summary>A delay before each answer (to observe concurrency and cancellation).</summary>
    public TimeSpan Delay { get; set; }

    /// <summary>Every probe in the order it started.</summary>
    public IReadOnlyList<(string Host, int Port, TimeSpan Timeout)> Calls => [.. _calls];

    /// <summary>The most probes that ran at the same time.</summary>
    public int MaxConcurrency => Volatile.Read(ref _maxConcurrency);

    /// <summary>The answer for <paramref name="host"/>:<paramref name="port"/> (host compared case-insensitively).</summary>
    public void Set(string host, int port, TlsProbeOutcome outcome, params byte[][] presentedChain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        _results[(host.ToLowerInvariant(), port)] = new TlsProbeResult(host, port, outcome,
            outcome == TlsProbeOutcome.Ok ? System.Security.Authentication.SslProtocols.Tls12 : null, presentedChain, null);
    }

    public async Task<TlsProbeResult> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        _calls.Enqueue((host, port, timeout));
        var now = Interlocked.Increment(ref _inFlight);
        int seen;
        while ((seen = Volatile.Read(ref _maxConcurrency)) < now && Interlocked.CompareExchange(ref _maxConcurrency, now, seen) != seen)
        {
        }

        try
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return _results.TryGetValue((host.ToLowerInvariant(), port), out var result)
                ? result
                : new TlsProbeResult(host, port, DefaultOutcome, null, [], null);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}
