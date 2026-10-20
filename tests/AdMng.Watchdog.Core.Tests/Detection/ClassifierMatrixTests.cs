using AdMng.Core.ActiveDirectory;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Testing.Scenarios;

namespace AdMng.Watchdog.Core.Tests.Detection;

/// <summary>Further rows of the classifier table (10.6), the KeyCredentialChanged matrix (10.6.4) and the forest singletons (10.11).</summary>
public class ClassifierMatrixTests
{
    private static readonly string KeyHex = string.Concat(Enumerable.Repeat("0A1B2C3D", 8));
    private static readonly string OtherKeyHex = string.Concat(Enumerable.Repeat("99887766", 8));

    [Fact]
    public async Task KeyCredential_OnAComputerThatAlreadyHadOne_IsHigh()
    {
        await using var scenario = new DetectionScenario();
        scenario.Directory.SeedAttributes(scenario.Company.Computer1, new Dictionary<string, object?>
        {
            [WatchdogAttributes.KeyCredentialLink] = DnBinary(KeyHex, scenario.Company.Computer1),
        });
        await scenario.BaselineAsync();

        await scenario.AddValuesAsync(scenario.Company.Computer1, WatchdogAttributes.KeyCredentialLink, DnBinary(OtherKeyHex, scenario.Company.Computer1));
        var changed = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(WatchEventKind.KeyCredentialChanged, changed.Kind);
        Assert.Equal(Severity.High, changed.Severity);
    }

    [Fact]
    public async Task KeyCredential_OnADomainControllerOrAPrivilegedUser_IsHigh()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        await scenario.ReplaceAsync(scenario.Builder.DomainControllerDn, WatchdogAttributes.KeyCredentialLink, DnBinary(KeyHex, scenario.Builder.DomainControllerDn));
        await scenario.ReplaceAsync(scenario.Builder.AdministratorDn, WatchdogAttributes.KeyCredentialLink, DnBinary(KeyHex, scenario.Builder.AdministratorDn));
        var events = await scenario.CycleEventsAsync();

        Assert.Equal(2, events.Count(e => e.Kind == WatchEventKind.KeyCredentialChanged));
        Assert.All(events.Where(e => e.Kind == WatchEventKind.KeyCredentialChanged), e => Assert.Equal(Severity.High, e.Severity));
    }

    [Fact]
    public async Task SupersededLinkOnAUser_IsManagedAccountLinkChanged()
    {
        string? dmsa = null;
        await using var scenario = new DetectionScenario(seed: b => dmsa = b.AddServiceAccount("dmsa-crm01", ObjectClasses.DelegatedManagedServiceAccount));
        await scenario.BaselineAsync();

        await scenario.ReplaceAsync(scenario.Company.PawelLewandowski, WatchdogAttributes.SupersededManagedAccountLink, dmsa!);
        var linked = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(WatchEventKind.ManagedAccountLinkChanged, linked.Kind);
        Assert.Equal(Severity.Critical, linked.Severity);              // stage B design 9.1: Critical always
        Assert.Contains(EventTags.BadSuccessor, linked.Tags);
        Assert.DoesNotContain(EventTags.Privileged, linked.Tags);
    }

    [Fact]
    public async Task RecycleBinAndDsHeuristics_AreDomainPolicyChanges()
    {
        await using var scenario = new DetectionScenario();
        scenario.Directory.SeedAttributes(scenario.Builder.PartitionsDn, new Dictionary<string, object?> { [WatchdogAttributes.EnabledFeature] = null });
        await scenario.BaselineAsync();

        scenario.Directory.SetValues(scenario.Builder.PartitionsDn, WatchdogAttributes.EnabledFeature, scenario.Builder.RecycleBinFeatureDn);
        await scenario.ReplaceAsync(scenario.Builder.DirectoryServiceDn, WatchdogAttributes.DsHeuristics, "0000002");
        var events = await scenario.CycleEventsAsync();

        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(WatchEventKind.DomainPolicyChanged, e.Kind));
        Assert.All(events, e => Assert.Equal(WatchObjectKind.Configuration, e.Object.Kind));
    }

    [Fact]
    public async Task PdcRoleMove_OnTheDomainHead_IsFsmoRoleChanged()
    {
        string? dc02 = null;
        await using var scenario = new DetectionScenario(seed: b => dc02 = b.AddDomainController("dc02.contoso.test", new Guid("b0c1d2e3-0000-4000-8000-000000000002")));
        await scenario.BaselineAsync();

        await scenario.ReplaceAsync(scenario.Builder.DomainDn, WatchdogAttributes.FsmoRoleOwner, dc02!);
        await scenario.ReplaceAsync(scenario.Builder.SchemaDn, WatchdogAttributes.FsmoRoleOwner, dc02!);
        var events = await scenario.CycleEventsAsync();

        Assert.Equal(2, events.Count(e => e.Kind == WatchEventKind.FsmoRoleChanged));
    }

    [Fact]
    public async Task PsoAppliesTo_IsPasswordSettingsChanged()
    {
        string? pso = null;
        await using var scenario = new DetectionScenario(seed: b => pso = b.AddPasswordSettings("PSO-Serwis", 5, TimeSpan.FromHours(1)));
        await scenario.BaselineAsync();

        await scenario.AddValuesAsync(pso!, WatchdogAttributes.PsoAppliesTo, scenario.Company.GroupIt);
        var changed = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(WatchEventKind.PasswordSettingsChanged, changed.Kind);
        Assert.Equal(Severity.High, changed.Severity);
    }

    [Fact]
    public async Task NewGroupPolicy_IsGroupPolicyChangedAfterTheSettleWindow()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        var policy = scenario.Builder.AddGroupPolicy("Zasady – Kioski");
        var created = Assert.Single(await scenario.SettleAsync());

        Assert.Equal(WatchEventKind.GroupPolicyChanged, created.Kind);
        Assert.Equal(policy, created.Object.Dn);
        Assert.Equal(Severity.Info, created.Severity);
    }

    [Fact]
    public async Task AdminCountCleared_IsAnAttributeChange()
    {
        await using var scenario = new DetectionScenario();
        scenario.Directory.SeedAttributes(scenario.Company.GrazynaDabrowska, new Dictionary<string, object?> { [Attr.AdminCount] = 1 });
        await scenario.BaselineAsync();

        await scenario.ClearAsync(scenario.Company.GrazynaDabrowska, Attr.AdminCount);

        Assert.Equal(WatchEventKind.AttributesChanged, Assert.Single(await scenario.CycleEventsAsync()).Kind);
    }

    private static string DnBinary(string hex, string dn) => $"B:{hex.Length}:{hex}:{dn}";
}
