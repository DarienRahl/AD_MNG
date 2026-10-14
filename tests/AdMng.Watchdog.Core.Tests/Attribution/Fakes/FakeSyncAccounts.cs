using AdMng.Watchdog.Core.Attribution;

namespace AdMng.Watchdog.Core.Tests.Attribution.Fakes;

/// <summary>Sync accounts listed by the test.</summary>
internal sealed class FakeSyncAccounts(params string[] sids) : ISyncAccountCatalog
{
    public bool IsSyncAccount(string? sid) => sid is not null && sids.Contains(sid, StringComparer.OrdinalIgnoreCase);
}
