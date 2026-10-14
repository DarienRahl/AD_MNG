using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Service.Attribution;

/// <summary><see cref="ISecurityEventSource"/> on hosts without the Windows Event Log API (Linux lab/dev, CI): not
/// available; every read fails as access to an unreachable log.</summary>
public sealed class NullSecurityEventSource : ISecurityEventSource
{
    public bool IsAvailable => false;

    public Task<SecurityLogInfo> GetLogInfoAsync(string host, string logName, CancellationToken cancellationToken) =>
        Task.FromException<SecurityLogInfo>(Unavailable(host));

    public IAsyncEnumerable<string> ReadAsync(string host, SecurityEventQuery query, CancellationToken cancellationToken) =>
        Fail(host);

    private static async IAsyncEnumerable<string> Fail(string host)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        throw Unavailable(host);
#pragma warning disable CS0162 // unreachable: makes the method an iterator
        yield break;
#pragma warning restore CS0162
    }

    private static SecurityLogAccessException Unavailable(string host) =>
        new(host, null, "Security log reading is not available on this platform.");
}
