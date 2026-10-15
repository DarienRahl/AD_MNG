namespace AdMng.Watchdog.IntegrationTests.Infrastructure;

/// <summary>An engine reading as Administrator (Deleted Objects readable).</summary>
public sealed class AdministratorEngineFixture() : EngineFixture(EngineAccount.Administrator);
