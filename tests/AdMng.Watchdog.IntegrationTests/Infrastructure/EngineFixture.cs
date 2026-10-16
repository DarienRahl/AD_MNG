namespace AdMng.Watchdog.IntegrationTests.Infrastructure;

/// <summary>
/// One watchdog engine per test class (class fixture): built and baselined once, then shared by the class's tests,
/// which run one after another and look only at their own objects. Nothing is started without the lab.
/// </summary>
public abstract class EngineFixture(EngineAccount account) : IAsyncLifetime
{
    private EngineHost? _host;

    public EngineHost Host => _host ?? throw new InvalidOperationException(WatchdogLab.SkipReason);

    public async ValueTask InitializeAsync()
    {
        if (WatchdogLab.IsConfigured)
        {
            _host = await EngineHost.StartAsync(account, null, CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }
}
