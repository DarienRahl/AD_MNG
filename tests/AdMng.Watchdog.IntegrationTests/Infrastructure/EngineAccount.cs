namespace AdMng.Watchdog.IntegrationTests.Infrastructure;

/// <summary>The account a watchdog engine reads the lab with.</summary>
public enum EngineAccount
{
    /// <summary><c>odczyt.test</c>: an ordinary user (no tombstones, no PSO container) – the production-like case.</summary>
    ReadOnly,

    /// <summary>Administrator: also reads <c>CN=Deleted Objects</c> (Show Deleted), so deletions are seen at once.</summary>
    Administrator,
}
