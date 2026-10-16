using AdMng.Watchdog.Service.Hosting;

namespace AdMng.Watchdog.Service.Tests.Hosting;

/// <summary><c>--smoke-test</c> passes on this platform (spec 19.1, CI gate).</summary>
public sealed class SmokeTestTests
{
    [Fact]
    public async Task SmokeTest_ExitsZero()
    {
        Assert.Equal(0, await SmokeTest.RunAsync([]));
    }
}
