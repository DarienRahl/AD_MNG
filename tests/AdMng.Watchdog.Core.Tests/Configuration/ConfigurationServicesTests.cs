using AdMng.Watchdog.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Watchdog.Core.Tests.Configuration;

public class ConfigurationServicesTests
{
    [Fact]
    public void AddWatchdogCore_ResolvesTheRedactorAndTheAuditor_AsSingletons()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 7, 30, 0, TimeSpan.Zero));
        using var provider = new ServiceCollection().AddSingleton<TimeProvider>(clock).AddWatchdogCore().BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<ConfigRedactor>(), provider.GetRequiredService<ConfigRedactor>());
        var auditor = provider.GetRequiredService<ConfigChangeAuditor>();
        Assert.Same(auditor, provider.GetRequiredService<ConfigChangeAuditor>());

        var audit = auditor.Compare(SampleConfiguration.Create(), SampleConfiguration.Create());
        Assert.Equal(clock.GetUtcNow(), auditor.CreateEvent(audit, new() { Account = "CONTOSO\\adm.jkowalska" }).DetectedAt);
    }
}
