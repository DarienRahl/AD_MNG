using AdMng.Core.ActiveDirectory;
using AdMng.Testing;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Rules;
using AdMng.Watchdog.Testing.Scenarios;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Watchdog.Core.Tests.Detection;

/// <summary>
/// Stage B detections (docs/dev/watchdog-linux-design.md 9.1–9.4) end to end through the fake directory: BadSuccessor,
/// ms-DS-MachineAccountQuota, the service's own account and the audit settings (SACL) of the key objects – each event
/// also matched against the shipped rules.
/// </summary>
public class StageBDetectionTests
{
    private static readonly WatchdogConfiguration Defaults = new() { Rules = DefaultRules.Create() };

    // ------------------------------------------------------------------ BadSuccessor (9.1)

    [Fact]
    public async Task DmsaCreatedInAnOu_IsCriticalWithTagBadSuccessor_AndMatchesTier0BadSuccessor()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        var dmsa = scenario.Builder.AddServiceAccount("dmsa-księgowość", ObjectClasses.DelegatedManagedServiceAccount, parentDn: scenario.Company.AccountingOu);
        var created = Assert.Single(await scenario.SettleAsync(), e => e.Kind == WatchEventKind.ServiceAccountCreated);

        Assert.Equal(dmsa, created.Object.Dn);
        Assert.Equal(Severity.Critical, created.Severity);
        Assert.Contains(EventTags.BadSuccessor, created.Tags);
        Assert.Contains(DefaultRules.Tier0BadSuccessor, MatchingRules(created));
    }

    [Fact]
    public async Task GmsaCreated_IsNotBadSuccessor()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        scenario.Builder.AddServiceAccount("gmsa-łódź01");
        var created = Assert.Single(await scenario.SettleAsync(), e => e.Kind == WatchEventKind.ServiceAccountCreated);

        Assert.DoesNotContain(EventTags.BadSuccessor, created.Tags);
        Assert.DoesNotContain(DefaultRules.Tier0BadSuccessor, MatchingRules(created));
    }

    [Fact]
    public async Task DelegatedMsaStateChanged_IsCriticalBadSuccessor()
    {
        string? dmsa = null;
        await using var scenario = new DetectionScenario(seed: b => dmsa = b.AddServiceAccount(
            "dmsa-crm02", ObjectClasses.DelegatedManagedServiceAccount, new Dictionary<string, object?> { [WatchdogAttributes.DelegatedMsaState] = 3 }));
        await scenario.BaselineAsync();

        await scenario.ReplaceAsync(dmsa!, WatchdogAttributes.DelegatedMsaState, 1);
        var changed = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(WatchEventKind.ManagedAccountLinkChanged, changed.Kind);
        Assert.Equal(Severity.Critical, changed.Severity);
        Assert.Equal([EventTags.BadSuccessor, EventTags.ServiceAccount], changed.Tags.Order(StringComparer.Ordinal).ToArray());
        Assert.Contains(DefaultRules.Tier0BadSuccessor, MatchingRules(changed));
    }

    [Fact]
    public async Task PrecededByLinkToAPrivilegedAccount_AddsTagPrivileged()
    {
        string? dmsa = null;
        await using var scenario = new DetectionScenario(seed: b => dmsa = b.AddServiceAccount("dmsa-erp02", ObjectClasses.DelegatedManagedServiceAccount));
        await scenario.BaselineAsync();

        await scenario.ReplaceAsync(dmsa!, WatchdogAttributes.ManagedAccountPrecededByLink, scenario.Builder.AdministratorDn);
        var linked = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(Severity.Critical, linked.Severity);
        Assert.Contains(EventTags.BadSuccessor, linked.Tags);
        Assert.Contains(EventTags.Privileged, linked.Tags);
        Assert.Contains(DefaultRules.Tier0BadSuccessor, MatchingRules(linked));
    }

    // ------------------------------------------------------------------ ms-DS-MachineAccountQuota (9.2)

    [Theory]
    [InlineData(0, 10, Severity.Critical)]
    [InlineData(10, 0, Severity.High)]
    [InlineData(10, 5, Severity.High)]
    [InlineData(5, 0, Severity.High)]
    public async Task MachineAccountQuotaChanged_IsTaggedAndCriticalOnlyWhenRaisedFromZero(int before, int after, Severity expected)
    {
        await using var scenario = new DetectionScenario(seed: b => b.Session.SeedAttributes(b.DomainDn,
            new Dictionary<string, object?> { [WatchdogAttributes.MachineAccountQuota] = before }));
        await scenario.BaselineAsync();

        await scenario.ReplaceAsync(scenario.Builder.DomainDn, WatchdogAttributes.MachineAccountQuota, after);
        var changed = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(WatchEventKind.DomainPolicyChanged, changed.Kind);
        Assert.Equal(expected, changed.Severity);
        Assert.Contains(EventTags.MachineAccountQuota, changed.Tags);
        Assert.Contains(DefaultRules.Tier0MachineQuota, MatchingRules(changed));
    }

    [Fact]
    public async Task OtherDomainPolicyChange_IsNotTier0MachineQuota()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        await scenario.ReplaceAsync(scenario.Builder.DomainDn, WatchdogAttributes.LockoutThreshold, 3);
        var changed = Assert.Single(await scenario.CycleEventsAsync());

        Assert.DoesNotContain(EventTags.MachineAccountQuota, changed.Tags);
        Assert.DoesNotContain(DefaultRules.Tier0MachineQuota, MatchingRules(changed));
    }

    [Fact]
    public async Task MachineAccountQuotaHealth_WarnsAboveZero_AndIsReadOnlyInCyclesWithChanges()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        var warning = scenario.Health.Get(HealthCheckCodes.DomainMachineAccountQuota)!;
        Assert.Equal(HealthState.Warning, warning.State);
        Assert.Equal(["10"], warning.Args);

        await scenario.ReplaceAsync(scenario.Builder.DomainDn, WatchdogAttributes.MachineAccountQuota, 0);
        await scenario.CycleEventsAsync();
        var ok = scenario.Health.Get(HealthCheckCodes.DomainMachineAccountQuota)!;
        Assert.Equal(HealthState.Ok, ok.State);
        Assert.Equal(["0"], ok.Args);
    }

    // ------------------------------------------------------------------ audit settings of the key objects (9.4)

    [Fact]
    public async Task SaclChangeOfEveryKeyObject_IsOneCriticalAuditSettingsEvent()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        var builder = scenario.Builder;
        string[] keys =
        [
            builder.DomainDn, builder.AdminSdHolderDn, $"OU=Domain Controllers,{builder.DomainDn}", builder.PublicKeyServicesDn,
            $"CN=Sites,{builder.ConfigurationDn}",
        ];

        foreach (var dn in keys)
        {
            scenario.Directory.SimulateSaclChange(dn);
        }

        var events = await scenario.CycleEventsAsync();

        Assert.Equal(keys.Length, events.Count);
        Assert.All(events, e =>
        {
            Assert.Equal(WatchEventKind.SecurityDescriptorChanged, e.Kind);
            Assert.Equal(Severity.Critical, e.Severity);
            Assert.Contains(EventTags.AuditSettings, e.Tags);
            var row = Assert.Single(e.Changes);
            Assert.Equal("nTSecurityDescriptor", row.Attribute);
            Assert.Empty(row.NewValues);
            Assert.NotNull(e.OriginatingDc);
            Assert.NotNull(e.OriginatingTime);
            Assert.NotNull(e.Object.ObjectGuid);
            Assert.Equal(AttributionStatus.Pending, e.Attribution.Status);
            Assert.Contains(DefaultRules.Tier0Security, MatchingRules(e));
        });
        Assert.Equal(keys.Order(StringComparer.OrdinalIgnoreCase), events.Select(e => e.Object.Dn).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(WatchObjectKind.PublicKeyInfrastructure, Assert.Single(events, e => e.Object.Dn == builder.PublicKeyServicesDn).Object.Kind);

        Assert.Empty(await scenario.CycleEventsAsync());                      // committed with W: never reported again
    }

    [Fact]
    public async Task DaclChangeOfAKeyObject_IsReportedOnceWithoutTagAuditSettings()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        var builder = scenario.Builder;
        var widened = FakeSecurityDescriptor.Build(
            $"{builder.DomainSid}-{WellKnownRids.DomainAdmins}",
            $"{builder.DomainSid}-{WellKnownRids.DomainAdmins}",
            FakeAce.FullControl($"{builder.DomainSid}-{WellKnownRids.DomainAdmins}"),
            FakeAce.FullControl("S-1-5-32-544"),
            FakeAce.FullControl(scenario.Snapshot(scenario.Company.JanKowalski)!.Sid!));

        await scenario.ReplaceAsync(builder.AdminSdHolderDn, Attr.NtSecurityDescriptor, widened);
        var changed = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(WatchEventKind.SecurityDescriptorChanged, changed.Kind);
        Assert.DoesNotContain(EventTags.AuditSettings, changed.Tags);
    }

    [Fact]
    public async Task SaclChange_IsNotReportedAtBaseline()
    {
        await using var scenario = new DetectionScenario();
        scenario.Directory.SimulateSaclChange(scenario.Builder.DomainDn);

        await scenario.BaselineAsync();

        Assert.Empty(scenario.TakeEvents());
    }

    [Fact]
    public async Task SaclChangeOfAnotherObject_IsNoEvent()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        scenario.Directory.SimulateSaclChange(scenario.Company.ItOu);

        Assert.Empty(await scenario.CycleEventsAsync());
    }

    // ------------------------------------------------------------------ the service's own account (9.3)

    [Fact]
    public async Task OwnAccount_EncryptionTypesSelfWrite_IsReportedDespiteTheComputerNoiseFilter()
    {
        var (scenario, account, own) = await OwnAccountScenarioAsync();
        await using var _ = scenario;

        await scenario.ReplaceAsync(own, Attr.SupportedEncryptionTypes, 0x1C);          // RC4 back on
        await scenario.ReplaceAsync(scenario.Company.Computer1, Attr.SupportedEncryptionTypes, 0x1C);
        var changed = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(own, changed.Object.Dn);
        Assert.Equal(WatchEventKind.AttributesChanged, changed.Kind);
        Assert.Contains(EventTags.WatchdogAccount, changed.Tags);
        Assert.Contains(DefaultRules.WatchdogSelf, MatchingRules(changed));
        Assert.Empty(account.Requests);
    }

    [Fact]
    public async Task OwnAccount_OwnHostSpnAndDnsHostName_AreReported()
    {
        var (scenario, _, own) = await OwnAccountScenarioAsync();
        await using var __ = scenario;

        await scenario.AddValuesAsync(own, Attr.ServicePrincipalName, "HTTP/admng-wd.contoso.test");
        var spn = Assert.Single(await scenario.CycleEventsAsync());
        await scenario.ReplaceAsync(own, Attr.DnsHostName, "admng-wd-nowy.contoso.test");
        var events = await scenario.CycleEventsAsync();

        Assert.Contains(EventTags.WatchdogAccount, spn.Tags);
        Assert.Equal("servicePrincipalName", Assert.Single(spn.Changes).Attribute);
        Assert.All(events, e => Assert.Contains(EventTags.WatchdogAccount, e.Tags));
        Assert.Contains(events, e => e.Changes.Any(c => c.Attribute == Attr.DnsHostName));
    }

    [Fact]
    public async Task OwnAccount_PlannedRotation_IsRecordedWithoutTheTag()
    {
        var (scenario, account, own) = await OwnAccountScenarioAsync();
        await using var _ = scenario;
        account.Rotation = new KeytabRotationRecord(2, scenario.Time.GetUtcNow().AddMinutes(-3), KeytabStatusResults.Ok);

        await scenario.ResetPasswordAsync(own);
        var changed = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(WatchEventKind.PasswordChanged, changed.Kind);
        Assert.DoesNotContain(EventTags.WatchdogAccount, changed.Tags);
        Assert.DoesNotContain(DefaultRules.WatchdogSelf, MatchingRules(changed));
        Assert.Equal([2], account.Requests);
    }

    [Theory]
    [InlineData(3, 0, KeytabStatusResults.Ok)]                    // the tool recorded another key version
    [InlineData(2, 11, KeytabStatusResults.Ok)]                   // pwdLastSet 11 minutes away from the recorded change
    [InlineData(2, 0, KeytabStatusResults.ChangeFailed)]          // the tool did not change it
    [InlineData(2, 0, KeytabStatusResults.ReplicationPending)]    // a planned rotation as well (contrast: no tag)
    public async Task OwnAccount_PasswordChangeOutsideThePlannedRotation_IsCritical(int kvno, int minutesAway, string result)
    {
        var (scenario, account, own) = await OwnAccountScenarioAsync();
        await using var _ = scenario;
        account.Rotation = new KeytabRotationRecord(kvno, scenario.Time.GetUtcNow().AddMinutes(-minutesAway), result);

        await scenario.ResetPasswordAsync(own);
        var changed = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(WatchEventKind.PasswordChanged, changed.Kind);
        var planned = result == KeytabStatusResults.ReplicationPending;
        Assert.Equal(!planned, changed.Tags.Contains(EventTags.WatchdogAccount));
        Assert.Equal(!planned, MatchingRules(changed).Contains(DefaultRules.WatchdogSelf));
    }

    [Fact]
    public async Task OwnAccount_PasswordChangeWithoutStatusFile_IsCritical()
    {
        var (scenario, _, own) = await OwnAccountScenarioAsync();
        await using var __ = scenario;

        await scenario.ResetPasswordAsync(own);
        var changed = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Contains(EventTags.WatchdogAccount, changed.Tags);
        var match = Assert.Single(RuleEngineMatches(changed), m => m.Rule.Id == DefaultRules.WatchdogSelf);
        Assert.Equal(Severity.Critical, match.Rule.Severity);
    }

    [Fact]
    public async Task OwnAccount_DaclChange_IsTrackedLikeACriticalObject()
    {
        var (scenario, _, own) = await OwnAccountScenarioAsync();
        await using var __ = scenario;
        var builder = scenario.Builder;
        var withoutDeny = FakeSecurityDescriptor.Build($"{builder.DomainSid}-{WellKnownRids.DomainAdmins}", null,
            FakeAce.FullControl($"{builder.DomainSid}-{WellKnownRids.DomainAdmins}"));

        await scenario.ReplaceAsync(own, Attr.NtSecurityDescriptor, withoutDeny);
        var changed = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(WatchEventKind.SecurityDescriptorChanged, changed.Kind);
        Assert.Contains(EventTags.WatchdogAccount, changed.Tags);
        Assert.Contains(DefaultRules.WatchdogSelf, MatchingRules(changed));
    }

    [Fact]
    public async Task OwnAccount_AddedToAGroup_TagsTheMembershipEvent()
    {
        var (scenario, _, own) = await OwnAccountScenarioAsync();
        await using var __ = scenario;

        await scenario.AddValuesAsync(scenario.Company.GroupIt, Attr.Member, own);
        await scenario.AddValuesAsync(scenario.Company.GroupIt, Attr.Member, scenario.Company.Computer2);
        var events = await scenario.CycleEventsAsync();

        var ownAdded = Assert.Single(events, e => e.Member?.Dn == own);
        Assert.Contains(EventTags.WatchdogAccount, ownAdded.Tags);
        Assert.DoesNotContain(EventTags.WatchdogAccount, Assert.Single(events, e => e.Member?.Dn == scenario.Company.Computer2).Tags);
    }

    [Fact]
    public async Task OwnAccount_MadePrivileged_TagsThePrivilegeEvent()
    {
        var (scenario, _, own) = await OwnAccountScenarioAsync();
        await using var __ = scenario;

        await scenario.AddValuesAsync(scenario.Builder.DomainAdminsDn, Attr.Member, own);
        var granted = Assert.Single(await scenario.CycleEventsAsync(), e => e.Kind == WatchEventKind.PrivilegeGranted);

        Assert.Equal(own, granted.Object.Dn);
        Assert.Contains(EventTags.WatchdogAccount, granted.Tags);
        Assert.Contains(DefaultRules.WatchdogSelf, MatchingRules(granted));
    }

    [Fact]
    public async Task OwnAccount_IsMatchedByGuidAfterARename()
    {
        var (scenario, account, own) = await OwnAccountScenarioAsync();
        await using var _ = scenario;
        var snapshot = scenario.Snapshot(own)!;
        account.Identity = account.Identity! with { ObjectGuid = snapshot.ObjectGuid };

        await scenario.ReplaceAsync(own, Attr.SamAccountName, "INNE-KONTO$");
        await scenario.CycleEventsAsync();
        await scenario.ReplaceAsync(own, Attr.Description, "Zmieniony opis – Łódź");
        var changed = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Contains(EventTags.WatchdogAccount, changed.Tags);
    }

    [Fact]
    public async Task WithoutAnOwnAccount_NothingIsTagged()
    {
        await using var scenario = new DetectionScenario(seed: b => b.AddComputer("ADMNG-WD", b.AddOu("Tier0")));
        await scenario.BaselineAsync();

        await scenario.ReplaceAsync($"CN=ADMNG-WD,OU=Tier0,{scenario.Builder.DomainDn}", Attr.SupportedEncryptionTypes, 0x1C);

        Assert.Empty(await scenario.CycleEventsAsync());                      // computer noise, as before
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<(DetectionScenario Scenario, ScenarioWatchdogAccount Account, string Own)> OwnAccountScenarioAsync()
    {
        var account = new ScenarioWatchdogAccount();
        string? own = null;
        var scenario = new DetectionScenario(
            seed: b =>
            {
                var tier0 = b.AddOu("Tier0");
                own = b.AddComputer("ADMNG-WD", tier0, new Dictionary<string, object?>
                {
                    [Attr.DnsHostName] = "admng-wd.contoso.test",
                    [Attr.OperatingSystem] = "Debian GNU/Linux 13",
                    [Attr.SupportedEncryptionTypes] = 0x18,
                    [Attr.NtSecurityDescriptor] = FakeSecurityDescriptor.Build($"{b.DomainSid}-{WellKnownRids.DomainAdmins}", null,
                        FakeAce.FullControl($"{b.DomainSid}-{WellKnownRids.DomainAdmins}"), FakeAce.FullControl("S-1-5-32-544")),
                });
            },
            services: s => s.AddSingleton<IWatchdogAccountSource>(account));
        await scenario.BaselineAsync();
        return (scenario, account, own!);
    }

    private static IReadOnlyList<string> MatchingRules(WatchEvent e) => [.. RuleEngineMatches(e).Select(m => m.Rule.Id)];

    private static IReadOnlyList<(RuleSettings Rule, RuleMatchOutcome Outcome)> RuleEngineMatches(WatchEvent e)
    {
        var context = RuleMatchContext.Create(Defaults, ConfigTokens.Unknown);
        return [.. Defaults.Rules
            .Select(r => (Rule: r, Outcome: RuleMatcher.Match(r, e, context, DetectionScenario.Start, attributionGraceExpired: true)))
            .Where(m => m.Outcome != RuleMatchOutcome.NoMatch)];
    }
}
