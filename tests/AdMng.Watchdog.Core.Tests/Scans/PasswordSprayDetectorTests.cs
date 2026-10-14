using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Testing.Scenarios;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Watchdog.Core.Tests.Scans;

/// <summary>PasswordSprayingSuspected (section 10.15) over 4771/4776 fixtures.</summary>
public class PasswordSprayDetectorTests
{
    private readonly ScenarioSecurityEventSource _securityLog = new();

    [Fact]
    public async Task TwentyAccountsFromOneAddress_IsSuspected()
    {
        await using var scenario = await StartAsync(enabled: true);
        var pdc = scenario.Directory.Info.Server;
        foreach (var i in Enumerable.Range(1, 20))
        {
            _securityLog.Add(pdc, 4771, scenario.Time.GetUtcNow().AddMinutes(-2), FailedLogonXml.Kerberos($"uzytkownik{i:D2}", "10.20.30.40", scenario.Time.GetUtcNow().AddMinutes(-2), recordId: i));
        }

        _securityLog.Add(pdc, 4771, scenario.Time.GetUtcNow().AddMinutes(-1), FailedLogonXml.Kerberos("jan.kowalski", "10.20.30.40", scenario.Time.GetUtcNow().AddMinutes(-1), recordId: 21));
        var spray = Assert.Single(await scenario.ScanEventsAsync());
        scenario.Advance(TimeSpan.FromMinutes(1));
        var cooling = await scenario.ScanEventsAsync();

        Assert.Equal(WatchEventKind.PasswordSprayingSuspected, spray.Kind);
        Assert.Equal(Severity.High, spray.Severity);
        Assert.Equal(21, spray.Burst!.Count);
        Assert.Equal("10.20.30.40", spray.Attribution.IpAddress);
        Assert.Contains(spray.Burst.Objects, o => o.Dn == scenario.Company.JanKowalski);
        Assert.Contains(spray.Burst.Objects, o => o.SamAccountName == "uzytkownik07" && o.Dn.Length == 0);
        Assert.Equal(pdc, _securityLog.Queries[0].Host);
        Assert.Equal([4771, 4776], _securityLog.Queries[0].Query.EventCodes);
        Assert.Empty(cooling);
    }

    [Fact]
    public async Task NtlmFailuresFromOneWorkstation_AreSuspected()
    {
        await using var scenario = await StartAsync(enabled: true);
        var now = scenario.Time.GetUtcNow();
        foreach (var i in Enumerable.Range(1, 20))
        {
            _securityLog.Add(scenario.Directory.Info.Server, 4776, now.AddMinutes(-3), FailedLogonXml.Ntlm($"konto{i:D2}", "KIOSK-01", now.AddMinutes(-3), recordId: i));
        }

        var spray = Assert.Single(await scenario.ScanEventsAsync());

        Assert.Equal("KIOSK-01", spray.Attribution.Workstation);
    }

    [Fact]
    public async Task FailuresFromManyAddresses_AreNoSpray()
    {
        await using var scenario = await StartAsync(enabled: true);
        var now = scenario.Time.GetUtcNow();
        foreach (var i in Enumerable.Range(1, 20))
        {
            _securityLog.Add(scenario.Directory.Info.Server, 4771, now.AddMinutes(-1), FailedLogonXml.Kerberos($"uzytkownik{i:D2}", $"10.0.0.{i}", now.AddMinutes(-1), recordId: i));
        }

        Assert.Empty(await scenario.ScanEventsAsync());
    }

    [Fact]
    public async Task OtherStatusCodesAndOldFailures_AreIgnored()
    {
        await using var scenario = await StartAsync(enabled: true);
        var now = scenario.Time.GetUtcNow();
        foreach (var i in Enumerable.Range(1, 20))
        {
            _securityLog.Add(scenario.Directory.Info.Server, 4771, now.AddMinutes(-1), FailedLogonXml.Kerberos($"wygasle{i:D2}", "10.9.9.9", now.AddMinutes(-1), status: "0x12", recordId: i));
            _securityLog.Add(scenario.Directory.Info.Server, 4771, now.AddMinutes(-30), FailedLogonXml.Kerberos($"stare{i:D2}", "10.8.8.8", now.AddMinutes(-30), recordId: 100 + i));
        }

        Assert.Empty(await scenario.ScanEventsAsync());
    }

    [Fact]
    public async Task DisabledByDefault_TheLogIsNotQueried()
    {
        await using var scenario = await StartAsync(enabled: false);

        await scenario.ScanEventsAsync();

        Assert.Empty(_securityLog.Queries);
    }

    [Fact]
    public async Task UnreadableLog_IsSkipped()
    {
        await using var scenario = await StartAsync(enabled: true);
        _securityLog.FailWith(5);

        var result = await scenario.Scans.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Failed);
        Assert.Empty(scenario.TakeEvents());
    }

    private async Task<DetectionScenario> StartAsync(bool enabled)
    {
        var scenario = new DetectionScenario(
            configuration: new WatchdogConfiguration { Scans = new ScanSettings { PasswordSpray = new PasswordSprayDetection { Enabled = enabled } } },
            services: s => s.AddSingleton<ISecurityEventSource>(_securityLog));
        await scenario.BaselineAsync();
        await scenario.Scans.RunOnceAsync(TestContext.Current.CancellationToken);
        scenario.TakeEvents();
        return scenario;
    }
}
