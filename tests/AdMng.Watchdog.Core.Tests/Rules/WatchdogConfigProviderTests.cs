using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Rules;
using AdMng.Watchdog.Core.Tests.Rules.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Watchdog.Core.Tests.Rules;

public class WatchdogConfigProviderTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.Zero));

    private WatchdogConfigProvider Create(FakeConfigStore store) =>
        new(store, new DefaultConfigurationFactory(), NullLogger<WatchdogConfigProvider>.Instance);

    [Fact]
    public void Current_BeforeTheFirstReload_IsTheDefaultConfigurationVersion0()
    {
        using var provider = Create(new FakeConfigStore(_clock));

        Assert.Equal(0, provider.Current.Version);
        Assert.Equal(DefaultRules.Ids, provider.Current.Configuration.Rules.Select(r => r.Id));
        Assert.False(provider.Current.IsRedacted);
    }

    [Fact]
    public async Task Reload_WithoutAStoredDocument_KeepsTheDefaultsAndRaisesNothing()
    {
        using var provider = Create(new FakeConfigStore(_clock));
        var raised = 0;
        provider.Changed += (_, _) => raised++;

        var envelope = await provider.ReloadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, envelope.Version);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task Reload_MergesMissingBuiltInRulesInMemory_WithoutTouchingStoredRules()
    {
        var store = new FakeConfigStore(_clock);
        var edited = DefaultRules.Find(DefaultRules.Lockouts)! with { Enabled = false };
        var custom = new RuleSettings { Id = "konta-łódź", Name = "Konta w Łodzi" };
        await store.SaveAsync(new WatchdogConfiguration { Rules = [custom, edited] }, 0, new() { Account = "CONTOSO\\adm.jkowalska" }, "initial", [], TestContext.Current.CancellationToken);
        using var provider = Create(store);

        var envelope = await provider.ReloadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, envelope.Version);
        var rules = envelope.Configuration.Rules;
        Assert.Equal((custom.Id, custom.Name), (rules[0].Id, rules[0].Name));
        Assert.False(rules[1].Enabled);
        Assert.Equal(DefaultRules.Ids.Count + 1, rules.Count);
        Assert.Equal(2, store.Stored!.Configuration.Rules.Count);   // never written back
    }

    [Fact]
    public async Task Reload_RepairsExplicitNullsAndDropsNullEntries()
    {
        var store = new FakeConfigStore(_clock)
        {
            Stored = new ConfigEnvelope
            {
                Version = 4,
                Configuration = new WatchdogConfiguration { Smtp = null!, Channels = [null!], RecipientGroups = null!, Rules = [null!] },
            },
        };
        using var provider = Create(store);

        var configuration = (await provider.ReloadAsync(TestContext.Current.CancellationToken)).Configuration;

        Assert.NotNull(configuration.Smtp);
        Assert.Empty(configuration.Channels);
        Assert.Empty(configuration.RecipientGroups);
        Assert.Equal(DefaultRules.Ids, configuration.Rules.Select(r => r.Id));
    }

    [Fact]
    public async Task Changed_IsRaisedOncePerNewVersion_AndAFailingHandlerDoesNotStopOthers()
    {
        var store = new FakeConfigStore(_clock);
        using var provider = Create(store);
        var seen = new List<long>();
        provider.Changed += (_, _) => throw new InvalidOperationException("handler failure");
        provider.Changed += (_, e) => seen.Add(e.Version);
        var ct = TestContext.Current.CancellationToken;

        await store.SaveAsync(new WatchdogConfiguration(), 0, new() { Account = "CONTOSO\\adm.jkowalska" }, "initial", [], ct);
        await provider.ReloadAsync(ct);
        await provider.ReloadAsync(ct);
        await store.SaveAsync(new WatchdogConfiguration { General = new GeneralSettings { InstanceName = "Nadzór – Gdańsk" } }, 1, new() { Account = "CONTOSO\\adm.jkowalska" }, "zmiana", [], ct);
        var latest = await provider.ReloadAsync(ct);

        Assert.Equal([1L, 2L], seen);
        Assert.Same(latest, provider.Current);
        Assert.Equal("Nadzór – Gdańsk", provider.Current.Configuration.General.InstanceName);
    }

    [Fact]
    public async Task ConcurrentReloads_AreSerialized()
    {
        var store = new FakeConfigStore(_clock);
        await store.SaveAsync(new WatchdogConfiguration(), 0, new() { Account = "CONTOSO\\adm.jkowalska" }, "initial", [], TestContext.Current.CancellationToken);
        using var provider = Create(store);
        var raised = 0;
        provider.Changed += (_, _) => Interlocked.Increment(ref raised);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => provider.ReloadAsync(TestContext.Current.CancellationToken)));

        Assert.Equal(1, raised);
        Assert.Equal(8, store.Reads);
    }

    [Fact]
    public void Effective_IsIdempotent()
    {
        var once = WatchdogConfigProvider.Effective(new DefaultConfigurationFactory().Create());

        var twice = WatchdogConfigProvider.Effective(once);

        Assert.Equal(once.Rules.Select(r => r.Id), twice.Rules.Select(r => r.Id));
    }
}
