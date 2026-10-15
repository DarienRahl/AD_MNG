namespace AdMng.Watchdog.IntegrationTests.Infrastructure;

/// <summary>An engine reading as <c>odczyt.test</c> (the production-like, least-privileged case).</summary>
public sealed class ReadOnlyEngineFixture() : EngineFixture(EngineAccount.ReadOnly);
