using AdMng.Core.ActiveDirectory;
using AdMng.Testing;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Testing.Scenarios;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Watchdog.Core.Tests.Detection;

/// <summary>Domain kinds (500–509): domain head, PSOs, GPOs, trusts, DCs, FSMO roles, PKI and schema.</summary>
public class DomainKindTests
{
    private static readonly Guid Dc02Invocation = new("b0c1d2e3-0000-4000-8000-000000000002");

    [Fact]
    public async Task DomainPolicyChanged_ForLockoutPolicyAndMachineAccountQuota()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        await scenario.ReplaceAsync(scenario.Builder.DomainDn, WatchdogAttributes.LockoutThreshold, 0);
        await scenario.ReplaceAsync(scenario.Builder.DomainDn, WatchdogAttributes.MachineAccountQuota, 0);
        var changed = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(WatchEventKind.DomainPolicyChanged, changed.Kind);
        Assert.Equal(Severity.High, changed.Severity);
        Assert.Equal(2, changed.Changes.Count);
    }

    [Fact]
    public async Task DomainPolicyChanged_ForRecycleBinAndTombstoneLifetime()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        await scenario.ReplaceAsync(scenario.Builder.DirectoryServiceDn, Attr.TombstoneLifetime, 60);
        var events = await scenario.CycleEventsAsync();

        var changed = Assert.Single(events);
        Assert.Equal(WatchEventKind.DomainPolicyChanged, changed.Kind);
        Assert.Equal(Severity.High, changed.Severity);
    }

    [Fact]
    public async Task PasswordSettingsChanged_ForANewAndAChangedPso()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        var pso = scenario.Builder.AddPasswordSettings("PSO-Administratorzy", 1, TimeSpan.FromHours(1), [scenario.Builder.DomainAdminsDn]);
        var created = Assert.Single(await scenario.SettleAsync());
        await scenario.ReplaceAsync(pso, "msDS-LockoutThreshold", 10);
        var changed = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(WatchEventKind.PasswordSettingsChanged, created.Kind);
        Assert.Equal(Severity.High, created.Severity);
        Assert.Equal(WatchEventKind.PasswordSettingsChanged, changed.Kind);
        Assert.Equal(pso, changed.Object.Dn);
    }

    [Fact]
    public async Task GroupPolicyLinkChanged_IsMediumOnAnOuAndHighOnTheDomainControllersOu()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        var policy = scenario.Builder.AddGroupPolicy("Zasady – Księgowość");
        await scenario.SettleAsync();

        var link = scenario.Builder.GpLink(Guid.Parse(policy.Split('{', '}')[1]));
        await scenario.ReplaceAsync(scenario.Company.AccountingOu, Attr.GpLink, link);
        await scenario.ReplaceAsync(scenario.Builder.DomainControllersDn, Attr.GpLink,
            scenario.Builder.GpLink(FakeDirectoryBuilder.DefaultDomainControllersPolicyGuid) + link);
        var events = await scenario.CycleEventsAsync();

        var ou = Assert.Single(events, e => e.Object.Dn == scenario.Company.AccountingOu);
        Assert.Equal(WatchEventKind.GroupPolicyLinkChanged, ou.Kind);
        Assert.Equal(Severity.Medium, ou.Severity);
        Assert.Contains(ou.Changes.SelectMany(c => c.NewValues), v => v.Contains("Zasady – Księgowość", StringComparison.Ordinal));
        Assert.Equal(Severity.High, Assert.Single(events, e => e.Object.Dn == scenario.Builder.DomainControllersDn).Severity);
    }

    [Fact]
    public async Task GroupPolicyChanged_IsMediumForTheDefaultDomainPolicy()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        await scenario.ReplaceAsync(scenario.Builder.DefaultDomainPolicyDn, "versionNumber", 65538);
        var changed = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(WatchEventKind.GroupPolicyChanged, changed.Kind);
        Assert.Equal(Severity.Medium, changed.Severity);
    }

    [Fact]
    public async Task TrustChanged_ForANewTrustIsCritical()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        scenario.Builder.AddTrust("fabrikam.test");
        var created = Assert.Single(await scenario.SettleAsync());

        Assert.Equal(WatchEventKind.TrustChanged, created.Kind);
        Assert.Equal(Severity.Critical, created.Severity);
    }

    [Fact]
    public async Task DomainControllerAdded_AndRemoved()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        var ntds = scenario.Builder.AddDomainController("dc02.contoso.test", Dc02Invocation);
        var added = await scenario.SettleAsync();
        var dcAdded = Assert.Single(added, e => e.Kind == WatchEventKind.DomainControllerAdded);
        Assert.Equal(Severity.Critical, dcAdded.Severity);
        Assert.Contains(scenario.Services.GetRequiredService<DomainControllerDirectory>().All, d => string.Equals(d.HostName, "dc02.contoso.test", StringComparison.OrdinalIgnoreCase));

        await scenario.DeleteAsync(ntds);
        Assert.DoesNotContain(await scenario.CycleEventsAsync(), e => e.Kind == WatchEventKind.DomainControllerRemoved);
        scenario.Advance(DomainControllerDirectory.RefreshInterval);
        scenario.Builder.AddOu("Wyzwalacz");
        var removed = await scenario.CycleEventsAsync();

        var dcRemoved = Assert.Single(removed, e => e.Kind == WatchEventKind.DomainControllerRemoved);
        Assert.Equal(Severity.High, dcRemoved.Severity);
    }

    [Fact]
    public async Task DomainControllerOfAnotherDomain_IsHighAndReadOnlyDcsAreNoAttributionSource()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        scenario.Builder.AddDomainController("emea-dc07.emea.contoso.test", new Guid("b0c1d2e3-0000-4000-8000-000000000003"), domainNc: "DC=emea,DC=contoso,DC=test");
        scenario.Builder.AddDomainController("rodc01.contoso.test", new Guid("b0c1d2e3-0000-4000-8000-000000000004"), isReadOnly: true);
        var events = await scenario.SettleAsync();

        var other = Assert.Single(events, e => e.Kind == WatchEventKind.DomainControllerAdded && e.Tags.Contains(EventTags.OtherDomain));
        Assert.Equal(Severity.High, other.Severity);
        var directory = scenario.Services.GetRequiredService<DomainControllerDirectory>();
        Assert.Contains(directory.All, d => string.Equals(d.HostName, "rodc01.contoso.test", StringComparison.OrdinalIgnoreCase) && d.IsReadOnly);
        Assert.DoesNotContain(directory.AttributionSources, h => string.Equals(h.HostName, "rodc01.contoso.test", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(directory.AttributionSources, h => string.Equals(h.HostName, "emea-dc07.emea.contoso.test", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FsmoRoleChanged_WhenTheRidMasterMoves()
    {
        await using var scenario = new DetectionScenario();
        var dc02 = scenario.Builder.AddDomainController("dc02.contoso.test", Dc02Invocation);
        await scenario.BaselineAsync();

        await scenario.ReplaceAsync(scenario.Builder.RidManagerDn, WatchdogAttributes.FsmoRoleOwner, dc02);
        var moved = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(WatchEventKind.FsmoRoleChanged, moved.Kind);
        Assert.Equal(Severity.High, moved.Severity);
        Assert.Contains(moved.Changes.SelectMany(c => c.NewValues), v => v.Contains("dc02", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CertificateTemplateChanged_ForFlagsAndPublication()
    {
        string? template = null;
        string? ca = null;
        await using var scenario = new DetectionScenario(seed: b =>
        {
            template = b.AddCertificateTemplate("Użytkownik-VPN");
            ca = b.AddEnrollmentService("CONTOSO-CA");
        });
        await scenario.BaselineAsync();

        await scenario.ReplaceAsync(template!, "msPKI-Certificate-Name-Flag", 1);
        await scenario.ReplaceAsync(ca!, "certificateTemplates", "Użytkownik-VPN");
        var events = await scenario.CycleEventsAsync();

        Assert.Equal(2, events.Count(e => e.Kind == WatchEventKind.CertificateTemplateChanged));
        Assert.All(events, e => Assert.Equal(Severity.High, e.Severity));
    }

    [Fact]
    public async Task SchemaChanged_IsOneEventPerCycle()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        scenario.Builder.AddSchemaObject("contosoBadgeNumber");
        scenario.Builder.AddSchemaObject("contosoCostCenter");
        scenario.Builder.AddSchemaObject("contosoEmployee", isClass: true);
        var events = await scenario.SettleAsync();

        var schema = Assert.Single(events);
        Assert.Equal(WatchEventKind.SchemaChanged, schema.Kind);
        Assert.Equal(Severity.High, schema.Severity);
        Assert.Contains(schema.Changes.SelectMany(c => c.NewValues), v => v.Contains("contosoCostCenter", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SchemaWritesDuringACycle_AreReportedOnceInTheNextCycle()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        scenario.Builder.AddSchemaObject("contosoNrIdentyfikatora");
        var injected = false;
        // Exchange /PrepareSchema keeps writing while the cycle runs: an object written after H was read.
        scenario.Directory.BeforeOperation = operation =>
        {
            if (!injected && operation.Kind == FakeOperationKind.Search && operation.Search?.Filter?.Contains(Attr.UsnChanged, StringComparison.OrdinalIgnoreCase) == true)
            {
                injected = true;
                scenario.Builder.AddSchemaObject("contosoMiejscePracy");
            }
        };

        var first = await scenario.CycleEventsAsync();
        scenario.Directory.BeforeOperation = null;
        var second = await scenario.CycleEventsAsync();

        Assert.Equal(["contosoNrIdentyfikatora"], SchemaNames(first));
        Assert.Equal(["contosoMiejscePracy"], SchemaNames(second));
    }

    [Fact]
    public async Task SchemaChanged_IsNotDuplicatedWhenTheCycleRepeats()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        scenario.Builder.AddSchemaObject("contosoKodOddziału");
        scenario.Store.FailNextFinalCommit();

        await Assert.ThrowsAsync<IOException>(() => scenario.RunCycleAsync());
        var crashed = scenario.TakeEvents();
        var repeated = await scenario.CycleEventsAsync();

        Assert.Equal(["contosoKodOddziału"], SchemaNames([.. crashed, .. repeated]));
    }

    private static List<string> SchemaNames(IEnumerable<WatchEvent> events) =>
        [.. events.Where(e => e.Kind == WatchEventKind.SchemaChanged).SelectMany(e => e.Changes.Single(c => c.Attribute == WatchdogAttributes.LdapDisplayName).NewValues)];
}
