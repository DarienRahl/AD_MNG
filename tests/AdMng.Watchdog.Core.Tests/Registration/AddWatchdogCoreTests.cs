using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Watchdog.Core.Tests.Registration;

public class AddWatchdogCoreTests
{
    [Fact]
    public void RegistersTheSystemTimeProvider_WhenNoneIsRegistered()
    {
        var services = new ServiceCollection().AddWatchdogCore();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(TimeProvider));
        Assert.Same(TimeProvider.System, descriptor.ImplementationInstance);
    }

    [Fact]
    public void KeepsATimeProviderRegisteredBefore()
    {
        var fake = new FakeTimeProvider();
        var services = new ServiceCollection().AddSingleton<TimeProvider>(fake).AddWatchdogCore();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(TimeProvider));
        Assert.Same(fake, descriptor.ImplementationInstance);
    }
}
