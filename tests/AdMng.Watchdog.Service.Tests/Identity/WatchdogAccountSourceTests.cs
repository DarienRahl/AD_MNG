using System.Net;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Identity;
using AdMng.Watchdog.Service.Tests.Fakes;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using AdMng.Watchdog.Testing.Scenarios;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Watchdog.Service.Tests.Identity;

/// <summary>
/// The connection of the detection's <c>watchdog-self</c> rule with the own-account monitor (stage B design 9.3): the
/// identity from the bootstrap principal and the monitor's lookup, the keytab tool's status.json as rotation record, and
/// the service-side protection of the account against <c>excludedSubtrees</c>.
/// </summary>
public sealed class WatchdogAccountSourceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 3, 15, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _folder = System.IO.Directory.CreateTempSubdirectory("admng-wd-status-").FullName;

    private string StatusFile => Path.Combine(_folder, "status.json");

    public void Dispose() => System.IO.Directory.Delete(_folder, recursive: true);

    [Theory]
    [InlineData("ADMNG-WD$@CONTOSO.TEST", "ADMNG-WD$")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Identity_ComesFromTheBootstrapPrincipal(string? principal, string? expected)
    {
        var source = Source(new TestClock(Start), principal);

        Assert.Equal(expected, source.Identity?.SamAccountName);
    }

    [Fact]
    public void Update_AddsTheMonitorsLookup_AndKeepsKnownValues()
    {
        var source = Source(new TestClock(Start));
        var guid = Guid.Parse("0b6f2c1e-7d3a-4e59-9c8b-1a2b3c4d5e6f");

        source.Update(guid, "S-1-5-21-1-2-3-1601");
        source.Update(null, null);

        Assert.Equal(new WatchdogAccountIdentity { SamAccountName = "ADMNG-WD$", ObjectGuid = guid, Sid = "S-1-5-21-1-2-3-1601" }, source.Identity);
    }

    [Fact]
    public async Task GetRotation_ReadsStatusJson()
    {
        var source = Source(new TestClock(Start));
        await WriteStatusAsync(8, KeytabStatusResults.Ok);

        var record = await source.GetRotationAsync(8, Ct);

        Assert.Equal(new KeytabRotationRecord(8, Start, KeytabStatusResults.Ok), record);
    }

    [Fact]
    public async Task GetRotation_MissingOrMalformedFile_IsNull()
    {
        var source = Source(new TestClock(Start));

        Assert.Null(await source.GetRotationAsync(8, Ct));
        await File.WriteAllTextAsync(StatusFile, "{ \"kvno\": ", Ct);
        Assert.Null(await source.GetRotationAsync(8, Ct));
    }

    [Fact]
    public async Task GetRotation_WaitsForTheToolToRecordTheNewKeyVersion()
    {
        var clock = new TestClock(Start);
        var source = Source(clock);
        await WriteStatusAsync(7, KeytabStatusResults.Ok);

        var pending = source.GetRotationAsync(8, Ct);
        Assert.False(pending.IsCompleted);
        await WriteStatusAsync(8, KeytabStatusResults.ReplicationPending);
        clock.Advance(WatchdogAccountSource.PollInterval);

        Assert.Equal(new KeytabRotationRecord(8, Start, KeytabStatusResults.ReplicationPending), await pending);
    }

    [Fact]
    public async Task GetRotation_GivesUpAfterTheBoundedWait()
    {
        var clock = new TestClock(Start);
        var source = Source(clock);
        await WriteStatusAsync(7, KeytabStatusResults.Ok);

        var pending = source.GetRotationAsync(9, Ct);
        clock.Advance(WatchdogAccountSource.MaxWait + WatchdogAccountSource.PollInterval);

        Assert.Equal(7, (await pending)!.Kvno);
    }

    [Fact]
    public async Task ExcludingTheSubtreeOfTheOwnAccount_NeedsTheBootstrapFlag()
    {
        var own = Guid.Parse("0b6f2c1e-7d3a-4e59-9c8b-1a2b3c4d5e6f");
        var account = new ScenarioWatchdogAccount { Identity = new WatchdogAccountIdentity { SamAccountName = "ADMNG-WD$", ObjectGuid = own } };
        await using var factory = new WatchdogFactory(services: s => s.AddSingleton<IWatchdogAccountSource>(account));
        await factory.Store.CommitAsync(new DetectionCommit
        {
            Upserts = [new() { ObjectGuid = own, Dn = "CN=ADMNG-WD,OU=Serwery Tier0,OU=Administracja,DC=contoso,DC=test", Kind = WatchObjectKind.Computer }],
        }, Ct);
        using var client = factory.Admin();
        var saved = await PutAsync(client, TestData.Configuration(), version: 0);

        var excluded = saved.Configuration with { Directory = saved.Configuration.Directory with { ExcludedSubtrees = ["OU=Serwery Tier0,OU=Administracja,{domain}"] } };
        using var response = await client.PutAsync(WatchdogRoutes.Config, ApiJson.Content(new ConfigEnvelope { Version = saved.Version, Configuration = excluded }), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(ConfigValidationCodesTier0Suppression, await ApiJson.IssueCodesAsync(response));
    }

    [Fact]
    public async Task RetentionBelowTheBootstrapFloor_IsRejectedByTheApi()
    {
        await using var factory = new WatchdogFactory();
        using var client = factory.Admin();
        var saved = await PutAsync(client, TestData.Configuration(), version: 0);

        var shorter = saved.Configuration with { Retention = saved.Configuration.Retention with { EventsDays = 365 } };
        using var response = await client.PutAsync(WatchdogRoutes.Config, ApiJson.Content(new ConfigEnvelope { Version = saved.Version, Configuration = shorter }), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("retentionBelowFloor", await ApiJson.IssueCodesAsync(response));
    }

    [Fact]
    public async Task RetentionFloor_CanBeLoweredOnlyByTheBootstrapFile()
    {
        await using var factory = new WatchdogFactory(new Dictionary<string, string?> { ["Watchdog:Retention:MinEventsDays"] = "365" });
        using var client = factory.Admin();
        var saved = await PutAsync(client, TestData.Configuration(), version: 0);

        var shorter = saved.Configuration with { Retention = saved.Configuration.Retention with { EventsDays = 365 } };

        Assert.Equal(saved.Version + 1, (await PutAsync(client, shorter, saved.Version)).Version);
    }

    private const string ConfigValidationCodesTier0Suppression = "tier0Suppression";

    private WatchdogAccountSource Source(TimeProvider clock, string? principal = "ADMNG-WD$@CONTOSO.TEST") =>
        new(new StaticOptionsMonitor<ServiceOptions>(new ServiceOptions
        {
            Kerberos = new KerberosBootstrapOptions { ServicePrincipal = principal, KeytabStatusFile = StatusFile },
        }), clock);

    private Task WriteStatusAsync(int kvno, string result) =>
        File.WriteAllTextAsync(StatusFile,
            $$"""{ "principal": "ADMNG-WD$@CONTOSO.TEST", "kvno": {{kvno}}, "lastChange": "2026-10-20T03:15:00Z", "result": "{{result}}" }""", Ct);

    private static async Task<ConfigEnvelope> PutAsync(HttpClient client, WatchdogConfiguration configuration, long version)
    {
        using var response = await client.PutAsync(WatchdogRoutes.Config, ApiJson.Content(new ConfigEnvelope { Version = version, Configuration = configuration }), Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await ApiJson.ReadAsync<ConfigEnvelope>(response);
    }
}
