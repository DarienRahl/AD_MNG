using AdMng.Watchdog.Core.Attribution;

namespace AdMng.Watchdog.Core.Tests.Attribution.Fakes;

/// <summary>Reverse DNS answered from a table.</summary>
internal sealed class FakeReverseDns : IReverseDnsResolver
{
    public Dictionary<string, string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int Calls { get; private set; }

    public Task<string?> ResolveAsync(string ipAddress, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Names.GetValueOrDefault(ipAddress));
    }
}
