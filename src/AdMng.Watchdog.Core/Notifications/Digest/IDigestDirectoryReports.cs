using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Core.Notifications.Digest;

/// <summary>
/// The live directory queries of the weekly digest sections (13.4, 10.17): inactive accounts and hygiene counters.
/// Results are only held in memory while the digest is rendered (rule 1). A null result means the directory could not
/// be queried – the section then says so instead of showing zeros.
/// </summary>
public interface IDigestDirectoryReports
{
    /// <summary>Inactive accounts with at most <paramref name="maxRows"/> rows.</summary>
    Task<InactiveAccountsResult?> GetInactiveAccountsAsync(InactiveAccountsScan settings, DateTimeOffset now, int maxRows, CancellationToken cancellationToken);

    /// <summary>The hygiene counters.</summary>
    Task<HygieneCounters?> GetHygieneAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
