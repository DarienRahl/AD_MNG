using System.Net;
using AdMng.Core.Computers.Remote;

namespace AdMng.Testing;

/// <summary>DNS stand-in (K1): host → addresses; unknown hosts resolve to nothing. Records every lookup.</summary>
public sealed class FakeHostAddressResolver : IHostAddressResolver
{
    private readonly Lock _gate = new();
    private readonly List<string> _lookups = [];

    public Dictionary<string, IPAddress[]> Addresses { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Lookups
    {
        get
        {
            lock (_gate)
            {
                return [.. _lookups];
            }
        }
    }

    public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _lookups.Add(host);
        }

        return Task.FromResult<IReadOnlyList<IPAddress>>(Addresses.TryGetValue(host, out var addresses) ? addresses : []);
    }

    /// <summary>Adds <paramref name="host"/> with its addresses.</summary>
    public FakeHostAddressResolver Add(string host, params string[] addresses)
    {
        Addresses[host] = [.. addresses.Select(IPAddress.Parse)];
        return this;
    }
}

/// <summary>Ping stand-in (K1): hosts that answer (with a round trip) and hosts that do not.</summary>
public sealed class FakePingService : IPingService
{
    public Dictionary<string, TimeSpan> Answering { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Pinged { get; } = [];

    public Task<PingResult> PingAsync(string host, int count, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Pinged.Add(host);
        var answer = Answering.TryGetValue(host, out var roundTrip) ? roundTrip : (TimeSpan?)null;
        return Task.FromResult(new PingResult(host, answer is null ? null : IPAddress.Parse("10.10.1.15"), [.. Enumerable.Repeat(answer, count)]));
    }
}
