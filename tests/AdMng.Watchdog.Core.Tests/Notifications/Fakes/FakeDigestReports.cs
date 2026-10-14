using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Notifications.Digest;

namespace AdMng.Watchdog.Core.Tests.Notifications.Fakes;

/// <summary>An <see cref="IDigestDirectoryReports"/> returning fixed results and counting the queries.</summary>
internal sealed class FakeDigestReports : IDigestDirectoryReports
{
    public InactiveAccountsResult? Inactive { get; set; } = new();

    public HygieneCounters? Hygiene { get; set; } = new();

    public int InactiveQueries { get; private set; }

    public int HygieneQueries { get; private set; }

    public Task<InactiveAccountsResult?> GetInactiveAccountsAsync(InactiveAccountsScan settings, DateTimeOffset now, int maxRows, CancellationToken cancellationToken)
    {
        InactiveQueries++;
        return Task.FromResult(Inactive is null ? null : Inactive with { Rows = [.. Inactive.Rows.Take(maxRows)] });
    }

    public Task<HygieneCounters?> GetHygieneAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        HygieneQueries++;
        return Task.FromResult(Hygiene);
    }
}
