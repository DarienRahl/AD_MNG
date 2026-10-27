using AdMng.Core.ActiveDirectory;
using AdMng.Core.Audit;
using AdMng.Core.Batch;
using AdMng.Core.Batch.Planning;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Security;
using AdMng.Directory.IntegrationTests.Infrastructure;

namespace AdMng.Directory.IntegrationTests.Integration;

/// <summary>
/// Computers (K1) against the Samba lab. The lab is shared: every object lives in an OU <c>K1T-…</c> below this run's
/// OU (removed with it) – the seeded lab computers are only read.
/// </summary>
public sealed class ComputerLabTests(LabFixture lab)
{
    private static readonly BatchOptions Options = new() { InitialRetryDelay = TimeSpan.FromMilliseconds(50) };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ModifySecurity_AddsAndRemovesAcesWithTheSdFlagsControl_AndKeepsTheOwner()
    {
        LabSettings.RequireLab();
        var admin = lab.Admin;
        var ou = await lab.CreateOuAsync(TestNames.Unique("K1T-ACL"));
        var computer = await CreateComputerAsync(ou, "K1TACL" + Suffix());
        var trustee = await lab.CreateUserAsync(ou, "Łukasz Dołączający");
        var trusteeSid = (await admin.GetAsync(trustee, [Attr.ObjectSid], Ct))!.ObjectSid!;
        var ownerBefore = await OwnerAsync(computer);
        var wanted = new AclChange(
            [
                AceSpec.Allow(trusteeSid, 0x100, ExtendedRightIds.ResetPassword),
                AceSpec.Allow(trusteeSid, 0x30, ExtendedRightIds.AccountRestrictions),
                AceSpec.Allow(trusteeSid, 0x08, ExtendedRightIds.ValidatedWriteDnsHostName),
                AceSpec.Allow(trusteeSid, 0x08, ExtendedRightIds.ValidatedWriteServicePrincipalName),
            ],
            []);

        var step = await AclPlanning.PlanAsync(admin, computer, wanted, Ct);
        Assert.NotNull(step);
        Assert.Equal(4, step!.Acl!.Added.Count);
        var plan = Plan(admin, computer, step);
        var audit = new InMemoryAuditLog();
        var result = await new BatchExecutor(audit, TimeProvider.System).ExecuteAsync(admin, plan, Options, null, Ct);

        Assert.Equal(ObjectResultStatus.Succeeded, Assert.Single(result.Objects).Status);
        var aces = await ExplicitAcesAsync(computer);
        Assert.All(wanted.Added, ace => Assert.Contains(aces, a => a.SameAce(ace)));
        Assert.Equal(ownerBefore, await OwnerAsync(computer));
        Assert.Null(await AclPlanning.PlanAsync(admin, computer, wanted, Ct));

        // Rollback removes exactly the four ACEs.
        var rollback = new RollbackPlanner().CreateRollbackPlan(result);
        var undone = await new BatchExecutor(audit, TimeProvider.System).ExecuteAsync(admin, rollback, Options, null, Ct);
        Assert.Equal(1, undone.SucceededCount);
        var after = await ExplicitAcesAsync(computer);
        Assert.DoesNotContain(after, a => string.Equals(a.TrusteeSid, trusteeSid, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ownerBefore, await OwnerAsync(computer));
    }

    internal static string Suffix() => Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    internal static ChangePlan Plan(IDirectorySession session, string dn, params PlannedChange[] steps) => new()
    {
        Title = "K1 lab",
        Server = session.Info.Server,
        CreatedAt = DateTimeOffset.UtcNow,
        Objects = [new ObjectPlan { DistinguishedName = dn, DisplayName = DistinguishedName.Parse(dn).Name, Changes = steps }],
    };

    /// <summary>A workstation account (sAMAccountName NAME$, WORKSTATION_TRUST_ACCOUNT) in <paramref name="parentDn"/>.</summary>
    internal async Task<string> CreateComputerAsync(string parentDn, string name, IReadOnlyDictionary<string, string>? extra = null)
    {
        var dn = DistinguishedName.Parse(parentDn).Child("CN", name).ToString();
        var attributes = new Dictionary<string, IReadOnlyList<object>>
        {
            [Attr.ObjectClass] = ["top", "person", "organizationalPerson", ObjectClasses.User, ObjectClasses.Computer],
            [Attr.SamAccountName] = [name.ToUpperInvariant() + "$"],
            [Attr.UserAccountControl] = [((int)(UserAccountControl.WorkstationTrustAccount | UserAccountControl.AccountDisable)).ToString(System.Globalization.CultureInfo.InvariantCulture)],
        };
        foreach (var (attribute, value) in extra ?? new Dictionary<string, string>())
        {
            attributes[attribute] = [value];
        }

        await lab.Admin.AddAsync(dn, attributes, Ct);
        return dn;
    }

    private async Task<IReadOnlyList<AceSpec>> ExplicitAcesAsync(string dn)
    {
        var descriptor = (await lab.Admin.GetAsync(dn, [Attr.NtSecurityDescriptor], Ct))!.GetBytes(Attr.NtSecurityDescriptor);
        Assert.NotNull(descriptor);
        Assert.True(DaclEditor.TryReadExplicit(descriptor, out var aces));
        return aces;
    }

    private async Task<string?> OwnerAsync(string dn)
    {
        var descriptor = (await lab.Admin.GetAsync(dn, [Attr.NtSecurityDescriptor], Ct))!.GetBytes(Attr.NtSecurityDescriptor);
        Assert.True(SecurityDescriptorSummary.TryParse(descriptor, out var summary));
        return summary!.Owner;
    }
}
