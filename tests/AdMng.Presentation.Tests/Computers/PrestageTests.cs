using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Computers;
using AdMng.Core.Model;
using AdMng.Core.Security;
using AdMng.Presentation.Dialogs;

namespace AdMng.Presentation.Tests.Computers;

/// <summary>Prestaging a computer account in "Nowy → Komputer" (K1.3): join right, managedBy, location, the KB5020276 summary.</summary>
public class PrestageTests
{
    private static async Task<(ComputerScenario Scenario, NewObjectViewModel Dialog, string Helpdesk)> OpenAsync(string culture = "en-US")
    {
        var scenario = await ComputerScenario.CreateAsync(culture);
        var harness = scenario.Harness;
        var helpdesk = harness.Builder.AddGroup("GG_Helpdesk_Dołączanie", scenario.Company.GroupsOu);
        var dialog = harness.Factory.CreateNewObject(ObjectKind.Computer, scenario.Company.ComputersOu);
        await dialog.InitializeAsync();
        return (scenario, dialog, helpdesk);
    }

    private static IReadOnlyList<AceSpec> ExplicitAces(ComputerScenario scenario, string dn)
    {
        var descriptor = (byte[])scenario.Harness.Session.Snapshot(dn)!.Attributes[Attr.NtSecurityDescriptor][0];
        Assert.True(DaclEditor.TryReadExplicit(descriptor, out var aces));
        return aces;
    }

    [Fact]
    public async Task Prestage_WithJoiner_CreatesTheAccountAndGrantsTheFourAces()
    {
        var (scenario, dialog, helpdesk) = await OpenAsync();
        using var _dialog = dialog;
        var harness = scenario.Harness;
        var helpdeskSid = harness.Session.GetValues(helpdesk, Attr.ObjectSid)[0];
        dialog.Name = "WAW-PC201";
        dialog.Description = "Laptop dla Żanety – księgowość";
        dialog.Location = "Warszawa, pokój 210";
        harness.Dialogs.PickedObjects.Enqueue([helpdesk]);
        await dialog.ChangeJoinerCommand.ExecuteAsync(null);
        harness.Dialogs.PickedObjects.Enqueue([scenario.Company.JanKowalski]);
        await dialog.ChangeManagedByCommand.ExecuteAsync(null);

        Assert.Equal(@"CONTOSO\GG_Helpdesk_Dołączanie", dialog.JoinerText);
        Assert.Equal("Jan Kowalski", dialog.ManagedByText);
        var plan = dialog.BuildPlan(harness.Session, null);
        Assert.Equal([ChangeAction.Create, ChangeAction.ModifySecurity], plan.Objects[0].Changes.Select(c => c.Action));
        await dialog.CreateCommand.ExecuteAsync(null);

        Assert.Null(dialog.Error);
        var dn = dialog.CreatedDn!;
        Assert.Equal("4096", harness.Value(dn, Attr.UserAccountControl));
        Assert.Equal(scenario.Company.JanKowalski, harness.Value(dn, Attr.ManagedBy));
        Assert.Equal("Warszawa, pokój 210", harness.Value(dn, Attr.Location));
        Assert.Equal("Laptop dla Żanety – księgowość", harness.Value(dn, Attr.Description));
        var granted = ExplicitAces(scenario, dn).Where(a => a.TrusteeSid == helpdeskSid).ToList();
        Assert.Equal(4, granted.Count);
        Assert.All(ComputerJoinRights.For(helpdeskSid), wanted => Assert.Contains(granted, a => a.SameAce(wanted)));
        Assert.All(granted, a => Assert.Equal(AceQualifier.Allow, a.Qualifier));

        // One audited plan: the create and the permission step.
        Assert.Contains(harness.AuditLog.Entries, e => e.DistinguishedName == dn && e.Attribute == Attr.NtSecurityDescriptor);
    }

    [Fact]
    public async Task Prestage_WithoutJoiner_WritesNoPermission()
    {
        var (scenario, dialog, _) = await OpenAsync();
        using var _dialog = dialog;
        dialog.Name = "WAW-PC202";

        var plan = dialog.BuildPlan(scenario.Harness.Session, null);
        Assert.Equal([ChangeAction.Create], plan.Objects[0].Changes.Select(c => c.Action));
        Assert.DoesNotContain(plan.Objects[0].Changes[0].Attributes, a => a.Attribute is Attr.ManagedBy or Attr.Location);
        await dialog.CreateCommand.ExecuteAsync(null);

        Assert.Null(dialog.Error);
        Assert.DoesNotContain(scenario.Harness.AuditLog.Entries, e => e.Attribute == Attr.NtSecurityDescriptor);
        Assert.EndsWith("Without a user or group only administrators can join the computer with this account.", dialog.JoinSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearJoiner_RemovesTheSecurityStep()
    {
        var (scenario, dialog, helpdesk) = await OpenAsync();
        using var _dialog = dialog;
        dialog.Name = "WAW-PC203";
        scenario.Harness.Dialogs.PickedObjects.Enqueue([helpdesk]);
        await dialog.ChangeJoinerCommand.ExecuteAsync(null);
        Assert.True(dialog.HasJoiner);

        dialog.ClearJoinerCommand.Execute(null);

        Assert.False(dialog.HasJoiner);
        Assert.Equal("none – only administrators", dialog.JoinerText);
        Assert.Single(dialog.BuildPlan(scenario.Harness.Session, null).Objects[0].Changes);
    }

    [Fact]
    public async Task JoinSummary_ExplainsKb5020276_AndWhetherTheJoinerSucceeds()
    {
        var (scenario, dialog, helpdesk) = await OpenAsync("pl-PL");
        using var _dialog = dialog;
        var harness = scenario.Harness;

        Assert.StartsWith("Od wzmocnienia dołączania do domeny z KB5020276 (2022–2023)", dialog.JoinSummary, StringComparison.Ordinal);
        Assert.Contains(@"Właścicielem konta będzie CONTOSO\Administrator.", dialog.JoinSummary, StringComparison.Ordinal);
        Assert.StartsWith("Opcja „Przypisz to konto komputera jako komputer sprzed Windows 2000”", dialog.PreWindows2000Note, StringComparison.Ordinal);

        harness.Dialogs.PickedObjects.Enqueue([helpdesk]);
        await dialog.ChangeJoinerCommand.ExecuteAsync(null);

        // The window's account (Administrator) is a Domain Admin in the sample: the helpdesk can join.
        Assert.EndsWith(@"CONTOSO\Administrator jest administratorem: CONTOSO\GG_Helpdesk_Dołączanie może dołączyć komputer z tym kontem.", dialog.JoinSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JoinSummary_WhenTheOwnerIsNoAdministrator_SaysTheJoinFailsWithoutThePolicy()
    {
        var scenario = await ComputerScenario.CreateAsync();
        var harness = scenario.Harness;
        harness.Session.Info = harness.Session.Info with { BoundIdentity = @"CONTOSO\lukasz.wisniewski", BoundUserDn = scenario.Company.LukaszWisniewski };
        var helpdesk = harness.Builder.AddGroup("GG_Helpdesk", scenario.Company.GroupsOu);
        using var dialog = harness.Factory.CreateNewObject(ObjectKind.Computer, scenario.Company.ComputersOu);
        await dialog.InitializeAsync();
        harness.Dialogs.PickedObjects.Enqueue([helpdesk]);
        await dialog.ChangeJoinerCommand.ExecuteAsync(null);

        Assert.EndsWith(
            @"CONTOSO\lukasz.wisniewski is not an administrator: the join by CONTOSO\GG_Helpdesk fails unless the policy ""Domain controller: Allow computer account re-use during domain join"" trusts CONTOSO\lukasz.wisniewski.",
            dialog.JoinSummary,
            StringComparison.Ordinal);

        // Łukasz joining himself: he creates the account, so the join works.
        harness.Dialogs.PickedObjects.Enqueue([scenario.Company.LukaszWisniewski]);
        await dialog.ChangeJoinerCommand.ExecuteAsync(null);
        Assert.EndsWith(@"The owner of the account will be CONTOSO\lukasz.wisniewski.", dialog.JoinSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prestage_IsUndoneByTheRollback_WhichDeletesTheAccount()
    {
        var (scenario, dialog, helpdesk) = await OpenAsync();
        using var _dialog = dialog;
        var harness = scenario.Harness;
        dialog.Name = "WAW-PC204";
        harness.Dialogs.PickedObjects.Enqueue([helpdesk]);
        await dialog.ChangeJoinerCommand.ExecuteAsync(null);
        var plan = dialog.BuildPlan(harness.Session, null);

        var result = await harness.Executor.ExecuteAsync(harness.Session, plan, null, null, TestContext.Current.CancellationToken);
        var rollback = new RollbackPlanner().CreateRollbackPlan(result);
        await harness.Executor.ExecuteAsync(harness.Session, rollback, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(ObjectResultStatus.Succeeded, Assert.Single(result.Objects).Status);
        Assert.Contains(ChangeAction.Delete, Assert.Single(rollback.Objects).Changes.Select(c => c.Action));
        Assert.False(harness.Session.Exists(plan.Objects[0].DistinguishedName));
    }
}
