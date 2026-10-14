namespace AdMng.Watchdog.Core.Attribution;

/// <summary>Fallback <see cref="ISyncAccountCatalog"/>: knows no sync account.</summary>
public sealed class NoSyncAccounts : ISyncAccountCatalog
{
    /// <inheritdoc/>
    public bool IsSyncAccount(string? sid) => false;
}
