using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Management;

namespace AdMng.Core.Tests.GroupPolicy.Copy;

/// <summary>21.5 (W2): unlink everywhere, and the delete guarded by what the cleanup advisor saw.</summary>
public class UnlinkAndGuardedDeleteTests : CopySettingsTestBed
{
    [Fact]
    public async Task UnlinkAll_RemovesEveryLink_OneCompareAndSwapPerContainer_AndUndoes()
    {
        var gpo = AddGpo("Porządki – wszędzie");
        Builder.SetGroupPolicyLinks(Builder.DomainDn, [(Sample.ServerSecurity, 1), (Sample.DefaultDomainPolicy, 0), (gpo, 0)]);
        Builder.SetGroupPolicyLinks(Company.ItOu, [(gpo, 2)]);
        Session.SeedAttributes(Sample.SiteDn, new Dictionary<string, object?> { [Attr.GpLink] = Value(Sample.SiteDn, Attr.GpLink) + GpLinkValue.Format(gpo, Builder.DomainDn, 0) });
        var before = new[] { Builder.DomainDn, Company.ItOu, Sample.SiteDn }.Select(dn => Value(dn, Attr.GpLink)).ToList();

        var plan = await Planner.PlanUnlinkAllAsync(Session, gpo, Ct);

        Assert.Equal(3, plan.Objects.Count);
        Assert.All(plan.Objects, o => Assert.Equal(ChangeAction.Modify, Assert.Single(o.Changes).Action));
        Assert.Contains("Remove-GPLink", Script(plan), StringComparison.Ordinal);
        var result = await ExecuteOkAsync(plan);
        Assert.False(GpLinkValue.Parse(Value(Builder.DomainDn, Attr.GpLink), Builder.DomainDn).Contains(gpo));
        Assert.True(GpLinkValue.Parse(Value(Builder.DomainDn, Attr.GpLink), Builder.DomainDn).Contains(Sample.ServerSecurity));
        Assert.Null(Value(Company.ItOu, Attr.GpLink));
        Assert.False(GpLinkValue.Parse(Value(Sample.SiteDn, Attr.GpLink), Builder.DomainDn).Contains(gpo));

        await ExecuteOkAsync(new RollbackPlanner().CreateRollbackPlan(result));
        Assert.Equal(before, new[] { Builder.DomainDn, Company.ItOu, Sample.SiteDn }.Select(dn => Value(dn, Attr.GpLink)));
    }

    [Fact]
    public async Task UnlinkAll_OfAnUnlinkedGpo_PlansNothing()
    {
        var plan = await Planner.PlanUnlinkAllAsync(Session, AddGpo("Porządki – bez powiązań"), Ct);

        Assert.Empty(plan.Objects);
    }

    [Fact]
    public async Task UnlinkAll_WithOtherDomainsInTheForest_Warns()
    {
        AddOtherDomain();
        var plan = await Planner.PlanUnlinkAllAsync(Session, Sample.WorkstationPasswords, Ct);

        Assert.Contains(IssuesOf(plan), i => i.Code == IssueCodes.GpoLinkedElsewhere);
    }

    [Fact]
    public async Task GuardedDelete_AsAnalysed_IsTheNormalDelete()
    {
        var gpo = Sample.WorkstationPasswords;
        var expected = new GpoExpectedState(DirectoryVersion(gpo), [Company.ComputersOu.ToUpperInvariant()]);

        var plan = await Planner.PlanDeleteAsync(Session, gpo, expected, Ct);

        Assert.Equal(0, plan.ErrorCount);
        await ExecuteOkAsync(plan);
        Assert.False(Session.Exists(GpoDn(gpo)));
    }

    [Fact]
    public async Task GuardedDelete_AfterTheVersionMoved_IsRefused()
    {
        var gpo = Sample.WorkstationPasswords;
        var expected = new GpoExpectedState(DirectoryVersion(gpo), [Company.ComputersOu]);
        Session.SetValues(GpoDn(gpo), "versionNumber", "5");

        var plan = await Planner.PlanDeleteAsync(Session, gpo, expected, Ct);

        Assert.True(plan.IsBlocked);
        Assert.Contains(IssuesOf(plan), i => i.Code == IssueCodes.GpoChangedSinceAnalysis && i.Severity == IssueSeverity.Error && i.Arguments.Single() == "Stacje robocze – Zasady haseł");
        Assert.Equal(0, plan.ExecutableCount);
    }

    [Fact]
    public async Task GuardedDelete_AfterANewLink_IsRefused()
    {
        var gpo = AddGpo("Porządki – nowe powiązanie");
        var expected = new GpoExpectedState(DirectoryVersion(gpo), []);
        Builder.SetGroupPolicyLinks(Company.SalesOu, [(gpo, 0)]);

        var plan = await Planner.PlanDeleteAsync(Session, gpo, expected, Ct);

        Assert.Contains(IssuesOf(plan), i => i.Code == IssueCodes.GpoChangedSinceAnalysis);
        var result = await ExecuteAsync(plan);
        Assert.True(Session.Exists(GpoDn(gpo)));
        Assert.True(GpLinkValue.Parse(Value(Company.SalesOu, Attr.GpLink), Builder.DomainDn).Contains(gpo));
        Assert.DoesNotContain(result.Objects.SelectMany(o => o.Changes), c => c.Succeeded);
    }
}
