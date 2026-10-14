using System.Collections.Concurrent;
using System.Globalization;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// Bounds the on-demand Security-log queries (4624/4771/4776/4625, section 11.4 #6): at most one such query per DC at a
/// time, a per-query timeout (<c>Attribution.SourceQueryTimeout</c>, waiting for the DC slot included; cancellation
/// reaches the reader, which cancels the event-log operation), and the lookup metrics of
/// <see cref="DcLogHealthTracker"/>. Timeouts run on the injected <see cref="TimeProvider"/>.
/// </summary>
public sealed class SourceQueryGate(DcLogHealthTracker tracker, TimeProvider timeProvider) : IDisposable
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Runs <paramref name="query"/> against <paramref name="host"/> within <paramref name="timeout"/>.</summary>
    public async Task<SourceQueryResult<T>> RunAsync<T>(string host, TimeSpan timeout, Func<CancellationToken, Task<T>> query,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(query);
        var gate = _gates.GetOrAdd(HostNames.Normalize(host), static _ => new SemaphoreSlim(1, 1));
        using var timeoutSource = new CancellationTokenSource(timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var started = timeProvider.GetTimestamp();
        var entered = false;
        try
        {
            await gate.WaitAsync(linked.Token).ConfigureAwait(false);
            entered = true;
            var value = await query(linked.Token).ConfigureAwait(false);
            tracker.RecordLookup(host, timeProvider.GetElapsedTime(started), timedOut: false);
            return new SourceQueryResult<T>(true, value, false, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            tracker.RecordLookup(host, timeProvider.GetElapsedTime(started), timedOut: true);
            return new SourceQueryResult<T>(false, default, true, null);
        }
        catch (SecurityLogAccessException ex)
        {
            var code = ex.Win32Error?.ToString(CultureInfo.InvariantCulture) ?? "access";
            tracker.RecordFailure(host, code);
            return new SourceQueryResult<T>(false, default, false, code);
        }
        finally
        {
            if (entered)
            {
                gate.Release();
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var gate in _gates.Values)
        {
            gate.Dispose();
        }
    }
}
