using AdMng.Core.Computers;
using AdMng.Presentation.Computers;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Presentation.Tests.Computers;

/// <summary>The computers (K1) registration as the application's container resolves it.</summary>
public sealed class ComputersRegistrationTests
{
    [Fact]
    public void TheReleaseCatalog_FromTheContainer_HoldsTheBuiltInReleases()
    {
        using var provider = new ServiceCollection().AddAdMngComputers().BuildServiceProvider();

        var catalog = provider.GetRequiredService<WindowsReleaseCatalog>();

        // The container must not pick the IEnumerable<WindowsRelease> constructor (an empty catalogue: no release known,
        // nothing past its end of support – the computer review's end-of-support tile stayed at 0 in the application).
        Assert.Same(catalog, provider.GetRequiredService<WindowsReleaseCatalog>());
        var windows7 = WindowsReleaseCatalog.BuiltIn().First(r => r.Release == "Windows 7 SP1");
        Assert.Equal(windows7, catalog.Find(windows7.Build, windows7.Edition));
        Assert.NotNull(catalog.Find(26100, WindowsReleaseCatalog.EditionOf("Windows 11 Pro")!.Value));
    }
}
