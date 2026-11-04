using AdMng.Core.Audit;
using AdMng.Core.Batch;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Testing;

namespace AdMng.Core.Tests.GroupPolicy;

/// <summary>The GPO management sample on the fake directory and a writable fake SYSVOL, with the planner and the executor.</summary>
public abstract class GroupPolicyTestBed
{
    protected static readonly BatchOptions Fast = new() { InitialRetryDelay = TimeSpan.FromMilliseconds(1) };

    protected GroupPolicyTestBed()
    {
        Builder = new FakeDirectoryBuilder(timeProvider: Time);
        Company = Builder.AddSampleCompany();
        Sample = Builder.AddGroupPolicyManagementSample(Company, Sysvol);
        Planner = new GroupPolicyPlanner(Reader, Time);
    }

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected ManualTimeProvider Time { get; } = new();

    protected FakeDirectoryBuilder Builder { get; }

    protected SampleCompany Company { get; }

    protected GroupPolicyManagementSample Sample { get; }

    protected FakeSysvol Sysvol { get; } = new();

    protected GroupPolicyReader Reader { get; } = new();

    protected GroupPolicyPlanner Planner { get; }

    protected InMemoryAuditLog Audit { get; } = new();

    protected FakeDirectorySession Session => Builder.Session;

    protected string GpoDn(Guid id) => GpLinkValue.GpoDn(id, Builder.DomainDn);

    protected Task<ISysvolWriteConnection> OpenSysvolAsync() => Sysvol.OpenForWriteAsync(Session, Ct);

    protected Task<BatchResult> ExecuteAsync(ChangePlan plan) =>
        new BatchExecutor(Audit, Time, null, Sysvol).ExecuteAsync(Session, plan, Fast, null, Ct);

    protected async Task<BatchResult> ExecuteOkAsync(ChangePlan plan)
    {
        Assert.Equal(0, plan.ErrorCount);
        var result = await ExecuteAsync(plan);
        var failures = result.Objects.SelectMany(o => o.Changes).Where(c => !c.Succeeded).Select(c => $"{c.Change.Action}: {c.ErrorMessage}").ToList();
        Assert.Equal([], failures);
        return result;
    }

    protected string? Value(string dn, string attribute) => Session.GetValues(dn, attribute).SingleOrDefault();

    protected string Script(ChangePlan plan) => new PowerShellExporter().Export(plan, Session.Info.Server);
}
