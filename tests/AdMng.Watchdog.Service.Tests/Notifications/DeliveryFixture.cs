using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Notifications;
using AdMng.Watchdog.Service.Tests.Fakes;
using AdMng.Watchdog.Service.Tests.Infrastructure;

namespace AdMng.Watchdog.Service.Tests.Notifications;

/// <summary>The real transports over a real SQLite secret store, with fake configuration, clock, token client and
/// health registry (transport and delivery-tester tests).</summary>
public sealed class DeliveryFixture : IAsyncDisposable
{
    private DeliveryFixture(TestStore store, ServiceOptions options, string environment)
    {
        Store = store;
        Options = new StaticOptionsMonitor<ServiceOptions>(options);
        Environment = new FakeHostEnvironment(environment);
        Guard = new DestinationGuard(store.Store, Options);
        Limiters = new ChannelRateLimiters(store.Clock);
        var sender = new HttpChannelSender(HttpClients, store.Clock, Log.For<HttpChannelSender>());
        Smtp = new SmtpEmailTransport(Config, Guard, new SmtpOAuthTokenProvider(Tokens, Guard), Limiters, Environment, store.Clock,
            Log.For<SmtpEmailTransport>());
        Teams = new TeamsWorkflowTransport(Config, Guard, new TeamsTokenProvider(Tokens, Guard), sender, Limiters, Environment);
        Webhook = new WebhookTransport(Config, Guard, sender, Limiters, Environment, store.Clock);
        Tester = new DeliveryTester(Config, Smtp, Teams, Webhook, store.Clock);
    }

    public TestStore Store { get; }

    public Microsoft.Extensions.Time.Testing.FakeTimeProvider Clock => Store.Clock;

    public StaticOptionsMonitor<ServiceOptions> Options { get; }

    public FakeHostEnvironment Environment { get; }

    public FakeConfigProvider Config { get; } = new();

    public FakeHealthRegistry Health { get; } = new();

    public FakeEntraTokenClient Tokens { get; } = new();

    public TestHttpClientFactory HttpClients { get; } = new();

    public TestLog Log { get; } = new();

    public DestinationGuard Guard { get; }

    public ChannelRateLimiters Limiters { get; }

    public SmtpEmailTransport Smtp { get; }

    public TeamsWorkflowTransport Teams { get; }

    public WebhookTransport Webhook { get; }

    public DeliveryTester Tester { get; }

    public static DeliveryFixture Create(ServiceOptions? options = null, string environment = "Production") =>
        new(TestStore.Create(), options ?? new ServiceOptions(), environment);

    public void Configure(WatchdogConfiguration configuration) => Config.Set(configuration);

    /// <summary>Stores a secret with the binding it would get on save.</summary>
    public Task StoreSecretAsync(string name, string value, string binding) =>
        Store.Store.SetAsync(name, new SecretValue(value), binding, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        Limiters.Dispose();
        await Store.DisposeAsync();
    }
}
