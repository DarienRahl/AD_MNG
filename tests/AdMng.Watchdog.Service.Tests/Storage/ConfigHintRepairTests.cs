using System.Text.Json;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Service.Storage;
using AdMng.Watchdog.Service.Tests.Infrastructure;

namespace AdMng.Watchdog.Service.Tests.Storage;

/// <summary>
/// Hints written by an earlier version kept the first path segment – for webhook.site or ntfy.sh the whole secret URL.
/// The configuration store repairs such a document in place on its first read (same version, no audit entry) and never
/// saves an unsafe hint.
/// </summary>
public sealed class ConfigHintRepairTests
{
    private const string SecretUrl = "https://webhook.site/0f8fad5b-d9cb-469f-a165-70867728950e";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ALeakyStoredHint_IsRepairedInPlace_OnTheFirstRead()
    {
        await using var store = TestStore.Create();
        var json = JsonSerializer.Serialize(Configuration(SecretUrl), WatchdogJson.Options);
        await store.Database.WriteAsync(
            (c, t) => SqliteValues.Execute(c, t, "INSERT INTO config (id, version, json, updated_at, updated_by) VALUES (1, 7, $json, 0, 'CONTOSO\\jan.kowalski');", ("$json", json)),
            Ct);

        var envelope = await store.Store.GetAsync(Ct);

        Assert.NotNull(envelope);
        Assert.Equal(7, envelope.Version);
        Assert.Equal("https://webhook.site/…", envelope.Configuration.Channels[0].Url.Hint);
        var raw = store.Database.Read(c => SqliteValues.Scalar<string>(c, null, "SELECT json FROM config WHERE id = 1;"));
        Assert.DoesNotContain("0f8fad5b", raw, StringComparison.Ordinal);
        Assert.Equal(7L, store.Database.Read(c => SqliteValues.Scalar<long>(c, null, "SELECT version FROM config WHERE id = 1;")));
        Assert.Equal(0L, store.Database.Read(c => SqliteValues.Scalar<long>(c, null, "SELECT count(*) FROM config_audit;")));
    }

    [Fact]
    public async Task Save_NeverStoresAnUnsafeHint()
    {
        await using var store = TestStore.Create();

        var saved = await store.Store.SaveAsync(Configuration(SecretUrl), 0, new PrincipalRef { Account = @"CONTOSO\jan.kowalski" }, "test", [], Ct);

        Assert.Equal("https://webhook.site/…", saved.Configuration.Channels[0].Url.Hint);
        var raw = store.Database.Read(c => SqliteValues.Scalar<string>(c, null, "SELECT json FROM config WHERE id = 1;"));
        Assert.DoesNotContain("0f8fad5b", raw, StringComparison.Ordinal);
    }

    private static WatchdogConfiguration Configuration(string hint) => new()
    {
        Channels =
        [
            new ChannelSettings
            {
                Id = "siem",
                Type = ChannelType.Webhook,
                Name = "SIEM Łódź",
                Url = new SecretField { IsSet = true, Hint = hint },
            },
        ],
    };
}
