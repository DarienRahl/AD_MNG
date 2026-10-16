using AdMng.Directory;
using AdMng.Watchdog.IntegrationTests.Infrastructure;

namespace AdMng.Watchdog.IntegrationTests;

public sealed class LabConnectionTests
{
    [Fact]
    public async Task ReadOnlyAccount_ReadsTheRootDseWatermark()
    {
        WatchdogLab.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var connector = new LdapDirectoryConnector();

        await using var session = await connector.ConnectAsync(
            WatchdogLab.Plain(WatchdogLab.ReadOnlyUser), WatchdogLab.Password(WatchdogLab.ReadOnlyPassword), ct);
        var rootDse = await session.GetAsync(string.Empty, ["highestCommittedUSN", "dsServiceName", "isSynchronized"], ct);

        Assert.NotNull(rootDse);
        Assert.True(rootDse.GetInt64("highestCommittedUSN") > 0);
        Assert.StartsWith("CN=NTDS Settings,", rootDse.GetString("dsServiceName"), StringComparison.OrdinalIgnoreCase);
        Assert.True(rootDse.GetBoolean("isSynchronized"));
    }
}
