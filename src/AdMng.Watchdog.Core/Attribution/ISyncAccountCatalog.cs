namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// Detected Entra Connect / sync accounts (section 10.6.4, detection side <c>SyncAccountDetector</c>, kept in memory):
/// an actor that is a sync account gets tag <c>syncAccount</c> and does not raise a user's KeyCredentialChanged to High.
/// <see cref="NoSyncAccounts"/> is the fallback until the detector is wired in (conservative: no actor is a sync
/// account, so nothing is downgraded).
/// </summary>
public interface ISyncAccountCatalog
{
    /// <summary>True when <paramref name="sid"/> is a detected sync account.</summary>
    bool IsSyncAccount(string? sid);
}
